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
}
