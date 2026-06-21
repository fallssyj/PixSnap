using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSnap.Services;
using PixSnap.Views;
using Serilog;
using System.IO;
using System.Windows;

namespace PixSnap.ViewModels;

public partial class VideoPlayerViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string? _tempFilePath;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private TimeSpan _position;

    [ObservableProperty]
    private TimeSpan _duration;

    [ObservableProperty]
    private string _positionText = "00:00";

    [ObservableProperty]
    private string _durationText = "00:00";

    [ObservableProperty]
    private double _sliderValue;

    [ObservableProperty]
    private double _volume = 1.0;

    [ObservableProperty]
    private bool _isMuted;

    /// <summary>拖动进度条时为 true，阻止定时器更新。</summary>
    [ObservableProperty]
    private bool _isDragging;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isSaving;

    private bool CanSave() => TempFilePath is not null && File.Exists(TempFilePath) && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (TempFilePath is null) return;

        var dir = SettingsService.ReadSaveDirectory();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存录屏",
            Filter = "MP4 视频|*.mp4",
            FileName = $"PixSnap_{DateTime.Now:yyyyMMdd_HHmmss}.mp4",
            InitialDirectory = Directory.Exists(dir) ? dir : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        if (dialog.ShowDialog() != true) return;

        IsSaving = true;
        try
        {
            var src = TempFilePath;
            var dest = dialog.FileName;
            await Task.Run(() => File.Copy(src, dest, overwrite: true));
            Log.Information("录屏已保存: {Path}", dest);
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存录屏失败");
            AppMessageBox.Show(
                $"保存失败：{ex.Message}",
                "PixSnap 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsSaving = false;
        }
    }

    public event Action? CloseRequested;

    /// <summary>请求 View 层执行 MediaElement 播放/暂停切换。</summary>
    public event Action? PlayPauseRequested;

    /// <summary>请求 View 层将 MediaElement.Position 跳转到指定时间。</summary>
    public event Action<TimeSpan>? SeekRequested;

    /// <summary>请求 View 层更新 MediaElement.Volume。</summary>
    public event Action<double>? VolumeChanged;

    [RelayCommand]
    private void PlayPause() => PlayPauseRequested?.Invoke();

    [RelayCommand]
    private void DragStarted() => IsDragging = true;

    [RelayCommand]
    private void DragCompleted() => IsDragging = false;

    [RelayCommand]
    private void SliderClick() => RequestSeekToSlider();

    [RelayCommand]
    private void ToggleMute()
    {
        IsMuted = !IsMuted;
    }

    private void RequestSeekToSlider()
    {
        if (Duration.TotalSeconds > 0)
        {
            var target = TimeSpan.FromSeconds(SliderValue * Duration.TotalSeconds);
            SeekRequested?.Invoke(target);
        }
    }

    partial void OnSliderValueChanged(double value)
    {
        // 拖拽或点击时实时 Seek，让画面跟随
        if (IsDragging)
            RequestSeekToSlider();
    }

    partial void OnIsMutedChanged(bool value)
    {
        VolumeChanged?.Invoke(value ? 0 : Volume);
    }

    partial void OnVolumeChanged(double value)
    {
        if (!IsMuted)
            VolumeChanged?.Invoke(value);
    }

    public void UpdatePosition(TimeSpan position)
    {
        if (IsDragging) return;
        Position = position;
        SliderValue = Duration.TotalSeconds > 0 ? position.TotalSeconds / Duration.TotalSeconds : 0;
    }

    public void OnMediaEnded()
    {
        IsPlaying = false;
    }

    partial void OnPositionChanged(TimeSpan value) => PositionText = FormatTime(value);
    partial void OnDurationChanged(TimeSpan value) => DurationText = FormatTime(value);

    private static string FormatTime(TimeSpan ts) =>
        ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
}
