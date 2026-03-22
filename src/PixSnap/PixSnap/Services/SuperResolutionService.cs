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

public static class SuperResolutionService
{
    private static readonly string ModelPath =
        Path.Combine(AppContext.BaseDirectory, "onnx", "realesrgan-x4plus.onnx");

    private const int TileSize = 256;
    private const int ScaleFactor = 4;

    public static async Task<BitmapSource?> RunAsync(
        BitmapSource originalImage,
        IProgress<(double Value, string Text)>? progress = null)
    {
        return await Task.Run(() =>
        {
            progress?.Report((0.05, "正在加载超分模型..."));
            if (!File.Exists(ModelPath))
                throw new FileNotFoundException($"未找到 ONNX 模型：{ModelPath}");

            using var srcBitmap = BitmapSourceToSKBitmap(originalImage);
            int srcW = srcBitmap.Width;
            int srcH = srcBitmap.Height;

            progress?.Report((0.20, "正在初始化推理引擎..."));
            using var session = OnnxSessionFactory.CreateSession(ModelPath, out var providerName);
            progress?.Report((0.28, $"当前推理设备：{providerName}"));
            var inputName = session.InputMetadata.Keys.First();

            progress?.Report((0.35, "正在执行超分推理..."));
            using var outputBitmap = RunTiled(session, inputName, srcBitmap, srcW, srcH, progress);

            progress?.Report((0.90, "正在生成超分结果..."));
            var result = SKBitmapToBitmapSource(outputBitmap);
            result.Freeze();

            progress?.Report((1.0, "超分辨率完成"));
            return result;
        });
    }

    private static SKBitmap RunTiled(
        InferenceSession session,
        string inputName,
        SKBitmap source,
        int srcW,
        int srcH,
        IProgress<(double Value, string Text)>? progress)
    {
        var result = new SKBitmap(new SKImageInfo(srcW * ScaleFactor, srcH * ScaleFactor, SKColorType.Bgra8888, SKAlphaType.Unpremul));

        int tilesX = (int)Math.Ceiling((double)srcW / TileSize);
        int tilesY = (int)Math.Ceiling((double)srcH / TileSize);
        int totalTiles = tilesX * tilesY;
        int tileIndex = 0;

        for (int y = 0; y < srcH; y += TileSize)
        {
            for (int x = 0; x < srcW; x += TileSize)
            {
                tileIndex++;
                double phase = 0.35 + 0.45 * tileIndex / totalTiles;
                progress?.Report((phase, $"正在分块超分 ({tileIndex}/{totalTiles})..."));

                int validW = Math.Min(TileSize, srcW - x);
                int validH = Math.Min(TileSize, srcH - y);
                var inputTensor = ExtractTileToTensor(source, x, y, validW, validH, TileSize, TileSize);

                using var outputs = session.Run(new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
                });

                var outputTensor = outputs.First().AsTensor<float>();
                WriteTensorToBitmap(outputTensor, result, x * ScaleFactor, y * ScaleFactor, validW, validH, srcW * ScaleFactor, srcH * ScaleFactor);
            }
        }

        return result;
    }

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

    private static void WriteTensorToBitmap(
        Tensor<float> tensor,
        SKBitmap target,
        int destX,
        int destY,
        int validW,
        int validH,
        int fullW,
        int fullH)
    {
        int scaledH = validH * ScaleFactor;
        int scaledW = validW * ScaleFactor;
        int rowBytes = target.RowBytes;

        unsafe
        {
            var ptr = (byte*)target.GetPixels();

            for (int y = 0; y < scaledH; y++)
            {
                int finalY = destY + y;
                if (finalY >= fullH) continue;

                for (int x = 0; x < scaledW; x++)
                {
                    int finalX = destX + x;
                    if (finalX >= fullW) continue;

                    float r = Math.Clamp(tensor[0, 0, y, x], 0f, 1f);
                    float g = Math.Clamp(tensor[0, 1, y, x], 0f, 1f);
                    float b = Math.Clamp(tensor[0, 2, y, x], 0f, 1f);

                    int idx = finalY * rowBytes + finalX * 4;
                    ptr[idx] = (byte)Math.Clamp(b * 255f, 0f, 255f);
                    ptr[idx + 1] = (byte)Math.Clamp(g * 255f, 0f, 255f);
                    ptr[idx + 2] = (byte)Math.Clamp(r * 255f, 0f, 255f);
                    ptr[idx + 3] = 255;
                }
            }
        }
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
