using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PixSnap.Services;

/// <summary>
/// 基于 LaMa fp32 ONNX 模型的图像修复服务。
/// 模型文件路径：&lt;程序目录&gt;/onnx/lama_fp32.onnx
/// 输入：原图 + 笔画列表（图片像素坐标 + 半径）；输出：修复后的 BitmapSource。
/// </summary>
public static class InpaintService
{
    private static readonly string ModelPath =
        Path.Combine(AppContext.BaseDirectory, "onnx", "lama_fp32.onnx");

    // 模型固定输入尺寸（lama_fp32.onnx 要求 512×512）
    private const int ModelSize = 512;

    /// <summary>兼容旧调用：默认羽化强度 20。</summary>
    public static Task<BitmapSource?> RunAsync(
        BitmapSource originalImage,
        IReadOnlyList<(Point Center, double Radius)> strokes,
        IProgress<(double Value, string Text)> progress,
        CancellationToken token)
    {
        return RunWithOptionsAsync(originalImage, strokes, 20, progress, token);
    }

    /// <summary>异步执行 AI 修复。将 strokes 覆盖的区域交给 LaMa 模型填充。</summary>
    public static async Task<BitmapSource?> RunWithOptionsAsync(
        BitmapSource originalImage,
        IReadOnlyList<(Point Center, double Radius)> strokes,
        int edgeFeatherStrength,
        IProgress<(double Value, string Text)> progress,
        CancellationToken token)
    {
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            progress.Report((0.05, "正在加载模型..."));

            if (!File.Exists(ModelPath))
            {
                Log.Error("LaMa 模型文件不存在: {ModelPath}", ModelPath);
                throw new FileNotFoundException(string.Format("未找到 ONNX 模型：{0}", ModelPath));
            }

            if (strokes.Count == 0)
            {
                Log.Debug("无涂抹笔画，跳过修复");
                return originalImage;
            }

            Log.Information("开始 AI 修复: {StrokeCount} 笔画, 图像 {W}×{H}", strokes.Count, originalImage.PixelWidth, originalImage.PixelHeight);

            // — 第一步：BitmapSource → BGRA SKBitmap（保留原图用于最终高精度合成） —
            using var srcBitmap = SkiaInteropHelper.BitmapSourceToSKBitmap(originalImage);
            int origW = srcBitmap.Width;
            int origH = srcBitmap.Height;

            // 仅对涂抹区域附近做高精度修复，避免整图降采样导致发糊
            var roiRect = ComputeRoiRect(strokes, origW, origH);
            int roiW = roiRect.Width;
            int roiH = roiRect.Height;
            Log.Debug("ROI 区域: ({Left},{Top}) {W}×{H}", roiRect.Left, roiRect.Top, roiW, roiH);

            using var roiBitmap = new SKBitmap(new SKImageInfo(roiW, roiH, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            if (!srcBitmap.ExtractSubset(roiBitmap, roiRect))
                throw new InvalidOperationException("无法提取 AI 修复 ROI 区域");

            var roiStrokes = TranslateStrokesToRoi(strokes, roiRect.Left, roiRect.Top);

            // 模型固定需要 ModelSize×ModelSize 输入，缩放图像和遮罩到该尺寸
            // 明确指定 Bgra8888 + Unpremul，避免平台默认格式和预乘导致颜色错误
            using var imageScaled = new SKBitmap(new SKImageInfo(ModelSize, ModelSize, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            roiBitmap.ScalePixels(imageScaled, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

            progress.Report((0.15, "正在生成修复遮罩..."));
            token.ThrowIfCancellationRequested();

            // — 第二步：绘制遮罩（白=擦除，黑=保留） —
            // 模型推理遮罩（512）
            using var maskBitmap = CreateMaskBitmap(roiStrokes, ModelSize, ModelSize, roiW, roiH);
            // ROI 合成遮罩（ROI 原始分辨率）
            using var maskOriginal = CreateMaskBitmap(roiStrokes, roiW, roiH, roiW, roiH);

            progress.Report((0.30, "正在初始化推理引擎..."));
            token.ThrowIfCancellationRequested();

            // — 第三步：构建 ONNX 输入张量 [1, 3, ModelSize, ModelSize] / [1, 1, ModelSize, ModelSize] —
            var imageTensor = BitmapToRgbTensor(imageScaled, ModelSize, ModelSize);
            var maskTensor  = MaskBitmapToTensor(maskBitmap, ModelSize, ModelSize);

            progress.Report((0.40, "AI 处理中，请稍候..."));
            token.ThrowIfCancellationRequested();

            // — 第四步：ONNX 推理 —
            BitmapSource result;
            var session = OnnxSessionFactory.GetOrCreateSession(ModelPath, out var providerName);
            {
                token.ThrowIfCancellationRequested();
                progress.Report((0.45, string.Format("当前推理设备：{0}", providerName)));

                // 动态解析模型输入名，兼容不同 LaMa 导出版本
                var inputNames     = session.InputMetadata.Keys.ToList();
                var imageInputName = inputNames.FirstOrDefault(
                    n => n.Contains("image", StringComparison.OrdinalIgnoreCase)) ?? inputNames[0];
                var maskInputName  = inputNames.FirstOrDefault(
                    n => n.Contains("mask",  StringComparison.OrdinalIgnoreCase))
                    ?? (inputNames.Count > 1 ? inputNames[1] : inputNames[0]);

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(imageInputName, imageTensor),
                    NamedOnnxValue.CreateFromTensor(maskInputName,  maskTensor)
                };

                progress.Report((0.50, "AI 处理中，请稍候..."));
                token.ThrowIfCancellationRequested();

                IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs;
                try
                {
                    outputs = session.Run(inputs);
                }
                catch (OnnxRuntimeException ex) when (providerName.StartsWith("DirectML", StringComparison.OrdinalIgnoreCase))
                {
                    // DML 推理时崩溃（如不支持的算子），回退到 CPU 重试
                    Log.Warning(ex, "DirectML 推理失败，回退到 CPU");
                    progress.Report((0.50, "DirectML 推理失败，已回退至 CPU..."));
                    using var cpuSession = OnnxSessionFactory.CreateCpuSession(ModelPath);
                    outputs = cpuSession.Run(inputs);
                }
                using (outputs)
                {
                var outputTensor = outputs.First().AsTensor<float>();

                progress.Report((0.90, "正在生成结果图像..."));
                token.ThrowIfCancellationRequested();

                // — 第五步：张量 → ModelSize SKBitmap —
                using var outputBitmap = RgbTensorToSKBitmap(outputTensor, ModelSize, ModelSize);

                // 将模型输出缩放回 ROI 分辨率
                using var outputAtRoi = new SKBitmap(new SKImageInfo(roiW, roiH, SKColorType.Bgra8888, SKAlphaType.Unpremul));
                outputBitmap.ScalePixels(outputAtRoi, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

                // 仅在涂抹区域应用模型结果，未涂抹区域严格保留 ROI 原图像素
                using var blendedRoi = CompositeByMask(roiBitmap, outputAtRoi, maskOriginal, edgeFeatherStrength);

                // 回贴到原图
                using var finalBitmap = new SKBitmap(new SKImageInfo(origW, origH, SKColorType.Bgra8888, SKAlphaType.Unpremul));
                srcBitmap.CopyTo(finalBitmap);
                BlitRoi(finalBitmap, blendedRoi, roiRect.Left, roiRect.Top);

                result = SkiaInteropHelper.SKBitmapToBitmapSource(finalBitmap);
                result.Freeze();
                }
            }

            progress.Report((1.0, "完成"));
            Log.Information("AI 修复完成");
            return result;
        }, token).ConfigureAwait(false);
    }

    private static List<(Point Center, double Radius)> TranslateStrokesToRoi(
        IReadOnlyList<(Point Center, double Radius)> strokes,
        int roiLeft,
        int roiTop)
    {
        var translated = new List<(Point Center, double Radius)>(strokes.Count);
        foreach (var (center, radius) in strokes)
            translated.Add((new Point(center.X - roiLeft, center.Y - roiTop), radius));
        return translated;
    }

    private static SKRectI ComputeRoiRect(IReadOnlyList<(Point Center, double Radius)> strokes, int width, int height)
    {
        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        double maxRadius = 1;

        foreach (var (center, radius) in strokes)
        {
            minX = Math.Min(minX, center.X - radius);
            minY = Math.Min(minY, center.Y - radius);
            maxX = Math.Max(maxX, center.X + radius);
            maxY = Math.Max(maxY, center.Y + radius);
            maxRadius = Math.Max(maxRadius, radius);
        }

        // 给上下文留边，提升修复质量
        int padding = (int)Math.Ceiling(Math.Max(24, maxRadius * 2.0));
        int left = Math.Clamp((int)Math.Floor(minX) - padding, 0, Math.Max(0, width - 1));
        int top = Math.Clamp((int)Math.Floor(minY) - padding, 0, Math.Max(0, height - 1));
        int right = Math.Clamp((int)Math.Ceiling(maxX) + padding, left + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(maxY) + padding, top + 1, height);

        return new SKRectI(left, top, right, bottom);
    }

    private static void BlitRoi(SKBitmap target, SKBitmap roi, int left, int top)
    {
        int copyWidthBytes = roi.Width * 4;
        int targetRowBytes = target.RowBytes;
        int roiRowBytes = roi.RowBytes;

        unsafe
        {
            var targetPtr = (byte*)target.GetPixels();
            var roiPtr = (byte*)roi.GetPixels();

            for (int y = 0; y < roi.Height; y++)
            {
                byte* srcRow = roiPtr + y * roiRowBytes;
                byte* dstRow = targetPtr + (top + y) * targetRowBytes + left * 4;
                Buffer.MemoryCopy(srcRow, dstRow, copyWidthBytes, copyWidthBytes);
            }
        }
    }

    // ── 辅助方法 ──────────────────────────────────────────────────────────────

    /// <summary>根据笔画列表在 padded 尺寸空间中绘制二值遮罩。</summary>
    private static SKBitmap CreateMaskBitmap(
        IReadOnlyList<(Point Center, double Radius)> strokes,
        int paddedW, int paddedH, int origW, int origH)
    {
        var mask = new SKBitmap(new SKImageInfo(paddedW, paddedH, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(mask);
        canvas.Clear(SKColors.Black);

        using var paint = new SKPaint
        {
            Color       = SKColors.White,
            IsAntialias = false,
            Style       = SKPaintStyle.Fill
        };

        // 将图片像素坐标缩放到 paddedW×paddedH 空间
        double scaleX = (double)paddedW / origW;
        double scaleY = (double)paddedH / origH;
        foreach (var (center, radius) in strokes)
        {
            canvas.DrawCircle(
                (float)(center.X * scaleX),
                (float)(center.Y * scaleY),
                (float)(radius * Math.Sqrt(scaleX * scaleY)),
                paint);
        }

        return mask;
    }



    /// <summary>BGRA SKBitmap → [1, 3, H, W] float32 RGB [0,1] 张量。</summary>
    private static DenseTensor<float> BitmapToRgbTensor(SKBitmap bmp, int w, int h)
    {
        var tensor  = new DenseTensor<float>([1, 3, h, w]);
        int rowBytes = bmp.RowBytes; // 使用实际行字节数，避免 padding 错位
        unsafe
        {
            var ptr = (byte*)bmp.GetPixels();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // SKColorType.Bgra8888：内存顺序 B G R A
                    int  idx = y * rowBytes + x * 4;
                    byte b   = ptr[idx];
                    byte g   = ptr[idx + 1];
                    byte r   = ptr[idx + 2];
                    // LaMa 期望 RGB 通道顺序
                    tensor[0, 0, y, x] = r / 255f;
                    tensor[0, 1, y, x] = g / 255f;
                    tensor[0, 2, y, x] = b / 255f;
                }
            }
        }
        return tensor;
    }

    /// <summary>灰度遮罩 SKBitmap → [1, 1, H, W] float32（0=保留，1=待修复）。</summary>
    private static DenseTensor<float> MaskBitmapToTensor(SKBitmap mask, int w, int h)
    {
        var tensor = new DenseTensor<float>([1, 1, h, w]);
        int rowBytes = mask.RowBytes;
        unsafe
        {
            var ptr = (byte*)mask.GetPixels();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // 白色画笔区域(255) -> 1 表示待修复；黑色背景(0) -> 0 表示保留
                    int idx = y * rowBytes + x * 4;
                    tensor[0, 0, y, x] = ptr[idx] > 128 ? 1f : 0f;
                }
            }
        }
        return tensor;
    }

    private static SKBitmap CompositeByMask(SKBitmap original, SKBitmap generated, SKBitmap mask, int edgeFeatherStrength)
    {
        int w = original.Width;
        int h = original.Height;
        var result = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));

        using var blendMask = BuildBlendMask(mask, edgeFeatherStrength);

        int srcRow = original.RowBytes;
        int genRow = generated.RowBytes;
        int maskRow = blendMask.RowBytes;
        int dstRow = result.RowBytes;

        unsafe
        {
            var srcPtr = (byte*)original.GetPixels();
            var genPtr = (byte*)generated.GetPixels();
            var maskPtr = (byte*)blendMask.GetPixels();
            var dstPtr = (byte*)result.GetPixels();

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int srcIdx = y * srcRow + x * 4;
                    int genIdx = y * genRow + x * 4;
                    int maskIdx = y * maskRow + x * 4;
                    int dstIdx = y * dstRow + x * 4;

                    float alpha = maskPtr[maskIdx] / 255f; // 0~1，越白越偏向 AI 结果

                    dstPtr[dstIdx] = (byte)Math.Clamp(srcPtr[srcIdx] * (1f - alpha) + genPtr[genIdx] * alpha, 0f, 255f);
                    dstPtr[dstIdx + 1] = (byte)Math.Clamp(srcPtr[srcIdx + 1] * (1f - alpha) + genPtr[genIdx + 1] * alpha, 0f, 255f);
                    dstPtr[dstIdx + 2] = (byte)Math.Clamp(srcPtr[srcIdx + 2] * (1f - alpha) + genPtr[genIdx + 2] * alpha, 0f, 255f);
                    dstPtr[dstIdx + 3] = 255;               // A
                }
            }
        }

        return result;
    }

    private static SKBitmap BuildBlendMask(SKBitmap binaryMask, int edgeFeatherStrength)
    {
        if (edgeFeatherStrength <= 0)
        {
            var copy = new SKBitmap(new SKImageInfo(binaryMask.Width, binaryMask.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            binaryMask.CopyTo(copy);
            return copy;
        }

        float sigma = Math.Clamp(edgeFeatherStrength / 12f, 0.1f, 8f);
        var blurred = new SKBitmap(new SKImageInfo(binaryMask.Width, binaryMask.Height, SKColorType.Bgra8888, SKAlphaType.Premul));

        using (var surface = SKSurface.Create(blurred.Info, blurred.GetPixels(), blurred.RowBytes))
        using (var image = SKImage.FromBitmap(binaryMask))
        using (var paint = new SKPaint
        {
            IsAntialias = true,
            ImageFilter = SKImageFilter.CreateBlur(sigma, sigma)
        })
        {
            surface.Canvas.Clear(SKColors.Black);
            surface.Canvas.DrawImage(image, 0, 0, paint);
            surface.Canvas.Flush();
        }

        return blurred;
    }

    /// <summary>[1, 3, H, W] float32 RGB 张量 → BGRA SKBitmap。</summary>
    private static SKBitmap RgbTensorToSKBitmap(Tensor<float> tensor, int w, int h)
    {
        // 使用 Unpremul 避免预乘运算再次扭曲颜色值
        var bmp      = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        int rowBytes = bmp.RowBytes;
        var (minValue, maxValue) = GetTensorRange(tensor);
        unsafe
        {
            var ptr = (byte*)bmp.GetPixels();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // 模型输出 RGB 通道顺序，写入内存为 B G R A（Bgra8888）
                    float r   = NormalizeModelOutput(tensor[0, 0, y, x], minValue, maxValue);
                    float g   = NormalizeModelOutput(tensor[0, 1, y, x], minValue, maxValue);
                    float b   = NormalizeModelOutput(tensor[0, 2, y, x], minValue, maxValue);
                    int   idx = y * rowBytes + x * 4;
                    ptr[idx]     = (byte)(b * 255);  // B
                    ptr[idx + 1] = (byte)(g * 255);  // G
                    ptr[idx + 2] = (byte)(r * 255);  // R
                    ptr[idx + 3] = 255;               // A
                }
            }
        }
        return bmp;
    }

    private static (float Min, float Max) GetTensorRange(Tensor<float> tensor)
    {
        float min = float.MaxValue;
        float max = float.MinValue;
        foreach (var value in tensor)
        {
            if (value < min) min = value;
            if (value > max) max = value;
        }
        return (min, max);
    }

    private static float NormalizeModelOutput(float value, float minValue, float maxValue)
    {
        // 常见导出值域：
        // 1) [0,1]   -> 直接使用
        // 2) [-1,1]  -> (x+1)/2
        // 3) [0,255] -> /255
        if (maxValue > 1.5f)
            return Math.Clamp(value / 255f, 0f, 1f);

        if (minValue < 0f)
            return Math.Clamp((value + 1f) * 0.5f, 0f, 1f);

        return Math.Clamp(value, 0f, 1f);
    }

}
