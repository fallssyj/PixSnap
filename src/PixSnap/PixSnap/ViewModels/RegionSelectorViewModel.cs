using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSnap.Models;
using System.Windows;

namespace PixSnap.ViewModels;

// ViewModel 只维护可绑定的选择语义，不参与 Canvas 坐标或控件可视化细节。
public partial class RegionSelectorViewModel : ObservableObject
{
    [ObservableProperty]
    private Rect _highlightRect;

    [ObservableProperty]
    private bool _isRecordingMode;

    /// <summary>录屏已在进行时，选区器仅允许截图，禁止再次启动录屏。</summary>
    public bool SuppressRecordingMode { get; set; }

    [ObservableProperty]
    private bool _enableMicrophone;

    [ObservableProperty]
    private bool _enableSystemAudio = true;

    [ObservableProperty]
    private RecordingQuality _recordingQuality = RecordingQuality.Original;

    public bool ShowRecordingOptions => IsRecordingMode;

    partial void OnIsRecordingModeChanged(bool value)
    {
        if (SuppressRecordingMode && value)
        {
            IsRecordingMode = false;
            return;
        }

        OnPropertyChanged(nameof(ShowRecordingOptions));
    }

    partial void OnRecordingQualityChanged(RecordingQuality value)
    {
        OnPropertyChanged(nameof(IsQualityStandard));
        OnPropertyChanged(nameof(IsQualityHigh));
        OnPropertyChanged(nameof(IsQualityOriginal));
    }

    public bool IsQualityStandard => RecordingQuality == RecordingQuality.Standard;
    public bool IsQualityHigh => RecordingQuality == RecordingQuality.High;
    public bool IsQualityOriginal => RecordingQuality == RecordingQuality.Original;

    [RelayCommand]
    private void SelectScreenshotMode() => IsRecordingMode = false;

    [RelayCommand]
    private void SelectRecordingMode()
    {
        if (!SuppressRecordingMode)
            IsRecordingMode = true;
    }

    [RelayCommand]
    private void ToggleMicrophone() => EnableMicrophone = !EnableMicrophone;

    [RelayCommand]
    private void ToggleSystemAudio() => EnableSystemAudio = !EnableSystemAudio;

    [RelayCommand]
    private void SetQualityStandard() => RecordingQuality = RecordingQuality.Standard;

    [RelayCommand]
    private void SetQualityHigh() => RecordingQuality = RecordingQuality.High;

    [RelayCommand]
    private void SetQualityOriginal() => RecordingQuality = RecordingQuality.Original;

    public void UpdateWindowHighlight(Rect highlight)
    {
        HighlightRect = highlight;
    }

    public void ClearHighlight()
    {
        HighlightRect = Rect.Empty;
    }
}
