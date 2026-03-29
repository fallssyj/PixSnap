using Serilog;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PixSnap.Views;

public partial class RecordingControlWindow : Window
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    private readonly Func<Task> _stopAction;
    private readonly Action _pauseAction;
    private readonly Action _resumeAction;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _diskCheckTimer;
    private readonly DateTime _startTime;
    private bool _isPaused;
    private bool _isStopping;
    private TimeSpan _pausedElapsed;
    private DateTime _pauseBeginTime;

    public RecordingControlWindow(Func<Task> stopAction, Action pauseAction, Action resumeAction)
    {
        Log.Information("[RecordingControlWindow] 构造函数开始");
        InitializeComponent();
        Log.Information("[RecordingControlWindow] InitializeComponent 完成");
        _stopAction = stopAction;
        _pauseAction = pauseAction;
        _resumeAction = resumeAction;
        _startTime = DateTime.Now;

        // 定位到屏幕顶部居中
        var screen = SystemParameters.WorkArea;
        Left = (screen.Width - Width) / 2;
        Top = 16;

        // 录制指示器闪烁动画
        var blink = new DoubleAnimation(1.0, 0.3, TimeSpan.FromMilliseconds(800))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        RecordingDot.BeginAnimation(OpacityProperty, blink);

        // 计时器每秒更新
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        // 磁盘空间检查（每 5 秒）
        _diskCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _diskCheckTimer.Tick += OnDiskCheckTick;
        _diskCheckTimer.Start();

        SourceInitialized += (_, _) =>
        {
            // 设置窗口显示亲和力，防止被屏幕录制软件捕获
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        };

        Log.Information("[RecordingControlWindow] 构造函数完成");
    }

    public string? OutputFilePath { get; set; }

    public void ShowAudioWarning()
    {
        AudioWarningIcon.Visibility = Visibility.Visible;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_isPaused) return;
        var elapsed = DateTime.Now - _startTime - _pausedElapsed;
        TimerText.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    private void OnDiskCheckTick(object? sender, EventArgs e)
    {
        if (_isPaused || string.IsNullOrEmpty(OutputFilePath)) return;

        try
        {
            var root = Path.GetPathRoot(OutputFilePath);
            if (string.IsNullOrEmpty(root)) return;

            var driveInfo = new DriveInfo(root);
            if (!driveInfo.IsReady) return;

            const long lowDiskThresholdBytes = 100L * 1024 * 1024; // 100 MB
            if (driveInfo.AvailableFreeSpace < lowDiskThresholdBytes)
            {
                Log.Warning("磁盘空间不足: {Drive} 可用 {AvailableMB:F1} MB，自动暂停录制",
                    root, driveInfo.AvailableFreeSpace / (1024.0 * 1024.0));

                // 自动暂停
                if (!_isPaused)
                {
                    _pauseBeginTime = DateTime.Now;
                    _isPaused = true;
                    _pauseAction();
                    PauseButton.Content = "继续";

                    RecordingDot.BeginAnimation(OpacityProperty, null);
                    RecordingDot.Opacity = 1.0;
                    RecordingDot.Fill = new SolidColorBrush(Colors.Gray);
                }

                MessageBoxWindow.Show(
                    $"磁盘 {root} 可用空间不足 100 MB，录制已自动暂停。\n请释放磁盘空间后手动点击「继续」恢复录制。",
                    "磁盘空间不足",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "检查磁盘空间失败");
        }
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPaused)
        {
            // 恢复录制
            _pausedElapsed += DateTime.Now - _pauseBeginTime;
            _isPaused = false;
            _resumeAction();
            PauseButton.Content = "暂停";

            // 恢复闪烁动画
            var blink = new DoubleAnimation(1.0, 0.3, TimeSpan.FromMilliseconds(800))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            RecordingDot.BeginAnimation(OpacityProperty, blink);
            RecordingDot.Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
        }
        else
        {
            // 暂停录制
            _pauseBeginTime = DateTime.Now;
            _isPaused = true;
            _pauseAction();
            PauseButton.Content = "继续";

            // 停止闪烁，改为灰色
            RecordingDot.BeginAnimation(OpacityProperty, null);
            RecordingDot.Opacity = 1.0;
            RecordingDot.Fill = new SolidColorBrush(Colors.Gray);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isStopping) return;
        _isStopping = true;

        _timer.Stop();
        _diskCheckTimer.Stop();

        // 显示编码中状态
        StopButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        RecordingDot.BeginAnimation(OpacityProperty, null);
        RecordingDot.Opacity = 1.0;
        RecordingDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00)); // 橙色
        TimerText.Text = "正在编码...";

        try
        {
            await _stopAction();
        }
        finally
        {
            Close();
        }
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _diskCheckTimer.Stop();
        base.OnClosed(e);
    }
}
