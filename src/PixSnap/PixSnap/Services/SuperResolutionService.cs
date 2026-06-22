using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace PixSnap.Services;

/// <summary>
/// 基于 RealESRGAN-x4plus ONNX 模型的图像超分辨率服务。
/// 采用分块推理策略，避免大图显存溢出，并通过重叠区域消除接缝。
/// 模型文件路径：<程序目录>/onnx/realesrgan-x4plus.onnx
/// </summary>
public static class SuperResolutionService
{
    private static readonly string ModelPath =
        Path.Combine(AppContext.BaseDirectory, "onnx", "realesrgan-x4plus.onnx");

    // realesrgan-x4plus.onnx 固定输入 256×256；分块尺寸必须与模型一致
    private const int ModelTileSize = 256;
    private const int TilePad = 16;
    private const int ScaleFactor = 4;

    // 输出位图最大像素数（BGRA 4字节/像素，128MP ≈ 512 MB，安全低于 SKBitmap int 上限）
    private const long MaxOutputPixels = 128_000_000;

    /// <summary>异步执行 4x 超分辨率。按 TileSize 分块推理后拼合成完整的 4 倍放大图。</summary>
    public static async Task<BitmapSource?> RunAsync(
        BitmapSource originalImage,
        IProgress<(double Value, string Text)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var frozen = ImageIOService.CreateFrozenSnapshot(originalImage);

        var export = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((0.05, "正在加载超分模型..."));
            if (!File.Exists(ModelPath))
            {
                Log.Error("RealESRGAN 模型文件不存在: {ModelPath}", ModelPath);
                throw new FileNotFoundException(string.Format("未找到 ONNX 模型：{0}", ModelPath));
            }

            Log.Information("开始超分辨率: 图像 {W}×{H}", frozen.PixelWidth, frozen.PixelHeight);

            using var srcBitmap = SkiaInteropHelper.BitmapSourceToSKBitmap(frozen);
            int srcW = srcBitmap.Width;
            int srcH = srcBitmap.Height;

            // 若 4x 输出超过安全阈值，先缩小源图再超分
            long outputPixels = (long)srcW * ScaleFactor * srcH * ScaleFactor;
            using var downscaled = outputPixels > MaxOutputPixels
                ? DownscaleSource(srcBitmap, srcW, srcH, outputPixels, progress, out srcW, out srcH)
                : null;

            var actualSource = downscaled ?? srcBitmap;

            progress?.Report((0.20, "正在初始化推理引擎..."));
            var session = OnnxSessionFactory.GetOrCreateSession(ModelPath, out var providerName);
            progress?.Report((0.28, FormatProviderMessage(providerName)));
            var inputName = session.InputMetadata.Keys.First();
            var tile = GetTileConfig(session, inputName);

            progress?.Report((0.35, "正在执行超分推理..."));
            using var outputBitmap = RunTiled(session, providerName, tile, inputName, actualSource, srcW, srcH, progress, cancellationToken);

            progress?.Report((0.90, "正在生成超分结果..."));
            return (SkiaInteropHelper.CopyPixels(outputBitmap), outputBitmap.Width, outputBitmap.Height);
        }, cancellationToken);

        return SkiaInteropHelper.CreateFrozenBitmapFromBgra(export.Item1, export.Item2, export.Item3);
    }

    private static SKBitmap DownscaleSource(
        SKBitmap srcBitmap, int srcW, int srcH, long outputPixels,
        IProgress<(double Value, string Text)>? progress,
        out int newW, out int newH)
    {
        double ratio = Math.Sqrt((double)MaxOutputPixels / outputPixels);
        newW = Math.Max(1, (int)(srcW * ratio));
        newH = Math.Max(1, (int)(srcH * ratio));
        Log.Information("源图过大，预缩放: {SrcW}×{SrcH} → {NewW}×{NewH}", srcW, srcH, newW, newH);
        progress?.Report((0.12, string.Format("源图过大，正在预缩放至 {0}×{1}...", newW, newH)));
        var downscaled = new SKBitmap(new SKImageInfo(newW, newH, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        srcBitmap.ScalePixels(downscaled, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return downscaled;
    }

    private readonly record struct TileConfig(int Size, int Pad, int Stride);

    private static TileConfig GetTileConfig(InferenceSession session, string inputName)
    {
        int tileSize = ModelTileSize;
        int tilePad = TilePad;

        if (session.InputMetadata.TryGetValue(inputName, out var meta)
            && meta.Dimensions is { Length: >= 4 } dims
            && dims[2] > 0
            && dims[3] > 0)
        {
            tileSize = (int)Math.Max(dims[2], dims[3]);
            tilePad = Math.Clamp(TilePad, 0, tileSize / 4);
            Log.Debug("超分模型输入尺寸: {H}×{W}, 分块 {TileSize}px", dims[2], dims[3], tileSize);
        }

        return new TileConfig(tileSize, tilePad, tileSize - tilePad * 2);
    }

    private static string FormatProviderMessage(string providerName)
    {
        if (providerName.StartsWith("DirectML", StringComparison.OrdinalIgnoreCase))
            return string.Format("GPU 加速：{0}", providerName);

        return string.Format("CPU 推理（较慢）：{0}。可在日志中查看 DirectML 失败原因", providerName);
    }

    /// <summary>描述单个分块在源图和张量中的几何位置。</summary>
    private readonly record struct TileRegion(
        int SrcX, int SrcY,             // tile 有效区域在源图中的起始坐标
        int ValidW, int ValidH,         // tile 有效区域尺寸（不含 pad）
        int ExtractX, int ExtractY,     // 含 pad 的实际抽取起始坐标
        int ExtractW, int ExtractH,     // 含 pad 的实际抽取尺寸
        int PadLeft, int PadTop);       // 左/上侧 pad 宽度（用于写回时跳过重叠区）

    /// <summary>
    /// 计算第 (tx, ty) 块的抽取 / 写回几何信息。
    /// 末块反向对齐：最后一个 tile 从 (src - TileStride) 处开始，
    /// 确保其拥有完整的右/下邻居像素上下文，避免边缘质量下降。
    /// </summary>
    private static TileRegion ComputeTileRegion(int tx, int ty, int srcW, int srcH, TileConfig tile)
    {
        int tileStride = tile.Stride;
        int tilePad = tile.Pad;
        int srcX   = srcW > tileStride ? Math.Min(tx * tileStride, srcW - tileStride) : 0;
        int srcY   = srcH > tileStride ? Math.Min(ty * tileStride, srcH - tileStride) : 0;
        int validW = Math.Min(tileStride, srcW - srcX);
        int validH = Math.Min(tileStride, srcH - srcY);

        int padLeft   = Math.Min(srcX,                 tilePad);
        int padTop    = Math.Min(srcY,                 tilePad);
        int padRight  = Math.Min(srcW - srcX - validW, tilePad);
        int padBottom = Math.Min(srcH - srcY - validH, tilePad);

        return new TileRegion(
            srcX, srcY, validW, validH,
            srcX - padLeft, srcY - padTop,
            padLeft + validW + padRight, padTop + validH + padBottom,
            padLeft, padTop);
    }

    /// <summary>分块推理：将源图按 TileStride 步长切成若干块，逐块 4x 超分后写回结果位图。</summary>
    private static SKBitmap RunTiled(
        InferenceSession session,
        string providerName,
        TileConfig tile,
        string inputName,
        SKBitmap source,
        int srcW,
        int srcH,
        IProgress<(double Value, string Text)>? progress,
        CancellationToken cancellationToken = default)
    {
        var result = new SKBitmap(new SKImageInfo(srcW * ScaleFactor, srcH * ScaleFactor, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        try
        {
            int tileStride = tile.Stride;
            int tilesX = (int)Math.Ceiling((double)srcW / tileStride);
            int tilesY = (int)Math.Ceiling((double)srcH / tileStride);
            int totalTiles = tilesX * tilesY;
            int tileIndex = 0;
            int outW = srcW * ScaleFactor;
            int outH = srcH * ScaleFactor;
            Log.Debug("分块超分: {TileSize}px, {TilesX}×{TilesY} = {Total} 块, {Provider}",
                tile.Size, tilesX, tilesY, totalTiles, providerName);

            var inputs = new List<NamedOnnxValue>(1);

            for (int ty = 0; ty < tilesY; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    tileIndex++;
                    progress?.Report((0.35 + 0.45 * tileIndex / totalTiles, string.Format("正在分块超分 ({0}/{1})...", tileIndex, totalTiles)));

                    // 若上一块触发 DirectML→CPU 回退，从缓存取最新 Session
                    session = OnnxSessionFactory.GetOrCreateSession(ModelPath, out providerName);

                    var tileRegion = ComputeTileRegion(tx, ty, srcW, srcH, tile);

                    var inputTensor = ExtractTileToTensor(
                        source,
                        tileRegion.ExtractX,
                        tileRegion.ExtractY,
                        tileRegion.ExtractW,
                        tileRegion.ExtractH,
                        tile.Size,
                        tile.Size);
                    inputs.Clear();
                    inputs.Add(NamedOnnxValue.CreateFromTensor(inputName, inputTensor));

                    RunTileInference(session, providerName, inputs, outputTensor =>
                    {
                        WriteTensorToBitmap(
                            outputTensor, result,
                            destX:         tileRegion.SrcX    * ScaleFactor,
                            destY:         tileRegion.SrcY    * ScaleFactor,
                            tensorOffsetX: tileRegion.PadLeft  * ScaleFactor,
                            tensorOffsetY: tileRegion.PadTop   * ScaleFactor,
                            copyW:         tileRegion.ValidW   * ScaleFactor,
                            copyH:         tileRegion.ValidH   * ScaleFactor,
                            fullW:         outW,
                            fullH:         outH);
                    });
                }
            }
        }
        catch
        {
            result.Dispose();
            throw;
        }

        return result;
    }

    /// <summary>执行单块推理，DirectML 失败时自动回退到 CPU。</summary>
    private static void RunTileInference(
        InferenceSession session,
        string providerName,
        List<NamedOnnxValue> inputs,
        Action<Tensor<float>> consumeOutput)
    {
        using var run = OnnxInferenceHelper.RunWithCpuFallback(session, providerName, ModelPath, inputs);
        consumeOutput(run.First().AsTensor<float>());
    }

    /// <summary>从源位图指定区域抽取像素并转换为 [1,3,H,W] RGB float32 张量（归一化到 0–1）。</summary>
    private static DenseTensor<float> ExtractTileToTensor(
        SKBitmap source,
        int startX,
        int startY,
        int validW,
        int validH,
        int fixedW,
        int fixedH)
    {
        var tensor = new DenseTensor<float>([1, 3, fixedH, fixedW]);
        int rowBytes = source.RowBytes;

        // 直接操作 tensor 底层 Span，避免多维索引器开销
        // 布局 [1, 3, fixedH, fixedW]：channel stride = fixedH * fixedW
        var buf = tensor.Buffer.Span;
        int chStride = fixedH * fixedW;
        int rOff = 0;               // channel 0 (R)
        int gOff = chStride;        // channel 1 (G)
        int bOff = chStride * 2;    // channel 2 (B)

        unsafe
        {
            var ptr = (byte*)source.GetPixels();
            for (int y = 0; y < validH; y++)
            {
                int pixelBase = (startY + y) * rowBytes + startX * 4;
                int spanBase  = y * fixedW;
                for (int x = 0; x < validW; x++)
                {
                    int idx = pixelBase + x * 4;
                    int si  = spanBase + x;
                    buf[rOff + si] = ptr[idx + 2] / 255f;
                    buf[gOff + si] = ptr[idx + 1] / 255f;
                    buf[bOff + si] = ptr[idx]     / 255f;
                }
            }
        }

        return tensor;
    }

    /// <summary>将推理输出张量的有效区域写入目标 SKBitmap。跳过两侧的 pad 重叠区，只拷贝中心像素。</summary>
    private static void WriteTensorToBitmap(
        Tensor<float> tensor,
        SKBitmap target,
        int destX,
        int destY,
        int tensorOffsetX,   // 张量中跳过的 X 偏移（pad 区域）
        int tensorOffsetY,   // 张量中跳过的 Y 偏移（pad 区域）
        int copyW,           // 实际写入的宽度（已缩放）
        int copyH,           // 实际写入的高度（已缩放）
        int fullW,
        int fullH)
    {
        int rowBytes = target.RowBytes;

        // 提前裁剪循环边界，避免内循环逐像素判断
        int effH = Math.Min(copyH, fullH - destY);
        int effW = Math.Min(copyW, fullW - destX);
        if (effH <= 0 || effW <= 0) return;

        // 直接操作 tensor 底层 Span，布局 [1, 3, H, W]
        var dims = tensor.Dimensions;
        int tensorW  = dims[3];
        int chStride = dims[2] * tensorW;    // H * W
        ReadOnlySpan<float> buf = tensor is DenseTensor<float> dense
            ? dense.Buffer.Span
            : tensor.ToArray();              // 理论上始终走 DenseTensor 分支
        int rOff = 0;
        int gOff = chStride;
        int bOff = chStride * 2;

        unsafe
        {
            var ptr = (byte*)target.GetPixels();

            for (int y = 0; y < effH; y++)
            {
                int tensorRowBase = (tensorOffsetY + y) * tensorW + tensorOffsetX;
                int pixelRowBase  = (destY + y) * rowBytes + destX * 4;

                for (int x = 0; x < effW; x++)
                {
                    int ti  = tensorRowBase + x;
                    float r = Math.Clamp(buf[rOff + ti], 0f, 1f);
                    float g = Math.Clamp(buf[gOff + ti], 0f, 1f);
                    float b = Math.Clamp(buf[bOff + ti], 0f, 1f);

                    int idx = pixelRowBase + x * 4;
                    ptr[idx]     = (byte)(b * 255f + 0.5f);
                    ptr[idx + 1] = (byte)(g * 255f + 0.5f);
                    ptr[idx + 2] = (byte)(r * 255f + 0.5f);
                    ptr[idx + 3] = 255;
                }
            }
        }
    }
}
