using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSnap.Models;
using System.Windows;

namespace PixSnap.ViewModels;

// ViewModel 只维护可绑定的选择语义和提示文本，不参与 Canvas 坐标或控件可视化细节。
public partial class RegionSelectorViewModel : ObservableObject
{
    [ObservableProperty]
    private Rect _selectionRect;

    [ObservableProperty]
    private Rect _highlightRect;

    [ObservableProperty]
    private string _selectionText = "移动鼠标以选择截图对象";

    [ObservableProperty]
    private string _instructionText = "悬停窗口后左键单击截窗口，按住左键拖动截矩形，Space 截当前显示器，Esc 或右键退出";

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

    public string FooterHintText => IsRecordingMode
        ? "左键单击高亮窗口录屏，左键拖动录矩形，Space 录当前显示器，Tab 切换模式，Esc / 右键退出"
        : "左键单击高亮窗口截图，左键拖动截矩形，Space 截当前显示器，Tab 切换模式，Esc / 右键退出";

    private string ActionVerb => IsRecordingMode ? "录制" : "截取";
    private string ObjectNoun => IsRecordingMode ? "录屏" : "截图";

    partial void OnIsRecordingModeChanged(bool value)
    {
        if (SuppressRecordingMode && value)
        {
            IsRecordingMode = false;
            return;
        }

        OnPropertyChanged(nameof(ShowRecordingOptions));
        OnPropertyChanged(nameof(FooterHintText));
        RefreshSelectionHint();
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

    public void UpdateHover(WindowInfo? window, ScreenInfo? screen)
    {
        SelectionText = window is not null
            ? string.Format("单击{0}窗口: {1}", ActionVerb, window.Title)
            : screen is not null
                ? string.Format("按 Space {0}显示器 {1}", ActionVerb, screen.Index + 1)
                : string.Format("移动鼠标以选择{0}对象", ObjectNoun);
    }

    public void UpdateSelection(Rect selection)
    {
        SelectionRect = selection;
        SelectionText = selection.Width > 0 && selection.Height > 0
            ? string.Format("区域 X:{0:0} Y:{1:0} W:{2:0} H:{3:0}", selection.X, selection.Y, selection.Width, selection.Height)
            : string.Format("拖动以选择{0}区域", ObjectNoun);
    }

    // 高亮窗口的语义状态保留在 ViewModel 中，具体怎么画由 View 决定。
    public void UpdateWindowHighlight(Rect highlight)
    {
        HighlightRect = highlight;
    }

    public void ClearSelection()
    {
        SelectionRect = Rect.Empty;
    }

    public void ClearHighlight()
    {
        HighlightRect = Rect.Empty;
    }

    private void RefreshSelectionHint()
    {
        SelectionText = string.Format("移动鼠标以选择{0}对象", ObjectNoun);
    }
}