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
/// 基于 RMBG-1.4 ONNX 模型的背景去除服务。
/// 输入原图，输出带 Alpha 通透背景的 BitmapSource。
/// 模型文件路径：<程序目录>/onnx/rmbg-1.4.onnx
/// </summary>
public static class BackgroundRemovalService
{
    private static readonly string ModelPath =
        Path.Combine(AppContext.BaseDirectory, "onnx", "rmbg-1.4.onnx");

    /// <summary>异步执行背景去除。将原图缩放至模型输入尺寸，推理得到前景掩码，再还原到原始分辨率并合成透明背景。</summary>
    public static async Task<BitmapSource?> RunAsync(
        BitmapSource originalImage,
        IProgress<(double Value, string Text)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((0.05, "正在加载去背景模型..."));
            if (!File.Exists(ModelPath))
            {
                Log.Error("RMBG 模型文件不存在: {ModelPath}", ModelPath);
                throw new FileNotFoundException($"未找到 ONNX 模型：{ModelPath}");
            }

            Log.Information("开始去除背景: 图像 {W}×{H}", originalImage.PixelWidth, originalImage.PixelHeight);

            using var srcBitmap = SkiaInteropHelper.BitmapSourceToSKBitmap(originalImage);
            int origW = srcBitmap.Width;
            int origH = srcBitmap.Height;

            progress?.Report((0.20, "正在初始化推理引擎..."));
            var session = OnnxSessionFactory.GetOrCreateSession(ModelPath, out var providerName);
            progress?.Report((0.28, $"当前推理设备：{providerName}"));
            var inputName = session.InputMetadata.Keys.First();
            var inputMeta = session.InputMetadata[inputName];
            var inputDims = inputMeta.Dimensions;

            int modelH = ResolveDim(inputDims, 2, 1024);
            int modelW = ResolveDim(inputDims, 3, 1024);

            using var scaled = new SKBitmap(new SKImageInfo(modelW, modelH, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            srcBitmap.ScalePixels(scaled, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

            progress?.Report((0.35, "正在构建输入张量..."));
            var imageTensor = BitmapToRgbTensor(scaled, modelW, modelH);

            progress?.Report((0.55, "正在执行去背景推理..."));
            cancellationToken.ThrowIfCancellationRequested();
            using var outputs = session.Run(new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, imageTensor)
            });

            var outputTensor = outputs.First().AsTensor<float>();
            using var maskBitmap = OutputToMaskBitmap(outputTensor, modelW, modelH);

            progress?.Report((0.78, "正在还原掩码分辨率..."));
            using var maskAtOriginal = new SKBitmap(new SKImageInfo(origW, origH, SKColorType.Bgra8888, SKAlphaType.Premul));
            maskBitmap.ScalePixels(maskAtOriginal, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

            progress?.Report((0.90, "正在合成透明背景..."));
            cancellationToken.ThrowIfCancellationRequested();
            using var composed = ApplyMaskAsAlpha(srcBitmap, maskAtOriginal);
            var result = SkiaInteropHelper.SKBitmapToBitmapSource(composed);
            result.Freeze();

            progress?.Report((1.0, "去除背景完成"));
            Log.Information("去除背景完成");
            return result;
        });
    }

    /// <summary>解析模型输入维度，动态值或无效值时使用回退默认值。</summary>
    private static int ResolveDim(IReadOnlyList<int> dims, int index, int fallback)
    {
        if (index >= dims.Count) return fallback;
        int dim = dims[index];
        return dim > 0 ? dim : fallback;
    }

    /// <summary>BGRA SKBitmap → [1, 3, H, W] RGB float32 张量（归一化到 0–1）。</summary>
    private static DenseTensor<float> BitmapToRgbTensor(SKBitmap bmp, int w, int h)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, h, w });
        int rowBytes = bmp.RowBytes;

        unsafe
        {
            var ptr = (byte*)bmp.GetPixels();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * rowBytes + x * 4;
                    float b = ptr[idx] / 255f;
                    float g = ptr[idx + 1] / 255f;
                    float r = ptr[idx + 2] / 255f;

                    tensor[0, 0, y, x] = r;
                    tensor[0, 1, y, x] = g;
                    tensor[0, 2, y, x] = b;
                }
            }
        }

        return tensor;
    }

    /// <summary>将模型输出张量转换为灰度掩码 SKBitmap（白色=前景，黑色=背景）。</summary>
    private static SKBitmap OutputToMaskBitmap(Tensor<float> tensor, int fallbackW, int fallbackH)
    {
        int rank = tensor.Rank;
        int h;
        int w;

        if (rank >= 4)
        {
            h = tensor.Dimensions[rank - 2];
            w = tensor.Dimensions[rank - 1];
        }
        else if (rank == 3)
        {
            h = tensor.Dimensions[1];
            w = tensor.Dimensions[2];
        }
        else
        {
            h = fallbackH;
            w = fallbackW;
        }

        h = h > 0 ? h : fallbackH;
        w = w > 0 ? w : fallbackW;

        var mask = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));

        float min = float.MaxValue;
        float max = float.MinValue;
        foreach (var value in tensor)
        {
            if (value < min) min = value;
            if (value > max) max = value;
        }

        unsafe
        {
            var ptr = (byte*)mask.GetPixels();
            int rowBytes = mask.RowBytes;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float v = rank switch
                    {
                        >= 4 => tensor[0, 0, y, x],
                        3 => tensor[0, y, x],
                        _ => 0f
                    };

                    float normalized = NormalizeToUnit(v, min, max);
                    byte gray = (byte)Math.Clamp(normalized * 255f, 0f, 255f);
                    int idx = y * rowBytes + x * 4;
                    ptr[idx] = gray;
                    ptr[idx + 1] = gray;
                    ptr[idx + 2] = gray;
                    ptr[idx + 3] = 255;
                }
            }
        }

        return mask;
    }

    /// <summary>将模型输出值归一化到 [0, 1] 范围，自动识别值域（0–1 / -1–1 / 0–255）。</summary>
    private static float NormalizeToUnit(float value, float min, float max)
    {
        if (max - min > 1e-6f)
            return Math.Clamp((value - min) / (max - min), 0f, 1f);

        if (min < 0f)
            return Math.Clamp((value + 1f) * 0.5f, 0f, 1f);

        if (max > 1f)
            return Math.Clamp(value / 255f, 0f, 1f);

        return Math.Clamp(value, 0f, 1f);
    }

    /// <summary>将灰度掩码作为 Alpha 通道应用到原图，生成透明背景的结果。</summary>
    private static SKBitmap ApplyMaskAsAlpha(SKBitmap source, SKBitmap mask)
    {
        int w = source.Width;
        int h = source.Height;
        var result = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));

        unsafe
        {
            var srcPtr = (byte*)source.GetPixels();
            var maskPtr = (byte*)mask.GetPixels();
            var dstPtr = (byte*)result.GetPixels();

            int srcRow = source.RowBytes;
            int maskRow = mask.RowBytes;
            int dstRow = result.RowBytes;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int s = y * srcRow + x * 4;
                    int m = y * maskRow + x * 4;
                    int d = y * dstRow + x * 4;

                    byte alpha = maskPtr[m];
                    dstPtr[d] = srcPtr[s];
                    dstPtr[d + 1] = srcPtr[s + 1];
                    dstPtr[d + 2] = srcPtr[s + 2];
                    dstPtr[d + 3] = alpha;
                }
            }
        }

        return result;
    }



}
