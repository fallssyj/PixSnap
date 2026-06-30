using Serilog;
using System.IO;

namespace PixSnap.Services;

/// <summary>应用日志目录维护：Serilog 初始化、过期清理、手动清空。</summary>
internal static class LogFileService
{
    public static string LogsDirectory => AppPaths.LogsDirectory;

    public static void ConfigureLogger()
    {
        var todayLogDir = Path.Combine(LogsDirectory, DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(todayLogDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(todayLogDir, "pixsnap.log"),
                fileSizeLimitBytes: null,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    /// <summary>关闭当前日志句柄并重新创建（用于清空或轮转后恢复写入）。</summary>
    public static void RestartLogger()
    {
        Log.CloseAndFlush();
        ConfigureLogger();
    }

    /// <summary>删除 logs 下所有日志文件及已清空的日期子目录。会先关闭 Serilog 以释放占用中的当前日志。</summary>
    public static (int Deleted, int Failed) DeleteAllFiles()
    {
        Log.CloseAndFlush();

        int deleted = 0;
        int failed = 0;
        if (Directory.Exists(LogsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(LogsDirectory, "*", SearchOption.AllDirectories))
            {
                if (TryDeleteFile(file))
                    deleted++;
                else
                    failed++;
            }

            RemoveEmptyDateSubdirectories();
        }

        ConfigureLogger();
        Log.Information("日志已清空：删除 {Deleted} 个文件，失败 {Failed} 个", deleted, failed);
        return (deleted, failed);
    }

    /// <summary>删除早于保留期的日志文件（按日期子目录或文件修改时间判断）。</summary>
    public static (int Deleted, int Failed) DeleteExpiredFiles(int retentionDays)
    {
        retentionDays = Math.Clamp(retentionDays, SettingsService.MinLogRetentionDays, SettingsService.MaxLogRetentionDays);
        if (!Directory.Exists(LogsDirectory))
            return (0, 0);

        var cutoffDate = DateTime.Today.AddDays(-(retentionDays - 1));
        int deleted = 0;
        int failed = 0;

        foreach (var file in Directory.EnumerateFiles(LogsDirectory, "*", SearchOption.AllDirectories))
        {
            if (!ShouldDeleteByRetention(file, cutoffDate))
                continue;

            if (TryDeleteFile(file))
                deleted++;
            else
                failed++;
        }

        RemoveEmptyDateSubdirectories();

        if (deleted > 0 || failed > 0)
            Log.Information("自动清理过期日志：删除 {Deleted} 个，失败 {Failed} 个（保留 {Days} 天）", deleted, failed, retentionDays);

        return (deleted, failed);
    }

    private static bool ShouldDeleteByRetention(string filePath, DateTime cutoffDate)
    {
        var parentName = Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty);
        if (parentName.Length == 10 && DateOnly.TryParse(parentName, out var folderDate))
            return folderDate < DateOnly.FromDateTime(cutoffDate);

        return File.GetLastWriteTime(filePath).Date < cutoffDate;
    }

    private static void RemoveEmptyDateSubdirectories()
    {
        if (!Directory.Exists(LogsDirectory))
            return;

        foreach (var dir in Directory.GetDirectories(LogsDirectory))
        {
            var folderName = Path.GetFileName(dir);
            if (folderName.Length != 10 || !DateOnly.TryParse(folderName, out _))
                continue;

            if (Directory.EnumerateFileSystemEntries(dir).Any())
                continue;

            try
            {
                Directory.Delete(dir);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "删除空日志目录失败: {Path}", dir);
            }
        }
    }

    private static bool TryDeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "删除日志文件失败: {Path}", filePath);
            return false;
        }
    }
}
