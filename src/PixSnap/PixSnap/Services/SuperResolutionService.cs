using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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
    // 分块重叠区寽：每块向四周多取 TilePad 个真实邻居像素，
    // 推理后只写回中心有效区域，从而消除分块接缝。
    private const int TilePad    = 16;
    private const int TileStride = TileSize - TilePad * 2;   // 224
    private const int ScaleFactor = 4;

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
                throw new FileNotFoundException($"未找到 ONNX 模型：{ModelPath}");
            }

            Log.Information("开始超分辨率: 图像 {W}×{H}", originalImage.PixelWidth, originalImage.PixelHeight);

            using var srcBitmap = SkiaInteropHelper.BitmapSourceToSKBitmap(originalImage);
            int srcW = srcBitmap.Width;
            int srcH = srcBitmap.Height;

            progress?.Report((0.20, "正在初始化推理引擎..."));
            var session = OnnxSessionFactory.GetOrCreateSession(ModelPath, out var providerName);
            progress?.Report((0.28, $"当前推理设备：{providerName}"));
            var inputName = session.InputMetadata.Keys.First();

            progress?.Report((0.35, "正在执行超分推理..."));
            using var outputBitmap = RunTiled(session, inputName, srcBitmap, srcW, srcH, progress, cancellationToken);

            progress?.Report((0.90, "正在生成超分结果..."));
            var result = SkiaInteropHelper.SKBitmapToBitmapSource(outputBitmap);
            result.Freeze();

            progress?.Report((1.0, "超分辨率完成"));
            Log.Information("超分辨率完成: 结果图像 {W}×{H}", result.PixelWidth, result.PixelHeight);
            return result;
        });
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
        Log.Debug("分块超分: {TilesX}×{TilesY} = {Total} 块", tilesX, tilesY, totalTiles);

        for (int ty = 0; ty < tilesY; ty++)
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tileIndex++;
                double phase = 0.35 + 0.45 * tileIndex / totalTiles;
                progress?.Report((phase, $"正在分块超分 ({tileIndex}/{totalTiles})..."));

                // 末块反向对齐：最后一个 tile 从 (src - TileStride) 处开始，
                // 确保其拥有完整的右/下邻居像素上下文，避免边缘质量下降。
                int srcX   = (srcW > TileStride) ? Math.Min(tx * TileStride, srcW - TileStride) : 0;
                int srcY   = (srcH > TileStride) ? Math.Min(ty * TileStride, srcH - TileStride) : 0;
                int validW = Math.Min(TileStride, srcW - srcX);
                int validH = Math.Min(TileStride, srcH - srcY);

                // 向四周扩展 TilePad 像素的重叠层（边界处才将被零填充）
                int padLeft   = Math.Min(srcX,                       TilePad);
                int padTop    = Math.Min(srcY,                       TilePad);
                int padRight  = Math.Min(srcW - srcX - validW,       TilePad);
                int padBottom = Math.Min(srcH - srcY - validH,       TilePad);

                // 实际从源图抽取的区域（包含重叠层）
                int extractX = srcX   - padLeft;
                int extractY = srcY   - padTop;
                int extractW = padLeft + validW + padRight;
                int extractH = padTop  + validH + padBottom;

                var inputTensor = ExtractTileToTensor(source, extractX, extractY, extractW, extractH, TileSize, TileSize);

                IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs;
                try
                {
                    outputs = session.Run(new List<NamedOnnxValue>
                    {
                        NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
                    });
                }
                catch (OnnxRuntimeException ex)
                {
                    Log.Warning(ex, "超分分块 {Index}/{Total} DirectML 推理失败，回退到 CPU", tileIndex, totalTiles);
                    using var cpuSession = OnnxSessionFactory.CreateCpuSession(ModelPath);
                    outputs = cpuSession.Run(new List<NamedOnnxValue>
                    {
                        NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
                    });
                }

                using (outputs)
                {
                    var outputTensor = outputs.First().AsTensor<float>();

                    // 推理输出中跳过重叠层，只写回有效中心区域
                    WriteTensorToBitmap(
                        outputTensor, result,
                        destX:         srcX   * ScaleFactor,
                        destY:         srcY   * ScaleFactor,
                        tensorOffsetX: padLeft * ScaleFactor,
                        tensorOffsetY: padTop  * ScaleFactor,
                        copyW:         validW  * ScaleFactor,
                        copyH:         validH  * ScaleFactor,
                        fullW:         srcW   * ScaleFactor,
                        fullH:         srcH   * ScaleFactor);
                }
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
        var tensor = new DenseTensor<float>(new[] { 1, 3, fixedH, fixedW });
        int rowBytes = source.RowBytes;

        unsafe
        {
            var ptr = (byte*)source.GetPixels();
            for (int y = 0; y < validH; y++)
            {
                int srcY = startY + y;
                for (int x = 0; x < validW; x++)
                {
                    int srcX = startX + x;
                    int idx = srcY * rowBytes + srcX * 4;
                    tensor[0, 0, y, x] = ptr[idx + 2] / 255f;
                    tensor[0, 1, y, x] = ptr[idx + 1] / 255f;
                    tensor[0, 2, y, x] = ptr[idx] / 255f;
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

        unsafe
        {
            var ptr = (byte*)target.GetPixels();

            for (int y = 0; y < copyH; y++)
            {
                int tensorY = tensorOffsetY + y;
                int finalY  = destY + y;
                if (finalY >= fullH) break;

                for (int x = 0; x < copyW; x++)
                {
                    int tensorX = tensorOffsetX + x;
                    int finalX  = destX + x;
                    if (finalX >= fullW) break;

                    float r = Math.Clamp(tensor[0, 0, tensorY, tensorX], 0f, 1f);
                    float g = Math.Clamp(tensor[0, 1, tensorY, tensorX], 0f, 1f);
                    float b = Math.Clamp(tensor[0, 2, tensorY, tensorX], 0f, 1f);

                    int idx = finalY * rowBytes + finalX * 4;
                    ptr[idx]     = (byte)(b * 255f + 0.5f);
                    ptr[idx + 1] = (byte)(g * 255f + 0.5f);
                    ptr[idx + 2] = (byte)(r * 255f + 0.5f);
                    ptr[idx + 3] = 255;
                }
            }
        }
    }



}
