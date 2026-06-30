using System.Windows;
using System.Windows.Threading;
using PixSnap.Services;
using PixSnap.ViewModels;

namespace PixSnap.Views;

public partial class VideoPlayerWindow
{
    private readonly DispatcherTimer _positionTimer;

    public VideoPlayerWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _positionTimer.Tick += OnPositionTimerTick;
    }

    private VideoPlayerViewModel? ViewModel => DataContext as VideoPlayerViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        vm.CloseRequested += OnCloseRequested;
        vm.PlayPauseRequested += OnPlayPauseRequested;
        vm.SeekRequested += OnSeekRequested;
        vm.VolumeChanged += OnVolumeChanged;

        if (vm.TempFilePath is not { } path) return;

        VideoPlayer.Source = new Uri(path);
        VideoPlayer.MediaOpened += OnMediaOpened;
        VideoPlayer.MediaEnded += OnMediaEnded;
        VideoPlayer.Play();
        vm.IsPlaying = true;
        _positionTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _positionTimer.Stop();

        var tempFilePath = ViewModel?.TempFilePath;

        // 取消订阅 ViewModel 事件
        if (ViewModel is { } vm)
        {
            vm.CloseRequested -= OnCloseRequested;
            vm.PlayPauseRequested -= OnPlayPauseRequested;
            vm.SeekRequested -= OnSeekRequested;
            vm.VolumeChanged -= OnVolumeChanged;
        }

        // 取消订阅 MediaElement 事件
        VideoPlayer.MediaOpened -= OnMediaOpened;
        VideoPlayer.MediaEnded -= OnMediaEnded;

        // 释放 MediaElement 资源（先释放文件句柄，再删除临时录屏）
        VideoPlayer.Stop();
        VideoPlayer.Close();
        VideoPlayer.Source = null;

        // 断开可视化树 + DataContext
        Content = null;
        DataContext = null;

        if (tempFilePath is not null)
            RecordingTempFileService.TryDelete(tempFilePath);

        // GC 回收 + 释放工作集
        MemoryManagementService.TrimAfterUiRelease();
    }

    private void OnCloseRequested() => Dispatcher.Invoke(Close);
    private void OnPlayPauseRequested() => Dispatcher.Invoke(TogglePlayPause);
    private void OnSeekRequested(TimeSpan t) => Dispatcher.Invoke(() => VideoPlayer.Position = t);
    private void OnVolumeChanged(double v) => Dispatcher.Invoke(() => VideoPlayer.Volume = v);

    private void OnMediaOpened(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && VideoPlayer.NaturalDuration.HasTimeSpan)
            vm.Duration = VideoPlayer.NaturalDuration.TimeSpan;
    }

    private void OnMediaEnded(object? sender, RoutedEventArgs e)
    {
        VideoPlayer.Stop();
        ViewModel?.OnMediaEnded();
        _positionTimer.Stop();
    }

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        if (ViewModel is { } vm && VideoPlayer.NaturalDuration.HasTimeSpan)
            vm.UpdatePosition(VideoPlayer.Position);
    }

    private void TogglePlayPause()
    {
        if (ViewModel is not { } vm) return;

        if (vm.IsPlaying)
        {
            VideoPlayer.Pause();
            vm.IsPlaying = false;
            _positionTimer.Stop();
        }
        else
        {
            VideoPlayer.Play();
            vm.IsPlaying = true;
            _positionTimer.Start();
        }
    }

}
