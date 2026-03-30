using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PixSnap.ViewModels;

public partial class NotificationViewModel : ObservableObject
{
    private const int AutoCloseDurationSeconds = 10;
    private readonly DispatcherTimer _timer;
    private readonly BitmapSource _screenshot;
    private readonly string _captureMode;

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    [ObservableProperty]
    private string _imageSizeText = string.Empty;

    [ObservableProperty]
    private string _captureTimeText = string.Empty;

    [ObservableProperty]
    private double _remainingProgress = 1.0;

    /// <summary>用户点击通知，请求打开预览窗口编辑截图。</summary>
    public event Action<BitmapSource, string>? OpenRequested;

    /// <summary>请求关闭宿主窗口。</summary>
    public event Action? CloseRequested;

    public NotificationViewModel(BitmapSource screenshot, string captureMode)
    {
        _screenshot = screenshot;
        _captureMode = captureMode;

        Thumbnail = screenshot;
        ImageSizeText = $"{screenshot.PixelWidth} × {screenshot.PixelHeight}";
        CaptureTimeText = DateTime.Now.ToString("HH:mm:ss");

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTimerTick;
    }

    private DateTime _startTime;

    public void Start()
    {
        _startTime = DateTime.Now;
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var elapsed = (DateTime.Now - _startTime).TotalSeconds;
        RemainingProgress = Math.Max(0, 1.0 - elapsed / AutoCloseDurationSeconds);

        if (elapsed >= AutoCloseDurationSeconds)
        {
            _timer.Stop();
            CloseRequested?.Invoke();
        }
    }

    [RelayCommand]
    private void Open()
    {
        _timer.Stop();
        OpenRequested?.Invoke(_screenshot, _captureMode);
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Dismiss()
    {
        _timer.Stop();
        CloseRequested?.Invoke();
    }

    public void Cleanup()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }
}
