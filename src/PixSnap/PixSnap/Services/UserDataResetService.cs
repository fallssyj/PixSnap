using Serilog;
using System.IO;
using System.Windows;

namespace PixSnap.Services;

/// <summary>清除用户本地数据（设置、模型、日志等），保留程序本身。</summary>
public static class UserDataResetService
{
    private const string ConfirmationMessage =
        "将删除以下内容：\n\n" +
        "· 所有应用设置\n" +
        "· 已下载的 AI 模型\n" +
        "· 运行日志\n\n" +
        "程序本身不会被卸载。此操作不可撤销。\n\n" +
        "清除完成后应用将退出。下次启动时需重新同意许可与免责声明。";

    public static bool TryPromptAndReset(Window? owner = null)
    {
        var result = AppMessageBox.Show(
            ConfirmationMessage,
            "清除本地数据",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            owner);

        if (result != MessageBoxResult.Yes)
            return false;

        try
        {
            ResetAllUserData();
        }
        catch (Exception ex)
        {
            LogFileService.ConfigureLogger();
            Log.Error(ex, "清除本地数据失败");
            AppMessageBox.Show(
                ExceptionMessageFormatter.Format("清除本地数据失败", ex),
                "清除本地数据",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                owner);
            return false;
        }

        Application.Current.Shutdown(0);
        return true;
    }

    public static void ResetAllUserData()
    {
        Log.Information("开始清除本地数据");

        OcrService.Shutdown();

        try
        {
            SettingsService.WriteStartupEnabled(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "清除开机启动注册表项失败");
        }

        Log.CloseAndFlush();

        TryDeleteDirectory(Path.Combine(Path.GetTempPath(), "PixSnap"));
        TryDeleteFile(AppPaths.SettingsFilePath + ".corrupted");
        TryDeleteFile(AppPaths.SettingsFilePath + ".tmp");
        TryDeleteDirectory(AppPaths.DataDirectory);
        TryDeleteFile(AppPaths.GetInstallPath("settings.json"));
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            throw new IOException($"无法删除目录：{path}。请关闭占用该目录的程序后重试。", ex);
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch
        {
            // 安装目录下的旧版 settings.json 可能只读或不存在，忽略即可。
        }
    }
}
