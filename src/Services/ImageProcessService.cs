// 编码：UTF-8 BOM
// 图片处理服务：使用 SkiaSharp 进行像素级操作（裁剪、圆角等）

using SkiaSharp;
using System.Windows.Media.Imaging;
using System.IO;
using System.Windows;

namespace PixSnap.Services;

/// <summary>基于 SkiaSharp 的图片处理服务</summary>
public class ImageProcessService
{
    /// <summary>将 WPF BitmapSource 转换为 SKBitmap</summary>
    public static SKBitmap BitmapSourceToSkBitmap(BitmapSource source)
    {
        // 转换为 Pbgra32 格式（兼容 SkiaSharp BGRA 格式）
        var formatted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
        int width = formatted.PixelWidth;
        int height = formatted.PixelHeight;

        var skBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        formatted.CopyPixels(pixels, stride, 0);

        unsafe
        {
            fixed (byte* ptr = pixels)
            {
                var skInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                skBitmap.InstallPixels(skInfo, (IntPtr)ptr, stride);
                // 拷贝一份，避免 pixels 数组被回收
                var copy = new SKBitmap(skInfo);
                using var canvas = new SKCanvas(copy);
                canvas.DrawBitmap(skBitmap, 0, 0);
                return copy;
            }
        }
    }

    /// <summary>将 SKBitmap 转换为 WPF BitmapSource</summary>
    public static BitmapSource SkBitmapToBitmapSource(SKBitmap skBitmap)
    {
        using var image = SKImage.FromBitmap(skBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        var decoder = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        decoder.Freeze();
        return decoder;
    }

    /// <summary>
    /// 内缩裁剪：上下左右各内缩指定像素
    /// </summary>
    /// <param name="source">原始图片</param>
    /// <param name="inset">内缩像素数（上下左右相同）</param>
    public static SKBitmap InsetCrop(SKBitmap source, int inset)
    {
        return InsetCrop(source, inset, inset, inset, inset);
    }

    /// <summary>
    /// 内缩裁剪：分别指定上下左右内缩像素
    /// </summary>
    public static SKBitmap InsetCrop(SKBitmap source, int top, int right, int bottom, int left)
    {
        int newWidth = source.Width - left - right;
        int newHeight = source.Height - top - bottom;

        if (newWidth <= 0 || newHeight <= 0)
            throw new ArgumentException("内缩值过大，导致裁剪后图像尺寸不合法");

        var result = new SKBitmap(newWidth, newHeight);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);

        // 从源图中截取内缩后的矩形
        var srcRect = new SKRectI(left, top, source.Width - right, source.Height - bottom);
        var dstRect = new SKRectI(0, 0, newWidth, newHeight);
        var srcRegion = new SKBitmap();
        source.ExtractSubset(srcRegion, srcRect);
        canvas.DrawBitmap(srcRegion, dstRect.Left, dstRect.Top);
        srcRegion.Dispose();

        return result;
    }

    /// <summary>
    /// 为图片添加圆角（透明背景）
    /// </summary>
    /// <param name="source">原始图片</param>
    /// <param name="radius">圆角半径（像素）</param>
    public static SKBitmap ApplyRoundCorners(SKBitmap source, float radius)
    {
        var result = new SKBitmap(source.Width, source.Height);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);

        // 创建圆角遮罩路径
        var rect = new SKRect(0, 0, source.Width, source.Height);
        using var path = new SKPath();
        path.AddRoundRect(rect, radius, radius);

        canvas.ClipPath(path, antialias: true);
        canvas.DrawBitmap(source, 0, 0);

        return result;
    }

    /// <summary>
    /// 保存图片到磁盘
    /// </summary>
    /// <param name="bitmap">SKBitmap 图片数据</param>
    /// <param name="filePath">目标路径</param>
    /// <param name="format">图片格式</param>
    /// <param name="quality">质量（JPEG 有效）</param>
    public static void SaveToFile(SKBitmap bitmap, string filePath, Models.ImageFormat format, int quality = 95)
    {
        var skFormat = format switch
        {
            Models.ImageFormat.Jpg => SKEncodedImageFormat.Jpeg,
            Models.ImageFormat.Bmp => SKEncodedImageFormat.Bmp,
            Models.ImageFormat.Webp => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png
        };

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(skFormat, quality);

        // 确保目录存在
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var stream = File.OpenWrite(filePath);
        data.SaveTo(stream);
    }

    /// <summary>
    /// 将保存格式转为文件扩展名
    /// </summary>
    public static string GetExtension(Models.ImageFormat format) => format switch
    {
        Models.ImageFormat.Jpg => ".jpg",
        Models.ImageFormat.Bmp => ".bmp",
        Models.ImageFormat.Webp => ".webp",
        _ => ".png"
    };

    /// <summary>
    /// 生成基于时间的文件名（年月日时分秒）
    /// </summary>
    public static string GenerateFileName(Models.ImageFormat format)
        => $"{DateTime.Now:yyyyMMddHHmmss}{GetExtension(format)}";

    /// <summary>
    /// 将 SKBitmap 复制到系统剪贴板
    /// </summary>
    public static void CopyToClipboard(BitmapSource bitmapSource)
    {
        var data = new System.Windows.DataObject();
        data.SetData(System.Windows.DataFormats.Bitmap, bitmapSource);
        System.Windows.Clipboard.SetDataObject(data, true);
    }
}
