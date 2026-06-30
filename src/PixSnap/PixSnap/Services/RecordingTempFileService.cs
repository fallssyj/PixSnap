using Serilog;
using System.IO;

namespace PixSnap.Services;

/// <summary>录屏临时 MP4 的创建与清理（位于用户配置的录屏临时目录）。</summary>
public static class RecordingTempFileService
{
    public static string CreateTempRecordingPath()
    {
        var dir = SettingsService.ReadRecordingTempDirectory();
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"recording_{Guid.NewGuid():N}.mp4");
    }

    public static bool IsManagedTempFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var fileName = Path.GetFileName(fullPath);
            if (!fileName.StartsWith("recording_", StringComparison.OrdinalIgnoreCase)
                || !fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                return false;

            var fileDir = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(fileDir))
                return false;

            var tempDir = Path.GetFullPath(SettingsService.ReadRecordingTempDirectory());
            return string.Equals(Path.GetFullPath(fileDir), tempDir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void TryDelete(string? filePath)
    {
        if (!IsManagedTempFile(filePath))
            return;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(filePath!))
                {
                    File.Delete(filePath!);
                    Log.Information("已删除录屏临时文件: {Path}", filePath);
                }

                return;
            }
            catch (IOException ex) when (attempt < 2)
            {
                Log.Debug(ex, "删除录屏临时文件重试 ({Attempt}/3): {Path}", attempt + 1, filePath);
                Thread.Sleep(120);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "删除录屏临时文件失败: {Path}", filePath);
                return;
            }
        }
    }
}
