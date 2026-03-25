using Serilog;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixSnap.Services;

/// <summary>
/// 截图历史记录管理：将最近截图保存为缩略图到 history/ 目录。
/// </summary>
public static class ScreenshotHistoryService
{
    private static readonly string HistoryDir =
        Path.Combine(AppContext.BaseDirectory, "history");

    private const int MaxHistoryItems = 30;
    private const int ThumbnailMaxDimension = 256;

    /// <summary>保存一张截图到历史记录（后台线程安全）。</summary>
    public static Task SaveAsync(BitmapSource image)
    {
        return Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(HistoryDir);
                var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
                var filePath = Path.Combine(HistoryDir, fileName);

                // 生成缩略图
                var thumbnail = CreateThumbnail(image);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(thumbnail));
                using var stream = File.Create(filePath);
                encoder.Save(stream);

                CleanupOldEntries();
                Log.Debug("截图历史记录已保存: {FileName}", fileName);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "保存截图历史失败");
            }
        });
    }

    /// <summary>获取按时间倒序排列的历史记录文件路径。</summary>
    public static IReadOnlyList<HistoryEntry> GetEntries()
    {
        if (!Directory.Exists(HistoryDir))
            return [];

        return Directory.GetFiles(HistoryDir, "*.png")
            .OrderByDescending(File.GetCreationTime)
            .Take(MaxHistoryItems)
            .Select(path => new HistoryEntry
            {
                FilePath = path,
                FileName = Path.GetFileNameWithoutExtension(path),
                CreatedAt = File.GetCreationTime(path)
            })
            .ToList();
    }

    /// <summary>加载缩略图为 BitmapSource（UI 线程安全调用）。</summary>
    public static BitmapSource? LoadThumbnail(string filePath)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(filePath, UriKind.Absolute);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = ThumbnailMaxDimension;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清除全部历史记录。</summary>
    public static void ClearAll()
    {
        try
        {
            if (Directory.Exists(HistoryDir))
                Directory.Delete(HistoryDir, recursive: true);
            Log.Information("截图历史已全部清除");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "清除截图历史失败");
        }
    }

    private static BitmapSource CreateThumbnail(BitmapSource source)
    {
        double scale = Math.Min(
            (double)ThumbnailMaxDimension / source.PixelWidth,
            (double)ThumbnailMaxDimension / source.PixelHeight);

        if (scale >= 1.0) return source;

        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }

    private static void CleanupOldEntries()
    {
        try
        {
            var files = Directory.GetFiles(HistoryDir, "*.png")
                .OrderByDescending(File.GetCreationTime)
                .Skip(MaxHistoryItems)
                .ToList();

            foreach (var file in files)
            {
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "清理旧历史记录失败");
        }
    }
}

public sealed class HistoryEntry
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required DateTime CreatedAt { get; init; }
}
