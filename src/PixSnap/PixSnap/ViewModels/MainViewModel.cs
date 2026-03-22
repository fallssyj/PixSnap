using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PixSnap.Models;
using PixSnap.Services;
using PixSnap.Views;
using System.Text;
using System.Windows;
using Application = System.Windows.Application;

namespace PixSnap.ViewModels;

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

        var shouldRestoreMainWindow = Application.Current.MainWindow?.IsVisible == true;

        try
        {
            IsCapturing = true;

            Application.Current.MainWindow?.Hide();
            await Task.Delay(120);

            var selector = new RegionSelectorWindow(_screenCaptureService);

            if (selector.ShowDialog() == true && selector.Selection is { } selection)
            {
                var (screenshot, mode) = await CaptureSelectionAsync(selection);
                SendScreenshot(screenshot, mode);
            }
        }
        catch (Exception ex)
        {
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