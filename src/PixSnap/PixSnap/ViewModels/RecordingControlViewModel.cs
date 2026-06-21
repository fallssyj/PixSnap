using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSnap.Services;
using Serilog;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PixSnap.ViewModels;

public partial class RecordingControlViewModel : ObservableObject
{
    private const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    private readonly Func<Task> _stopAction;
    private readonly Action _pauseAction;
    private readonly Action _resumeAction;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _diskCheckTimer;
    private readonly DateTime _startTime;
    private DateTime _pauseBeginTime;
    private TimeSpan _pausedElapsed;

    [ObservableProperty]
    private string _timerText = "00:00";

    [ObservableProperty]
    private string _pauseButtonText = "暂停";

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isStopping;

    [ObservableProperty]
    private bool _showAudioWarning;

    [ObservableProperty]
    private Brush _recordingDotFill = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));

    [ObservableProperty]
    private bool _isDotAnimating = true;

    public string? OutputFilePath { get; set; }

    public RecordingControlViewModel(Func<Task> stopAction, Action pauseAction, Action resumeAction)
    {
        _stopAction = stopAction;
        _pauseAction = pauseAction;
        _resumeAction = resumeAction;
        _startTime = DateTime.Now;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateTimerText();

        _diskCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _diskCheckTimer.Tick += (_, _) => CheckDiskSpace();
    }

    public void OnWindowSourceInitialized(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
    }

    public void StartTimers()
    {
        _timer.Start();
        _diskCheckTimer.Start();
    }

    public void StopTimers()
    {
        _timer.Stop();
        _diskCheckTimer.Stop();
    }

    public void SetAudioWarningVisible() => ShowAudioWarning = true;

    [RelayCommand(CanExecute = nameof(CanUseRecordingControls))]
    private void TogglePause()
    {
        if (IsPaused)
        {
            _pausedElapsed += DateTime.Now - _pauseBeginTime;
            IsPaused = false;
            PauseButtonText = "暂停";
            _resumeAction();
            RecordingDotFill = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
            IsDotAnimating = true;
        }
        else
        {
            _pauseBeginTime = DateTime.Now;
            IsPaused = true;
            PauseButtonText = "继续";
            _pauseAction();
            RecordingDotFill = new SolidColorBrush(Colors.Gray);
            IsDotAnimating = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseRecordingControls))]
    private async Task StopAsync()
    {
        if (IsStopping)
            return;

        IsStopping = true;
        StopTimers();

        TogglePauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();

        RecordingDotFill = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00));
        IsDotAnimating = false;
        TimerText = "正在编码...";

        try
        {
            await _stopAction();
        }
        finally
        {
            RequestClose?.Invoke();
        }
    }

    public event Action? RequestClose;

    private bool CanUseRecordingControls() => !IsStopping;

    partial void OnIsStoppingChanged(bool value)
    {
        TogglePauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPausedChanged(bool value) => UpdateTimerText();

    private void UpdateTimerText()
    {
        if (IsPaused || IsStopping)
            return;

        var elapsed = DateTime.Now - _startTime - _pausedElapsed;
        TimerText = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    private void CheckDiskSpace()
    {
        if (IsPaused || IsStopping || string.IsNullOrEmpty(OutputFilePath))
            return;

        try
        {
            var root = Path.GetPathRoot(OutputFilePath);
            if (string.IsNullOrEmpty(root))
                return;

            var driveInfo = new DriveInfo(root);
            if (!driveInfo.IsReady)
                return;

            const long lowDiskThresholdBytes = 100L * 1024 * 1024;
            if (driveInfo.AvailableFreeSpace >= lowDiskThresholdBytes)
                return;

            Log.Warning("磁盘空间不足: {Drive} 可用 {AvailableMB:F1} MB，自动暂停录制",
                root, driveInfo.AvailableFreeSpace / (1024.0 * 1024.0));

            if (!IsPaused)
                TogglePauseCommand.Execute(null);

            AppMessageBox.Show(
                $"磁盘 {root} 可用空间不足 100 MB，录制已自动暂停。\n请释放磁盘空间后手动点击「继续」恢复录制。",
                "磁盘空间不足",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "检查磁盘空间失败");
        }
    }
}
