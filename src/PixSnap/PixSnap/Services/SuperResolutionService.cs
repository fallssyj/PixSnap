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

    private const int TileSize   = 256;
    // 分块重叠区域：每块向四周多取 TilePad 个真实邻居像素，
    // 推理后只写回中心有效区域，从而消除分块接缝。
    private const int TilePad    = 16;
    private const int TileStride = TileSize - TilePad * 2;   // 224
    private const int ScaleFactor = 4;

    // 输出位图最大像素数（BGRA 4字节/像素，128MP ≈ 512 MB，安全低于 SKBitmap int 上限）
    private const long MaxOutputPixels = 128_000_000;

    /// <summary>异步执行 4x 超分辨率。按 TileSize 分块推理后拼合成完整的 4 倍放大图。</summary>
    public static async Task<BitmapSource?> RunAsync(
        BitmapSource originalImage,
        IProgress<(double Value, string Text)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((0.05, "正在加载超分模型..."));
            if (!File.Exists(ModelPath))
            {
                Log.Error("RealESRGAN 模型文件不存在: {ModelPath}", ModelPath);
                throw new FileNotFoundException(string.Format("未找到 ONNX 模型：{0}", ModelPath));
            }

            Log.Information("开始超分辨率: 图像 {W}×{H}", originalImage.PixelWidth, originalImage.PixelHeight);

            using var srcBitmap = SkiaInteropHelper.BitmapSourceToSKBitmap(originalImage);
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
            progress?.Report((0.28, string.Format("当前推理设备：{0}", providerName)));
            var inputName = session.InputMetadata.Keys.First();

            progress?.Report((0.35, "正在执行超分推理..."));
            using var outputBitmap = RunTiled(session, inputName, actualSource, srcW, srcH, progress, cancellationToken);

            progress?.Report((0.90, "正在生成超分结果..."));
            var result = SkiaInteropHelper.SKBitmapToBitmapSource(outputBitmap);
            result.Freeze();

            progress?.Report((1.0, "超分辨率完成"));
            Log.Information("超分辨率完成: 结果图像 {W}×{H}", result.PixelWidth, result.PixelHeight);
            return result;
        }).ConfigureAwait(false);
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
    private static TileRegion ComputeTileRegion(int tx, int ty, int srcW, int srcH)
    {
        int srcX   = srcW > TileStride ? Math.Min(tx * TileStride, srcW - TileStride) : 0;
        int srcY   = srcH > TileStride ? Math.Min(ty * TileStride, srcH - TileStride) : 0;
        int validW = Math.Min(TileStride, srcW - srcX);
        int validH = Math.Min(TileStride, srcH - srcY);

        int padLeft   = Math.Min(srcX,                 TilePad);
        int padTop    = Math.Min(srcY,                 TilePad);
        int padRight  = Math.Min(srcW - srcX - validW, TilePad);
        int padBottom = Math.Min(srcH - srcY - validH, TilePad);

        return new TileRegion(
            srcX, srcY, validW, validH,
            srcX - padLeft, srcY - padTop,
            padLeft + validW + padRight, padTop + validH + padBottom,
            padLeft, padTop);
    }

    /// <summary>分块推理：将源图按 TileStride 步长切成若干 TileSize×TileSize 块，逐块 4x 超分后写回结果位图。</summary>
    private static SKBitmap RunTiled(
        InferenceSession session,
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
            int tilesX = (int)Math.Ceiling((double)srcW / TileStride);
            int tilesY = (int)Math.Ceiling((double)srcH / TileStride);
            int totalTiles = tilesX * tilesY;
            int tileIndex = 0;
            int outW = srcW * ScaleFactor;
            int outH = srcH * ScaleFactor;
            Log.Debug("分块超分: {TilesX}×{TilesY} = {Total} 块", tilesX, tilesY, totalTiles);

            var inputs = new List<NamedOnnxValue>(1); // 复用，减少每块 GC 分配

            for (int ty = 0; ty < tilesY; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    tileIndex++;
                    progress?.Report((0.35 + 0.45 * tileIndex / totalTiles, string.Format("正在分块超分 ({0}/{1})...", tileIndex, totalTiles)));

                    var tile = ComputeTileRegion(tx, ty, srcW, srcH);

                    var inputTensor = ExtractTileToTensor(source, tile.ExtractX, tile.ExtractY, tile.ExtractW, tile.ExtractH, TileSize, TileSize);
                    inputs.Clear();
                    inputs.Add(NamedOnnxValue.CreateFromTensor(inputName, inputTensor));

                    using var outputs = RunTileInference(session, inputs, tileIndex, totalTiles);
                    var outputTensor = outputs.First().AsTensor<float>();

                    WriteTensorToBitmap(
                        outputTensor, result,
                        destX:         tile.SrcX    * ScaleFactor,
                        destY:         tile.SrcY    * ScaleFactor,
                        tensorOffsetX: tile.PadLeft  * ScaleFactor,
                        tensorOffsetY: tile.PadTop   * ScaleFactor,
                        copyW:         tile.ValidW   * ScaleFactor,
                        copyH:         tile.ValidH   * ScaleFactor,
                        fullW:         outW,
                        fullH:         outH);
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
    private static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunTileInference(
        InferenceSession session, List<NamedOnnxValue> inputs, int tileIndex, int totalTiles)
    {
        try
        {
            return session.Run(inputs);
        }
        catch (OnnxRuntimeException ex)
        {
            Log.Warning(ex, "超分分块 {Index}/{Total} DirectML 推理失败，回退到 CPU", tileIndex, totalTiles);
            using var cpuSession = OnnxSessionFactory.CreateCpuSession(ModelPath);
            return cpuSession.Run(inputs);
        }
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
