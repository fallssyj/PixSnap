using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PixSnap.Models;
using PixSnap.Services;
using PixSnap.Views;
using Serilog;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
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

        var recordingInProgress = _screenCaptureService.IsRecording;
        if (recordingInProgress)
            Log.Information("录屏进行中，进入截图选区（录屏继续）");

        Log.Information("开始截图/录屏流程");

        var shouldRestoreMainWindow = Application.Current.MainWindow?.IsVisible == true;

        try
        {
            IsCapturing = true;

            // 在隐藏主窗口之前并行启动预截图与窗口快照，
            // 但不等待预截图完成，确保选区窗口可以立刻弹出。
            var screens = _screenCaptureService.GetScreens();
            var preCaptureTasks = StartPreCaptureTasks(screens);
            var windowSnapshot = NativeWindowHelper.SnapshotWindowRects();

            Application.Current.MainWindow?.Hide();
            await Task.Delay(120);

            var initialPreCaptures = CollectCompletedPreCaptures(preCaptureTasks);
            var selector = new RegionSelectorWindow(
                _screenCaptureService,
                initialPreCaptures,
                windowSnapshot,
                suppressRecordingMode: recordingInProgress);

            if (selector.ShowDialog() == true && selector.Selection is { } selection)
            {
                if (selection.IsRecording)
                {
                    StartRecording(selection);
                }
                else
                {
                    var preCaptures = await ResolvePreCapturesAsync(preCaptureTasks, timeoutMs: 800);
                    var (screenshot, mode) = preCaptures.Count == screens.Count
                        ? CropFromPreCaptures(selection, preCaptures, screens)
                        : await CaptureFromSelectionAsync(selection);

                    Log.Information("截图完成: 模式={Mode}, 尺寸={W}×{H}", mode, screenshot.PixelWidth, screenshot.PixelHeight);
                    // 截图完成后自动复制到剪贴板
                    ClipboardHelper.TrySetImage(screenshot);
                    SendScreenshot(screenshot, mode);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "截图/录屏失败");
            ShowError(ExceptionMessageFormatter.Format("操作失败", ex));
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

    private Dictionary<int, Task<BitmapSource>> StartPreCaptureTasks(IEnumerable<ScreenInfo> screens)
    {
        var tasks = new Dictionary<int, Task<BitmapSource>>();
        foreach (var screen in screens)
        {
            tasks[screen.Index] = _screenCaptureService.CaptureFullScreenAsync(screen.Index);
        }
        return tasks;
    }

    private static Dictionary<int, BitmapSource> CollectCompletedPreCaptures(Dictionary<int, Task<BitmapSource>> preCaptureTasks)
    {
        var preCaptures = new Dictionary<int, BitmapSource>();
        foreach (var (screenIndex, task) in preCaptureTasks)
        {
            if (task.IsCompletedSuccessfully)
            {
                preCaptures[screenIndex] = task.Result;
            }
        }
        return preCaptures;
    }

    private static async Task<Dictionary<int, BitmapSource>> ResolvePreCapturesAsync(
        Dictionary<int, Task<BitmapSource>> preCaptureTasks,
        int timeoutMs)
    {
        try
        {
            await Task.WhenAll(preCaptureTasks.Values).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "预截图等待超时或失败，将回退到实时 WGC 路径");
        }

        return CollectCompletedPreCaptures(preCaptureTasks);
    }

    private async Task<(BitmapSource Screenshot, string Mode)> CaptureFromSelectionAsync(CaptureSelection selection)
    {
        return selection.Mode switch
        {
            CaptureSelectionMode.FullScreen =>
                (await _screenCaptureService.CaptureFullScreenAsync(selection.ScreenIndex), "FullScreen"),

            CaptureSelectionMode.Window =>
                (await _screenCaptureService.CaptureWindowAsync(selection.WindowHandle, includeBorder: false), "Window"),

            _ =>
                (await _screenCaptureService.CaptureRegionAsync(selection.Region), "Region")
        };
    }

    /// <summary>从预截取的全屏截图中裁剪出用户选区，避免选区窗口抢焦点导致截图内容变化。</summary>
    private static (BitmapSource Screenshot, string Mode) CropFromPreCaptures(
        CaptureSelection selection,
        Dictionary<int, BitmapSource> preCaptures,
        List<ScreenInfo> screens)
    {
        if (selection.Mode == CaptureSelectionMode.FullScreen)
        {
            return (preCaptures[selection.ScreenIndex], "FullScreen");
        }

        var targetRect = selection.Mode == CaptureSelectionMode.Window
            ? selection.WindowRect
            : selection.Region;

        var mode = selection.Mode == CaptureSelectionMode.Window ? "Window" : "Region";

        // 找到包含目标矩形中心点的屏幕
        var cx = targetRect.X + targetRect.Width / 2;
        var cy = targetRect.Y + targetRect.Height / 2;
        var screen = screens.FirstOrDefault(s =>
            s.Bounds.Contains((int)cx, (int)cy)) ?? screens[0];

        var capture = preCaptures[screen.Index];
        var bounds = screen.Bounds;

        int x = Math.Max(0, (int)Math.Round(targetRect.X - bounds.X));
        int y = Math.Max(0, (int)Math.Round(targetRect.Y - bounds.Y));
        int w = (int)Math.Round(targetRect.Width);
        int h = (int)Math.Round(targetRect.Height);

        w = Math.Min(w, capture.PixelWidth - x);
        h = Math.Min(h, capture.PixelHeight - y);

        if (w <= 0 || h <= 0)
        {
            return (capture, mode);
        }

        var cropped = new CroppedBitmap(capture, new Int32Rect(x, y, w, h));
        cropped.Freeze();
        return (cropped, mode);
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

        RecordingControlWindow? controlWindow = null;
        try
        {
            // 先创建并显示控制窗口（避免与第一帧回调竞争）
            var recordingVm = new RecordingControlViewModel(
                stopAction: async () =>
                {
                    try
                    {
                        await _screenCaptureService.StopRecordingAsync();
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
                        ShowError(ExceptionMessageFormatter.Format("停止录制失败", ex));
                    }
                },
                pauseAction: () => _screenCaptureService.PauseRecording(),
                resumeAction: () => _screenCaptureService.ResumeRecording())
            {
                OutputFilePath = tempPath
            };

            controlWindow = new RecordingControlWindow(recordingVm);

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
                controlWindow.SetAudioWarningVisible();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动录制失败");
            // 关闭已显示的控制窗口
            try { controlWindow?.Close(); } catch { }
            // 清理可能已创建的临时文件
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (Exception delEx) { Log.Warning(delEx, "清理录制临时文件失败: {Path}", tempPath); }
            ShowError(ExceptionMessageFormatter.Format("启动录制失败", ex));
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
        AppMessageBox.Show(
            message,
            "PixSnap 错误",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}