using SkiaSharp;
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixSnap.Services;

/// <summary>
/// SkiaSharp 与 WPF BitmapSource 之间的互操作辅助类。
/// 将 <see cref="BitmapSource"/> ↔ <see cref="SKBitmap"/> 的转换集中在此处，
/// 避免在多个 AI 服务中重复实现相同的像素搬运逻辑。
/// </summary>
internal static class SkiaInteropHelper
{
    /// <summary>
    /// 将 WPF <see cref="BitmapSource"/> 转换为 SkiaSharp <see cref="SKBitmap"/>。
    /// 内部统一转为 Bgra32（非预乘）格式后逐行拷贝像素。
    /// </summary>
    /// <param name="source">输入的 WPF 位图。</param>
    /// <returns>内容一致的 SKBitmap（Bgra8888 / Unpremul），调用方负责 Dispose。</returns>
    public static SKBitmap BitmapSourceToSKBitmap(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int w      = bgra.PixelWidth;
        int h      = bgra.PixelHeight;
        int stride = w * 4;

        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        bgra.CopyPixels(new Int32Rect(0, 0, w, h), bmp.GetPixels(), h * stride, stride);

        return bmp;
    }

    /// <summary>
    /// 将 SkiaSharp <see cref="SKBitmap"/> 转换为 WPF <see cref="WriteableBitmap"/>。
    /// 通过 <see cref="WriteableBitmap.BackBuffer"/> 直接拷贝像素，零中间分配。
    /// </summary>
    /// <param name="bmp">输入的 SKBitmap（Bgra8888）。</param>
    /// <returns>像素一致的 WriteableBitmap（Bgra32，96 DPI），未冻结。</returns>
    public static BitmapSource SKBitmapToBitmapSource(SKBitmap bmp)
    {
        int w  = bmp.Width;
        int h  = bmp.Height;
        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        wb.Lock();
        try
        {
            unsafe
            {
                Buffer.MemoryCopy(
                    (void*)bmp.GetPixels(),
                    (void*)wb.BackBuffer,
                    (long)h * w * 4,
                    (long)h * w * 4);
            }
            wb.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally
        {
            wb.Unlock();
        }
        return wb;
    }

    /// <summary>将 SKBitmap 像素导出为 BGRA32 字节数组。</summary>
    public static byte[] CopyPixels(SKBitmap bmp)
    {
        int stride = bmp.Width * 4;
        var pixels = new byte[bmp.Height * stride];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), pixels, 0, pixels.Length);
        return pixels;
    }

    /// <summary>在 UI 线程从 BGRA 像素创建冻结的 BitmapSource。</summary>
    public static BitmapSource CreateFrozenBitmapFromBgra(byte[] pixels, int width, int height, double dpiX = 96, double dpiY = 96)
    {
        var wb = new WriteableBitmap(width, height, dpiX, dpiY, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        wb.Freeze();
        return wb;
    }

    /// <summary>高质量放大位图，用于 OCR 前增强小字号 UI 文字。</summary>
    public static SKBitmap Upscale(SKBitmap source, int factor)
    {
        if (factor <= 1)
            return source.Copy();

        var info = new SKImageInfo(
            source.Width * factor,
            source.Height * factor,
            source.ColorType,
            source.AlphaType);
        return source.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
               ?? source.Copy();
    }
}
