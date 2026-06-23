using Serilog;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace PixSnap.Services;

internal static class ShellHelper
{
    public static void OpenDirectory(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            AppMessageBox.Show($"尚未设置{label}。", "PixSnap", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Directory.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "创建{Label}失败: {Path}", label, path);
                AppMessageBox.Show($"{label}不存在且无法创建：\n{path}", "PixSnap", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开{Label}失败: {Path}", label, path);
            AppMessageBox.Show($"无法打开{label}：\n{ex.Message}", "PixSnap", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
