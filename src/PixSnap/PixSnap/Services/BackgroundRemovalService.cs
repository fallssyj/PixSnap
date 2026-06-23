using PixSnap.Models;
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
/// 基于 RMBG-1.4 ONNX 模型的背景去除服务。
/// 输入原图，输出带 Alpha 通透背景的 BitmapSource。
/// 模型文件路径：<程序目录>/onnx/rmbg-1.4.onnx
/// </summary>
public static class BackgroundRemovalService
{
    private static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] ImageNetStd = [0.229f, 0.224f, 0.225f];

    /// <summary>异步执行背景去除。将原图缩放至模型输入尺寸，推理得到前景掩码，再还原到原始分辨率并合成透明背景。</summary>
    public static async Task<BitmapSource?> RunAsync(
        BitmapSource originalImage,
        IProgress<(double Value, string Text)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var frozen = ImageIOService.CreateFrozenSnapshot(originalImage);
        var mattingModel = AiFeatureSettings.Matting;
        string modelPath = AiModelCatalog.GetMattingModelPath(mattingModel);

        var export = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((0.05, "正在加载去背景模型..."));
            if (!File.Exists(modelPath))
            {
                Log.Error("抠图模型文件不存在: {ModelPath}", modelPath);
                throw new FileNotFoundException($"未找到 ONNX 模型：{modelPath}", modelPath);
            }

            Log.Information("开始去除背景({Model}): 图像 {W}×{H}", mattingModel, frozen.PixelWidth, frozen.PixelHeight);

            using var srcBitmap = SkiaInteropHelper.BitmapSourceToSKBitmap(frozen);
            int origW = srcBitmap.Width;
            int origH = srcBitmap.Height;

            progress?.Report((0.20, "正在初始化推理引擎..."));
            var session = OnnxSessionFactory.GetOrCreateSession(modelPath, out var providerName);
            progress?.Report((0.28, string.Format("当前推理设备：{0}", providerName)));
            var inputName = session.InputMetadata.Keys.First();
            var inputMeta = session.InputMetadata[inputName];
            var inputDims = inputMeta.Dimensions;

            int modelH = ResolveDim(inputDims, 2, 1024);
            int modelW = ResolveDim(inputDims, 3, 1024);

            using var scaled = new SKBitmap(new SKImageInfo(modelW, modelH, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            srcBitmap.ScalePixels(scaled, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

            progress?.Report((0.35, "正在构建输入张量..."));
            var imageTensor = BitmapToInputTensor(scaled, modelW, modelH, mattingModel);

            progress?.Report((0.55, "正在执行去背景推理..."));
            cancellationToken.ThrowIfCancellationRequested();
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, imageTensor)
            };
            using var run = OnnxInferenceHelper.RunWithCpuFallback(session, providerName, modelPath, inputs);
            var outputTensor = run.First().AsTensor<float>();
            using var maskBitmap = OutputToMaskBitmap(outputTensor, modelW, modelH, mattingModel);

            progress?.Report((0.78, "正在还原掩码分辨率..."));
            using var maskAtOriginal = new SKBitmap(new SKImageInfo(origW, origH, SKColorType.Bgra8888, SKAlphaType.Premul));
            maskBitmap.ScalePixels(maskAtOriginal, new SKSamplingOptions(SKCubicResampler.Mitchell));

            progress?.Report((0.84, "正在优化发丝边缘..."));
            RefineMattingAlpha(maskAtOriginal);

            progress?.Report((0.90, "正在合成透明背景..."));
            cancellationToken.ThrowIfCancellationRequested();
            using var composed = ApplyMaskAsAlpha(srcBitmap, maskAtOriginal);
            return (SkiaInteropHelper.CopyPixels(composed), composed.Width, composed.Height);
        }, cancellationToken);

        return SkiaInteropHelper.CreateFrozenBitmapFromBgra(export.Item1, export.Item2, export.Item3);
    }

    /// <summary>解析模型输入维度，动态值或无效值时使用回退默认值。</summary>
    private static int ResolveDim(IReadOnlyList<int> dims, int index, int fallback)
    {
        if (index >= dims.Count) return fallback;
        int dim = dims[index];
        return dim > 0 ? dim : fallback;
    }

    /// <summary>BGRA SKBitmap → [1, 3, H, W] float32 张量（按抠图模型规范归一化）。</summary>
    private static DenseTensor<float> BitmapToInputTensor(SKBitmap bmp, int w, int h, MattingModel model)
    {
        return model switch
        {
            MattingModel.BiRefNet => BitmapToImageNetTensor(bmp, w, h),
            _ => BitmapToRmbgTensor(bmp, w, h)
        };
    }

    /// <summary>RMBG-1.4：/255 后 mean=0.5、std=1.0。</summary>
    private static DenseTensor<float> BitmapToRmbgTensor(SKBitmap bmp, int w, int h)
    {
        var tensor = new DenseTensor<float>([1, 3, h, w]);
        int rowBytes = bmp.RowBytes;
        var buf = tensor.Buffer.Span;
        int chStride = h * w;

        unsafe
        {
            var ptr = (byte*)bmp.GetPixels();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * rowBytes + x * 4;
                    int pos = y * w + x;
                    buf[pos] = ptr[idx + 2] / 255f - 0.5f;
                    buf[chStride + pos] = ptr[idx + 1] / 255f - 0.5f;
                    buf[chStride * 2 + pos] = ptr[idx] / 255f - 0.5f;
                }
            }
        }

        return tensor;
    }

    /// <summary>BiRefNet：ImageNet 归一化。</summary>
    private static DenseTensor<float> BitmapToImageNetTensor(SKBitmap bmp, int w, int h)
    {
        var tensor = new DenseTensor<float>([1, 3, h, w]);
        int rowBytes = bmp.RowBytes;
        var buf = tensor.Buffer.Span;
        int chStride = h * w;

        unsafe
        {
            var ptr = (byte*)bmp.GetPixels();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * rowBytes + x * 4;
                    int pos = y * w + x;
                    float r = ptr[idx + 2] / 255f;
                    float g = ptr[idx + 1] / 255f;
                    float b = ptr[idx] / 255f;
                    buf[pos] = (r - ImageNetMean[0]) / ImageNetStd[0];
                    buf[chStride + pos] = (g - ImageNetMean[1]) / ImageNetStd[1];
                    buf[chStride * 2 + pos] = (b - ImageNetMean[2]) / ImageNetStd[2];
                }
            }
        }

        return tensor;
    }

    /// <summary>将模型输出张量转换为灰度掩码 SKBitmap（白色=前景，黑色=背景）。</summary>
    private static SKBitmap OutputToMaskBitmap(Tensor<float> tensor, int fallbackW, int fallbackH, MattingModel model)
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
        bool useSigmoid = model == MattingModel.BiRefNet;
        if (!useSigmoid)
        {
            foreach (var value in tensor)
            {
                if (value < min) min = value;
                if (value > max) max = value;
            }
        }

        var tensorSpan = rank >= 3 && tensor is DenseTensor<float> dense
            ? dense.Buffer.Span
            : default;

        unsafe
        {
            var ptr = (byte*)mask.GetPixels();
            int rowBytes = mask.RowBytes;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float v = rank >= 3 ? tensorSpan[y * w + x] : 0f;
                    float normalized = useSigmoid
                        ? Sigmoid(v)
                        : NormalizeRmbgOutput(v, min, max);
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

    private static float Sigmoid(float value) =>
        1f / (1f + MathF.Exp(-value));

    /// <summary>RMBG 官方后处理：按 batch min-max 归一化到 [0, 1]。</summary>
    private static float NormalizeRmbgOutput(float value, float min, float max)
    {
        if (max - min > 1e-6f)
            return Math.Clamp((value - min) / (max - min), 0f, 1f);

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

    /// <summary>柔化半透明边缘并尽量保留细发丝 Alpha。</summary>
    private static void RefineMattingAlpha(SKBitmap mask)
    {
        int w = mask.Width;
        int h = mask.Height;
        int count = w * h;
        var alpha = new byte[count];

        unsafe
        {
            var ptr = (byte*)mask.GetPixels();
            int rowBytes = mask.RowBytes;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    alpha[y * w + x] = ptr[y * rowBytes + x * 4];
                }
            }
        }

        var blurred = BoxBlurAlpha(alpha, w, h, radius: 1);

        unsafe
        {
            var ptr = (byte*)mask.GetPixels();
            int rowBytes = mask.RowBytes;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float a = alpha[i] / 255f;
                    float b = blurred[i] / 255f;
                    float edge = 4f * a * (1f - a);
                    float softened = a * (1f - edge * 0.3f) + b * (edge * 0.3f);
                    float recovered = Math.Max(softened, b * 0.92f);
                    byte gray = (byte)Math.Clamp(recovered * 255f, 0f, 255f);
                    int idx = y * rowBytes + x * 4;
                    ptr[idx] = gray;
                    ptr[idx + 1] = gray;
                    ptr[idx + 2] = gray;
                }
            }
        }
    }

    private static byte[] BoxBlurAlpha(byte[] source, int w, int h, int radius)
    {
        var temp = new byte[source.Length];
        var result = new byte[source.Length];
        int diameter = radius * 2 + 1;

        for (int y = 0; y < h; y++)
        {
            int sum = 0;
            for (int x = -radius; x <= radius; x++)
                sum += source[y * w + Math.Clamp(x, 0, w - 1)];

            for (int x = 0; x < w; x++)
            {
                temp[y * w + x] = (byte)(sum / diameter);
                int removeX = Math.Clamp(x - radius, 0, w - 1);
                int addX = Math.Clamp(x + radius + 1, 0, w - 1);
                sum += source[y * w + addX] - source[y * w + removeX];
            }
        }

        for (int x = 0; x < w; x++)
        {
            int sum = 0;
            for (int y = -radius; y <= radius; y++)
                sum += temp[Math.Clamp(y, 0, h - 1) * w + x];

            for (int y = 0; y < h; y++)
            {
                result[y * w + x] = (byte)(sum / diameter);
                int removeY = Math.Clamp(y - radius, 0, h - 1);
                int addY = Math.Clamp(y + radius + 1, 0, h - 1);
                sum += temp[addY * w + x] - temp[removeY * w + x];
            }
        }

        return result;
    }



}
