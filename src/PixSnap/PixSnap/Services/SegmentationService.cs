using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PixSnap.Models;
using Serilog;
using SkiaSharp;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PixSnap.Services;

/// <summary>点选分割（MobileSAM / FastSAM）。</summary>
public static class SegmentationService
{
    private const int SamInputSize = 1024;

    private static readonly float[] SamMean = [123.675f, 116.28f, 103.53f];
    private static readonly float[] SamStd = [58.395f, 57.12f, 57.375f];

    public static bool IsAvailable =>
        AiModelCatalog.IsSegmentationReady(AiFeatureSettings.Segmentation);

    public sealed class SegmentationNotAvailableException(string message) : Exception(message);

    /// <summary>在图像坐标点击一点，返回与原图同尺寸的 Alpha 蒙版（白=前景）。</summary>
    public static async Task<BitmapSource?> SegmentAtPointAsync(
        BitmapSource image,
        Point pixelPoint,
        IProgress<(double Value, string Text)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new SegmentationNotAvailableException(
                "点选分割模型未就绪。请在「设置 → AI → 模型管理」中下载。");
        }

        return AiFeatureSettings.Segmentation switch
        {
            SegmentationModel.FastSam => await RunFastSamAsync(image, progress, cancellationToken).ConfigureAwait(false),
            _ => await RunMobileSamAsync(image, pixelPoint, progress, cancellationToken).ConfigureAwait(false)
        };
    }

    private static async Task<BitmapSource?> RunMobileSamAsync(
        BitmapSource image,
        Point pixelPoint,
        IProgress<(double Value, string Text)>? progress,
        CancellationToken cancellationToken)
    {
        var frozen = ImageIOService.CreateFrozenSnapshot(image);
        string encoderPath = AiModelCatalog.GetAbsolutePath(AiModelCatalog.GetRequired("mobilesam-encoder"));
        string decoderPath = AiModelCatalog.GetAbsolutePath(AiModelCatalog.GetRequired("mobilesam-decoder"));

        var export = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((0.1, "正在加载 MobileSAM..."));

            using var src = SkiaInteropHelper.BitmapSourceToSKBitmap(frozen);
            int origW = src.Width;
            int origH = src.Height;
            float scale = SamInputSize / (float)Math.Max(origW, origH);
            int resizedW = (int)Math.Round(origW * scale);
            int resizedH = (int)Math.Round(origH * scale);

            using var resized = new SKBitmap(new SKImageInfo(resizedW, resizedH, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            src.ScalePixels(resized, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

            var imageTensor = BuildSamImageTensor(resized, SamInputSize, SamInputSize);
            var encoderSession = OnnxSessionFactory.GetOrCreateSession(encoderPath, out var encProvider);
            string encInput = encoderSession.InputMetadata.Keys.First();

            progress?.Report((0.35, "MobileSAM 编码中..."));
            using var encRun = OnnxInferenceHelper.RunWithCpuFallback(
                encoderSession, encProvider, encoderPath,
                [NamedOnnxValue.CreateFromTensor(encInput, imageTensor)]);
            var embeddings = encRun.First().AsTensor<float>();

            float px = (float)(pixelPoint.X * scale);
            float py = (float)(pixelPoint.Y * scale);
            var pointCoords = new DenseTensor<float>([1, 1, 2]);
            pointCoords[0, 0, 0] = px;
            pointCoords[0, 0, 1] = py;
            var pointLabels = new DenseTensor<float>([1, 1]);
            pointLabels[0, 0] = 1f;
            var maskInput = new DenseTensor<float>([1, 1, 256, 256]);
            var hasMask = new DenseTensor<float>([1]);
            hasMask[0] = 0f;
            var origSize = new DenseTensor<float>([2]);
            origSize[0] = resizedH;
            origSize[1] = resizedW;

            var decoderSession = OnnxSessionFactory.GetOrCreateSession(decoderPath, out var decProvider);
            var decInputs = BuildDecoderInputs(decoderSession, embeddings, pointCoords, pointLabels, maskInput, hasMask, origSize);

            progress?.Report((0.65, "MobileSAM 生成分割蒙版..."));
            using var decRun = OnnxInferenceHelper.RunWithCpuFallback(decoderSession, decProvider, decoderPath, decInputs);
            var maskTensor = decRun.First().AsTensor<float>();
            using var maskSmall = TensorToMaskBitmap(maskTensor, resizedW, resizedH);
            using var maskFull = new SKBitmap(new SKImageInfo(origW, origH, SKColorType.Bgra8888, SKAlphaType.Premul));
            maskSmall.ScalePixels(maskFull, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

            return (SkiaInteropHelper.CopyPixels(maskFull), origW, origH);
        }, cancellationToken);

        return SkiaInteropHelper.CreateFrozenBitmapFromBgra(export.Item1, export.Item2, export.Item3);
    }

    private static Task<BitmapSource?> RunFastSamAsync(
        BitmapSource image,
        IProgress<(double Value, string Text)>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report((0.1, "FastSAM 整图分割暂不支持点选，请在设置中切换为 MobileSAM。"));
        throw new NotSupportedException("FastSAM 适用于整图分割，点选模式请使用 MobileSAM。");
    }

    private static List<NamedOnnxValue> BuildDecoderInputs(
        InferenceSession session,
        Tensor<float> embeddings,
        DenseTensor<float> pointCoords,
        DenseTensor<float> pointLabels,
        DenseTensor<float> maskInput,
        DenseTensor<float> hasMask,
        DenseTensor<float> origSize)
    {
        var map = new Dictionary<string, Tensor<float>>(StringComparer.Ordinal)
        {
            ["image_embeddings"] = embeddings,
            ["point_coords"] = pointCoords,
            ["point_labels"] = pointLabels,
            ["mask_input"] = maskInput,
            ["has_mask_input"] = hasMask,
            ["orig_im_size"] = origSize
        };

        var inputs = new List<NamedOnnxValue>();
        foreach (var name in session.InputMetadata.Keys)
        {
            if (map.TryGetValue(name, out var tensor))
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, tensor));
        }

        if (inputs.Count == 0)
            throw new InvalidOperationException("MobileSAM 解码器输入名称与预期不匹配。");

        return inputs;
    }

    private static DenseTensor<float> BuildSamImageTensor(SKBitmap bmp, int targetW, int targetH)
    {
        var tensor = new DenseTensor<float>([1, 3, targetH, targetW]);
        int chStride = targetH * targetW;
        int rowBytes = bmp.RowBytes;
        unsafe
        {
            var ptr = (byte*)bmp.GetPixels();
            for (int y = 0; y < targetH; y++)
            {
                for (int x = 0; x < targetW; x++)
                {
                    int sx = x * bmp.Width / targetW;
                    int sy = y * bmp.Height / targetH;
                    int idx = sy * rowBytes + sx * 4;
                    int pos = y * targetW + x;
                    tensor[0, 0, y, x] = (ptr[idx + 2] - SamMean[0]) / SamStd[0];
                    tensor[0, 1, y, x] = (ptr[idx + 1] - SamMean[1]) / SamStd[1];
                    tensor[0, 2, y, x] = (ptr[idx] - SamMean[2]) / SamStd[2];
                }
            }
        }

        return tensor;
    }

    private static SKBitmap TensorToMaskBitmap(Tensor<float> tensor, int width, int height)
    {
        int h = tensor.Dimensions[^2];
        int w = tensor.Dimensions[^1];
        var mask = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var span = tensor is DenseTensor<float> dense ? dense.Buffer.Span : tensor.ToArray().AsSpan();
        unsafe
        {
            var ptr = (byte*)mask.GetPixels();
            int rowBytes = mask.RowBytes;
            for (int y = 0; y < height; y++)
            {
                int sy = y * h / height;
                for (int x = 0; x < width; x++)
                {
                    int sx = x * w / width;
                    float v = span[sy * w + sx];
                    byte gray = (byte)Math.Clamp(v > 0.5f ? 255 : 0, 0, 255);
                    int d = y * rowBytes + x * 4;
                    ptr[d] = gray;
                    ptr[d + 1] = gray;
                    ptr[d + 2] = gray;
                    ptr[d + 3] = 255;
                }
            }
        }

        return mask;
    }
}
