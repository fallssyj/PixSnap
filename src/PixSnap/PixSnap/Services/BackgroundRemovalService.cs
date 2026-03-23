using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixSnap.Services;

public static class BackgroundRemovalService
{
    private static readonly string ModelPath =
        Path.Combine(AppContext.BaseDirectory, "onnx", "rmbg-1.4.onnx");

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
                throw new FileNotFoundException($"未找到 ONNX 模型：{ModelPath}");

            using var srcBitmap = BitmapSourceToSKBitmap(originalImage);
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
            var result = SKBitmapToBitmapSource(composed);
            result.Freeze();

            progress?.Report((1.0, "去除背景完成"));
            return result;
        });
    }

    private static int ResolveDim(IReadOnlyList<int> dims, int index, int fallback)
    {
        if (index >= dims.Count) return fallback;
        int dim = dims[index];
        return dim > 0 ? dim : fallback;
    }

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

    private static SKBitmap BitmapSourceToSKBitmap(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int w = bgra.PixelWidth;
        int h = bgra.PixelHeight;
        int stride = w * 4;
        var pixels = new byte[h * stride];
        bgra.CopyPixels(pixels, stride, 0);

        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        unsafe
        {
            fixed (byte* srcPtr = pixels)
            {
                Buffer.MemoryCopy(srcPtr, (void*)bmp.GetPixels(), pixels.LongLength, pixels.LongLength);
            }
        }

        return bmp;
    }

    private static BitmapSource SKBitmapToBitmapSource(SKBitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        wb.Lock();
        try
        {
            unsafe
            {
                Buffer.MemoryCopy((void*)bmp.GetPixels(), (void*)wb.BackBuffer, (long)h * w * 4, (long)h * w * 4);
            }
            wb.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally
        {
            wb.Unlock();
        }
        return wb;
    }
}
