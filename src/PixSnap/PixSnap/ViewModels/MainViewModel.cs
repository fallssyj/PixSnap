using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PixSnap.Models;
using PixSnap.Services;
using PixSnap.Views;
using Serilog;
using System.Text;
using System.Windows;
using Application = System.Windows.Application;

namespace PixSnap.ViewModels;

/// <summary>
/// 主窗口 ViewModel：管理截图流程（全屏 / 窗口 / 区域）并将结果通过 Messenger 发送到预览窗口。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IScreenCaptureService _screenCaptureService;

    [ObservableProperty]
    private bool _isCapturing;

    public MainViewModel(IScreenCaptureService screenCaptureService)
    {
        _screenCaptureService = screenCaptureService;
    }

    [RelayCommand]
    private async Task StartCapture()
    {
        if (IsCapturing)
        {
            return;
        }

        Log.Information("开始截图流程");

        var shouldRestoreMainWindow = Application.Current.MainWindow?.IsVisible == true;

        try
        {
            IsCapturing = true;

            Application.Current.MainWindow?.Hide();
            await Task.Delay(120);

            var selector = new RegionSelectorWindow(_screenCaptureService);

            if (selector.ShowDialog() == true && selector.Selection is { } selection)
            {
                // 记录区域截图的区域，以便 "上次区域" 快捷截图
                if (selection.Mode == CaptureSelectionMode.Region && selection.Region is { Width: > 0, Height: > 0 } r)
                    SettingsService.WriteLastRegion(r.X, r.Y, r.Width, r.Height);

                var (screenshot, mode) = await CaptureSelectionAsync(selection);
                Log.Information("截图完成: 模式={Mode}, 尺寸={W}×{H}", mode, screenshot.PixelWidth, screenshot.PixelHeight);
                SendScreenshot(screenshot, mode);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "截图失败");
            ShowError(BuildExceptionMessage("截图失败", ex));
        }
        finally
        {
            if (shouldRestoreMainWindow)
            {
                Application.Current.MainWindow?.Show();
                Application.Current.MainWindow?.Activate();
            }

            IsCapturing = false;
        }
    }

    /// <summary>根据用户选区模式执行对应的截图操作，返回截图及模式标识字符串。</summary>
    private async Task<(System.Windows.Media.Imaging.BitmapSource Screenshot, string Mode)> CaptureSelectionAsync(CaptureSelection selection)
    {
        return selection.Mode switch
        {
            CaptureSelectionMode.FullScreen => (
                await _screenCaptureService.CaptureFullScreenAsync(selection.ScreenIndex),
                "FullScreen"),
            CaptureSelectionMode.Window => (
                await _screenCaptureService.CaptureWindowAsync(selection.WindowHandle, includeBorder: false),
                "Window"),
            CaptureSelectionMode.Region => (
                await _screenCaptureService.CaptureRegionAsync(selection.Region),
                "Region"),
            _ => throw new InvalidOperationException("不支持的截图模式。")
        };
    }

    /// <summary>直接截取上次的区域，无需打开选区窗口。</summary>
    public async Task CaptureLastRegionAsync()
    {
        var region = SettingsService.ReadLastRegion();
        if (region is null)
        {
            MessageBoxWindow.Show("没有保存的截图区域。请先执行一次区域截图。", "PixSnap", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (IsCapturing) return;
        IsCapturing = true;

        try
        {
            var (x, y, w, h) = region.Value;
            Application.Current.MainWindow?.Hide();
            await Task.Delay(120);

            var rect = new Rect(x, y, w, h);
            var screenshot = await _screenCaptureService.CaptureRegionAsync(rect);
            Log.Information("上次区域截图完成: 区域={Region}, 尺寸={W}×{H}", rect, screenshot.PixelWidth, screenshot.PixelHeight);
            SendScreenshot(screenshot, "Region");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上次区域截图失败");
            ShowError(BuildExceptionMessage("上次区域截图失败", ex));
        }
        finally
        {
            Application.Current.MainWindow?.Show();
            IsCapturing = false;
        }
    }

    partial void OnIsCapturingChanged(bool value)
    {
        StartCaptureCommand.NotifyCanExecuteChanged();
    }

    private static void SendScreenshot(System.Windows.Media.Imaging.BitmapSource screenshot, string mode)
    {
        WeakReferenceMessenger.Default.Send(new ScreenshotCapturedMessage(screenshot, mode));
    }

    private static void ShowError(string message)
    {
        MessageBoxWindow.Show(
            message,
            "PixSnap 错误",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static string BuildExceptionMessage(string prefix, Exception exception)
    {
        var builder = new StringBuilder();
        builder.AppendLine(prefix);

        var current = exception;
        var depth = 0;
        while (current is not null)
        {
            builder.AppendLine($"[{depth}] {current.GetType().FullName}");
            builder.AppendLine(current.Message);
            builder.AppendLine($"HRESULT: 0x{current.HResult:X8}");

            if (!string.IsNullOrWhiteSpace(current.StackTrace))
            {
                builder.AppendLine("StackTrace:");
                builder.AppendLine(current.StackTrace);
            }

            current = current.InnerException;
            depth++;
            if (current is not null)
            {
                builder.AppendLine("InnerException:");
            }
        }

        return builder.ToString().TrimEnd();
    }
}