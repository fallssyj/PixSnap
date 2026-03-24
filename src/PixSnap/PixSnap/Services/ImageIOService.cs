using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixSnap.Services;

/// <summary>
/// 图像文件加载、保存、编码等 I/O 纯函数集合。
/// </summary>
public static class ImageIOService
{
    /// <summary>后台线程加载指定路径的图片文件为冻结的 BitmapSource。</summary>
    public static Task<BitmapSource> LoadBitmapFromFileAsync(string filePath, IProgress<(double Value, string Text)>? progress = null)
    {
        return Task.Run(() =>
        {
            progress?.Report((0.2, "正在读取文件..."));
            using var stream = File.OpenRead(filePath);
            progress?.Report((0.55, "正在解码图片..."));
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            var bitmap = decoder.Frames[0];
            if (!bitmap.IsFrozen)
                bitmap.Freeze();

            progress?.Report((0.95, "正在准备显示..."));
            return (BitmapSource)bitmap;
        });
    }

    /// <summary>后台线程将 BitmapSource 编码为 PNG 并写入磁盘。</summary>
    public static Task SavePngAsync(BitmapSource bitmap, string filePath, IProgress<(double Value, string Text)>? progress = null)
    {
        return Task.Run(() =>
        {
            progress?.Report((0.25, "正在编码图片..."));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            progress?.Report((0.7, "正在写入磁盘..."));
            using var stream = File.Create(filePath);
            encoder.Save(stream);
            progress?.Report((0.95, "正在完成保存..."));
        });
    }

    /// <summary>编码为 PNG 流以获取压缩后的实际文件大小。</summary>
    public static long GetEncodedPngSize(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.Length;
    }

    /// <summary>将字节数格式化为可读的文件大小文本（KB / MB）。</summary>
    public static string FormatFileSize(long byteCount)
    {
        const double kilo = 1024d;
        const double mega = kilo * 1024d;

        return byteCount switch
        {
            >= (long)mega => $"{byteCount / mega:0.0} MB",
            >= (long)kilo => $"{byteCount / kilo:0.0} KB",
            _ => $"{byteCount} B"
        };
    }

    /// <summary>创建冻结的深拷贝快照。</summary>
    public static BitmapSource CreateFrozenSnapshot(BitmapSource source)
    {
        if (source.IsFrozen)
            return source;

        var copy = new WriteableBitmap(source);
        copy.Freeze();
        return copy;
    }

    /// <summary>创建引用快照（无深拷贝）。当编辑流程保证不原位修改旧图时安全使用。</summary>
    public static BitmapSource CreateSnapshotFast(BitmapSource source)
    {
        return source;
    }
}
