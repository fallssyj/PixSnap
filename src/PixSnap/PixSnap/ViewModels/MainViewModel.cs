using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PixSnap.Models;
using PixSnap.Services;
using PixSnap.Views;
using Serilog;
using System.IO;
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

        Log.Information("开始截图/录屏流程");

        var shouldRestoreMainWindow = Application.Current.MainWindow?.IsVisible == true;

        try
        {
            IsCapturing = true;

            Application.Current.MainWindow?.Hide();
            await Task.Delay(120);

            var selector = new RegionSelectorWindow(_screenCaptureService);

            if (selector.ShowDialog() == true && selector.Selection is { } selection)
            {
                if (selection.IsRecording)
                {
                    StartRecording(selection);
                }
                else
                {
                    var (screenshot, mode) = await CaptureSelectionAsync(selection);
                    Log.Information("截图完成: 模式={Mode}, 尺寸={W}×{H}", mode, screenshot.PixelWidth, screenshot.PixelHeight);
                    // 截图完成后自动复制到剪贴板
                    System.Windows.Clipboard.SetImage(screenshot);
                    SendScreenshot(screenshot, mode);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "截图/录屏失败");
            ShowError(BuildExceptionMessage("操作失败", ex));
        }
        finally
        {
            Log.Information("[录屏] StartCapture finally 块: shouldRestore={ShouldRestore}", shouldRestoreMainWindow);

            if (shouldRestoreMainWindow)
            {
                Application.Current.MainWindow?.Show();
                Application.Current.MainWindow?.Activate();
            }

            IsCapturing = false;
            Log.Information("[录屏] StartCapture finally 块完成");
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

    /// <summary>启动录屏并显示录制控制窗口。</summary>
    private void StartRecording(CaptureSelection selection)
    {
        if (_screenCaptureService.IsRecording)
        {
            ShowError("已有录屏正在进行中，请先停止当前录制。");
            return;
        }

        var tempPath = GenerateTempRecordingPath();
        var mic = selection.EnableMicrophone;
        var sys = selection.EnableSystemAudio;
        var bitrate = selection.VideoBitrate;
        Log.Information("开始录制: 模式={Mode}, 临时文件={Path}, 麦克风={Mic}, 系统声音={Sys}, 码率={Bitrate}", selection.Mode, tempPath, mic, sys, bitrate);

        try
        {
            // 先创建并显示控制窗口（避免与第一帧回调竞争）
            var controlWindow = new RecordingControlWindow(
                stopAction: async () =>
                {
                    try
                    {
                        await Task.Run(() => _screenCaptureService.StopRecording());
                        Log.Information("录制完成: {Path}", tempPath);

                        if (File.Exists(tempPath))
                        {
                            var playerVm = new VideoPlayerViewModel { TempFilePath = tempPath };
                            var playerWindow = new VideoPlayerWindow { DataContext = playerVm };
                            playerWindow.Show();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "停止录制失败");
                        ShowError(BuildExceptionMessage("停止录制失败", ex));
                    }
                },
                pauseAction: () => _screenCaptureService.PauseRecording(),
                resumeAction: () => _screenCaptureService.ResumeRecording()
            )
            {
                OutputFilePath = tempPath
            };

            Log.Information("[录屏] 控制窗口已创建，即将 Show()");
            controlWindow.Show();
            Log.Information("[录屏] 控制窗口已显示，开始启动原生录制");

            // 控制窗口显示后再启动录制
            switch (selection.Mode)
            {
                case CaptureSelectionMode.FullScreen:
                    _screenCaptureService.StartRecordingFullScreen(selection.ScreenIndex, tempPath, mic, sys, bitrate);
                    break;
                case CaptureSelectionMode.Window:
                    _screenCaptureService.StartRecordingWindow(selection.WindowHandle, tempPath, mic, sys, bitrate);
                    break;
                case CaptureSelectionMode.Region:
                    _screenCaptureService.StartRecordingRegion(selection.Region, tempPath, mic, sys, bitrate);
                    break;
            }

            Log.Information("[录屏] 原生录制已启动");

            // 检查音频初始化是否失败
            if ((mic || sys) && _screenCaptureService.AudioInitFailed)
            {
                Log.Warning("音频设备初始化失败，录制将无声音");
                controlWindow.ShowAudioWarning();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动录制失败");
            // 清理可能已创建的临时文件
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (Exception delEx) { Log.Warning(delEx, "清理录制临时文件失败: {Path}", tempPath); }
            ShowError(BuildExceptionMessage("启动录制失败", ex));
        }
    }

    private static string GenerateTempRecordingPath()
    {
        var dir = SettingsService.ReadRecordingTempDirectory();
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        return Path.Combine(dir, $"recording_{Guid.NewGuid():N}.mp4");
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