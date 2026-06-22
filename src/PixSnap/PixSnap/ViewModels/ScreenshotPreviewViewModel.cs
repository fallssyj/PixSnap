using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PixSnap.Models;
using PixSnap.Services;
using PixSnap.Views;
using Serilog;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace PixSnap.ViewModels;

/// <summary>截图预览窗口的编辑模式，各模式互斥，同一时刻只有一种可激活。</summary>
public enum EditMode { None, Crop, RoundCorner, Eraser, Annotate }

public sealed partial class AnnotationToolItem : ObservableObject
{
    public required AnnotationTool Tool { get; init; }
    public required string Glyph { get; init; }
    public required string ToolTip { get; init; }
    public required ICommand Command { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class AnnotationColorItem : ObservableObject
{
    public required string ColorValue { get; init; }
    public required Color WpfColor { get; init; }
    public required string ToolTip { get; init; }
    public required ICommand Command { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}

internal enum AnnotationExitChoice
{
    Apply,
    Discard,
    Cancel
}

public sealed class CropAspectRatioPresetItem
{
    public required string Content { get; init; }
    public required string Parameter { get; init; }
    public required string ToolTip { get; init; }
}

public partial class ScreenshotPreviewViewModel : ObservableRecipient, IRecipient<ScreenshotCapturedMessage>
{
    public const double MinZoomFactor = 0.1;
    public const double MaxZoomFactor = 8.0;
    private const long ExactFileSizePixelThreshold = 16_000_000;

    private readonly INavigationService _navigation;
    private readonly UndoRedoManager _history = new();
    private readonly SemaphoreSlim _imageApplySemaphore = new(1, 1);
    private CancellationTokenSource? _fileSizeUpdateCts;
    private CancellationTokenSource? _aiCts;
    private bool _copyAfterAnnotationApply;
    private bool _isClosing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveToFileCommand))]
    private BitmapSource? _screenshotImage;

    [ObservableProperty]
    private bool _isActualSize;

    [ObservableProperty]
    private double _zoomFactor = 1.0;

    [ObservableProperty]
    private double _fitZoomFactor = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureTitleDisplay))]
    private string _captureTime = string.Empty;

    [ObservableProperty]
    private string _imageSize = string.Empty;

    [ObservableProperty]
    private string _fileSize = string.Empty;

    [ObservableProperty]
    private string _captureMode = string.Empty;

    [ObservableProperty]
    private bool _isPinned;

    /// <summary>AI 功能弹出菜单是否展开。双向绑定到 Popup.IsOpen。</summary>
    [ObservableProperty]
    private bool _isAiPopupOpen;

    /// <summary>OCR 识别结果是否显示在图片上。</summary>
    [ObservableProperty]
    private bool _isOcrOverlayVisible;

    public ObservableCollection<OcrTextRegion> OcrRegions { get; } = [];

    public string OcrRegionCountText => OcrRegions.Count > 0
        ? string.Format("已识别 {0} 处文字", OcrRegions.Count)
        : "扫描文本";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyAiProcessing))]
    [NotifyPropertyChangedFor(nameof(ActiveAiProgressText))]
    [NotifyPropertyChangedFor(nameof(ActiveAiProgress))]
    private bool _isAiModuleProcessing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveAiProgress))]
    private double _aiModuleProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveAiProgressText))]
    private string _aiModuleProgressText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyAiProcessing))]
    [NotifyPropertyChangedFor(nameof(IsAnyIndeterminateProcessing))]
    [NotifyPropertyChangedFor(nameof(ActiveAiProgressText))]
    private bool _isRotating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyAiProcessing))]
    [NotifyPropertyChangedFor(nameof(ActiveAiProgressText))]
    [NotifyPropertyChangedFor(nameof(ActiveAiProgress))]
    private bool _isSaving;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyAiProcessing))]
    [NotifyPropertyChangedFor(nameof(ActiveAiProgressText))]
    [NotifyPropertyChangedFor(nameof(ActiveAiProgress))]
    private bool _isFileOperationProcessing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveAiProgress))]
    private double _fileOperationProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveAiProgressText))]
    private string _fileOperationProgressText = string.Empty;

    // ── 裁剪 / 圆角子 ViewModel ─────────────────────────────────────────────

    // ── 编辑模式（互斥单活）──────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyAnnotationAndCopyCommand))]
    private EditMode _activeEditMode;

    partial void OnActiveEditModeChanged(EditMode oldValue, EditMode newValue)
    {
        OnPropertyChanged(nameof(IsCropMode));
        OnPropertyChanged(nameof(IsRoundCornerMode));
        OnPropertyChanged(nameof(IsEraserMode));
        OnPropertyChanged(nameof(IsAnnotateMode));
        OnPropertyChanged(nameof(IsEditPanelVisible));
        OnPropertyChanged(nameof(IsTitleBarHistoryVisible));
        NotifyUndoRedoStateChanged();
        if (oldValue == EditMode.Eraser && newValue != EditMode.Eraser)
            EraserPanel.ClearStrokesCommand.Execute(null);
        if (newValue != EditMode.None)
            ClearOcrOverlay();
    }

    public bool IsCropMode => ActiveEditMode == EditMode.Crop;
    public bool IsRoundCornerMode => ActiveEditMode == EditMode.RoundCorner;
    public bool IsEraserMode => ActiveEditMode == EditMode.Eraser;
    public bool IsAnnotateMode => ActiveEditMode == EditMode.Annotate;
    public bool IsEditPanelVisible => ActiveEditMode != EditMode.None;

    /// <summary>标题栏撤销/重做：标注模式下隐藏，改用 dock 内历史操作。</summary>
    public bool IsTitleBarHistoryVisible => !IsAnnotateMode;

    public string ActiveAnnotationToolDisplayText => AnnotationPanel.SelectedTool switch
    {
        AnnotationTool.Pointer => "选择",
        AnnotationTool.Arrow => "箭头",
        AnnotationTool.Rectangle => "矩形",
        AnnotationTool.Ellipse => "椭圆",
        AnnotationTool.Text => "文本",
        AnnotationTool.Pen => "画笔",
        AnnotationTool.Blur => "模糊",
        _ => "标注"
    };

    /// <summary>退出当前编辑模式，回到浏览状态。</summary>
    public void ExitEditMode() => ActiveEditMode = EditMode.None;

    public CropViewModel CropPanel { get; } = new();
    public RoundCornerViewModel RoundCornerPanel { get; } = new();
    public EraserViewModel EraserPanel { get; } = new();
    public AnnotationViewModel AnnotationPanel { get; } = new();
    public ObservableCollection<AnnotationToolItem> AnnotationTools { get; } = [];
    public ObservableCollection<AnnotationColorItem> AnnotationColors { get; } = [];
    public ObservableCollection<CropAspectRatioPresetItem> CropAspectRatioPresets { get; } = [];

    [ObservableProperty]
    private bool _isCustomAnnotationColorSelected;

    public string PreviewScaleModeText => IsActualSize ? "缩放以适应" : "缩放以原始";
    public string ZoomDisplayText => string.Format("缩放 {0:P0}", IsActualSize ? ZoomFactor : FitZoomFactor);
    public string ZoomCompactText
    {
        get
        {
            var percent = (IsActualSize ? ZoomFactor : FitZoomFactor) * 100.0;
            return $"{Math.Round(percent):0}%";
        }
    }
    public string CaptureTitleDisplay => MiddleEllipsis(CaptureTime, 24);
    public bool IsAnyAiProcessing => EraserPanel.IsProcessing || IsAiModuleProcessing || IsFileOperationProcessing || CropPanel.IsCropProcessing || RoundCornerPanel.IsProcessing || IsRotating;

    /// <summary>当前处理属于不定进度类型（裁剪、圆角或旋转），进度条应显示不确定动画。</summary>
    public bool IsAnyIndeterminateProcessing => CropPanel.IsCropProcessing || RoundCornerPanel.IsProcessing || IsRotating;

    public string ActiveAiProgressText
        => CropPanel.IsCropProcessing
            ? "正在裁剪..."
            : RoundCornerPanel.IsProcessing
                ? "正在处理圆角..."
                : IsRotating
                    ? "正在旋转图片..."
                    : EraserPanel.IsProcessing
                        ? EraserPanel.ProgressText
                        : IsAiModuleProcessing
                            ? AiModuleProgressText
                            : FileOperationProgressText;
    public double ActiveAiProgress
        => EraserPanel.IsProcessing
            ? EraserPanel.Progress
            : IsAiModuleProcessing
                ? AiModuleProgress
                : FileOperationProgress;
    public double ZoomSliderValue
    {
        get => (IsActualSize ? ZoomFactor : FitZoomFactor) * 100.0;
        set => SetManualZoomFactor(value / 100.0);
    }

    public ScreenshotPreviewViewModel(INavigationService navigation)
    {
        _navigation = navigation;
        AnnotationTools =
        [
            new() { Tool = AnnotationTool.Pointer, Glyph = "\uE8B0", ToolTip = "选择 / 移动 (V)", Command = AnnotationPanel.SelectPointerCommand },
            new() { Tool = AnnotationTool.Arrow, Glyph = "\uE76C", ToolTip = "箭头 (A)", Command = AnnotationPanel.SelectArrowCommand },
            new() { Tool = AnnotationTool.Rectangle, Glyph = "\uE739", ToolTip = "矩形 (R)", Command = AnnotationPanel.SelectRectangleCommand },
            new() { Tool = AnnotationTool.Ellipse, Glyph = "\uEA3A", ToolTip = "椭圆 (E)", Command = AnnotationPanel.SelectEllipseCommand },
            new() { Tool = AnnotationTool.Text, Glyph = "\uE8D2", ToolTip = "文本 (T)", Command = AnnotationPanel.SelectTextCommand },
            new() { Tool = AnnotationTool.Pen, Glyph = "\uEE56", ToolTip = "画笔 (P)", Command = AnnotationPanel.SelectPenCommand },
            new() { Tool = AnnotationTool.Blur, Glyph = "\uED5B", ToolTip = "模糊 (M)", Command = AnnotationPanel.SelectBlurCommand }
        ];
        AnnotationColors =
        [
            new() { ColorValue = "#FFFF0000", WpfColor = Colors.Red, ToolTip = "红色", Command = AnnotationPanel.SetColorRedCommand },
            new() { ColorValue = "#FF3399FF", WpfColor = Color.FromRgb(0x33, 0x99, 0xFF), ToolTip = "蓝色", Command = AnnotationPanel.SetColorBlueCommand },
            new() { ColorValue = "#FF22CC55", WpfColor = Color.FromRgb(0x22, 0xCC, 0x55), ToolTip = "绿色", Command = AnnotationPanel.SetColorGreenCommand },
            new() { ColorValue = "#FFFFCC00", WpfColor = Color.FromRgb(0xFF, 0xCC, 0x00), ToolTip = "黄色", Command = AnnotationPanel.SetColorYellowCommand },
            new() { ColorValue = "#FFFFFFFF", WpfColor = Colors.White, ToolTip = "白色", Command = AnnotationPanel.SetColorWhiteCommand },
            new() { ColorValue = "#FF000000", WpfColor = Colors.Black, ToolTip = "黑色", Command = AnnotationPanel.SetColorBlackCommand },
            new() { ColorValue = "#FFFF8800", WpfColor = Color.FromRgb(0xFF, 0x88, 0x00), ToolTip = "橙色", Command = AnnotationPanel.SetColorOrangeCommand },
            new() { ColorValue = "#FFAA44FF", WpfColor = Color.FromRgb(0xAA, 0x44, 0xFF), ToolTip = "紫色", Command = AnnotationPanel.SetColorPurpleCommand },
            new() { ColorValue = "#FFFF66B2", WpfColor = Color.FromRgb(0xFF, 0x66, 0xB2), ToolTip = "粉色", Command = AnnotationPanel.SetColorPinkCommand },
            new() { ColorValue = "#FF888888", WpfColor = Color.FromRgb(0x88, 0x88, 0x88), ToolTip = "灰色", Command = AnnotationPanel.SetColorGrayCommand }
        ];
        CropAspectRatioPresets =
        [
            new() { Content = "自由", Parameter = "0:0", ToolTip = "自由裁剪" },
            new() { Content = "1:1", Parameter = "1:1:1:1", ToolTip = "正方形" },
            new() { Content = "16:9", Parameter = "16:9:16:9", ToolTip = "宽屏" },
            new() { Content = "4:3", Parameter = "4:3:4:3", ToolTip = "标准" },
            new() { Content = "3:2", Parameter = "3:2:3:2", ToolTip = "相机" }
        ];

        // 不向全局 Messenger 注册：每个预览窗口只对应一次截图，
        // 由 App 直接调用 Receive() 初始化，无需监听后续广播。
        EraserPanel.InpaintApplied += OnEraserApplied;
        EraserPanel.PropertyChanged += OnEraserPanelPropertyChanged;
        CropPanel.PropertyChanged += OnCropPanelPropertyChanged;
        RoundCornerPanel.PropertyChanged += OnRoundCornerPanelPropertyChanged;
        AnnotationPanel.PropertyChanged += OnAnnotationPanelPropertyChanged;
        AnnotationPanel.AnnotationApplied += OnAnnotationApplied;
        AnnotationPanel.AnnotationCancelled += OnAnnotationCancelled;
        UpdateAnnotationToolSelection();
        UpdateAnnotationColorSelection();
    }

    private void OnAnnotationCancelled() => ExitEditMode();

    private void OnAnnotationPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnnotationViewModel.SelectedTool))
        {
            UpdateAnnotationToolSelection();
            OnPropertyChanged(nameof(ActiveAnnotationToolDisplayText));
        }
        else if (e.PropertyName == nameof(AnnotationViewModel.StrokeColor))
        {
            UpdateAnnotationColorSelection();
        }
        else if (e.PropertyName is nameof(AnnotationViewModel.CanUndo) or nameof(AnnotationViewModel.CanRedo))
        {
            NotifyUndoRedoStateChanged();
        }
    }

    private void UpdateAnnotationToolSelection()
    {
        foreach (var tool in AnnotationTools)
        {
            tool.IsSelected = tool.Tool == AnnotationPanel.SelectedTool;
        }
    }

    private void UpdateAnnotationColorSelection()
    {
        var current = AnnotationPanel.StrokeColor;
        var anyPreset = false;
        foreach (var color in AnnotationColors)
        {
            var match = color.WpfColor.R == current.R
                && color.WpfColor.G == current.G
                && color.WpfColor.B == current.B;
            color.IsSelected = match;
            if (match)
                anyPreset = true;
        }

        IsCustomAnnotationColorSelected = !anyPreset;
    }

    /// <summary>退出标注模式前确认未应用内容；返回 false 表示用户选择继续编辑。</summary>
    private async Task<bool> TryLeaveAnnotationModeAsync()
    {
        if (ActiveEditMode != EditMode.Annotate)
            return true;

        if (!AnnotationPanel.HasPendingAnnotations)
        {
            ActiveEditMode = EditMode.None;
            return true;
        }

        var choice = PromptAnnotationExitChoice();
        switch (choice)
        {
            case AnnotationExitChoice.Apply:
                await ApplyAnnotation();
                return ActiveEditMode != EditMode.Annotate;
            case AnnotationExitChoice.Discard:
                AnnotationPanel.ClearAnnotationsCommand.Execute(null);
                ActiveEditMode = EditMode.None;
                return true;
            default:
                return false;
        }
    }

    private static AnnotationExitChoice PromptAnnotationExitChoice()
    {
        var result = AppMessageBox.Show(
            "当前标注尚未应用到图片。\n\n是 — 应用标注并退出\n否 — 放弃标注\n取消 — 继续编辑",
            "未应用的标注",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return result switch
        {
            MessageBoxResult.Yes => AnnotationExitChoice.Apply,
            MessageBoxResult.No => AnnotationExitChoice.Discard,
            _ => AnnotationExitChoice.Cancel
        };
    }

    private void OnEraserPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EraserViewModel.IsProcessing)
            or nameof(EraserViewModel.Progress)
            or nameof(EraserViewModel.ProgressText))
        {
            OnPropertyChanged(nameof(IsAnyAiProcessing));
            OnPropertyChanged(nameof(ActiveAiProgressText));
            OnPropertyChanged(nameof(ActiveAiProgress));
        }
    }

    private void OnCropPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CropViewModel.IsCropProcessing))
        {
            OnPropertyChanged(nameof(IsAnyAiProcessing));
            OnPropertyChanged(nameof(IsAnyIndeterminateProcessing));
            OnPropertyChanged(nameof(ActiveAiProgressText));
            OnPropertyChanged(nameof(ActiveAiProgress));
        }
    }

    private void OnRoundCornerPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RoundCornerViewModel.IsProcessing))
        {
            OnPropertyChanged(nameof(IsAnyAiProcessing));
            OnPropertyChanged(nameof(IsAnyIndeterminateProcessing));
            OnPropertyChanged(nameof(ActiveAiProgressText));
            OnPropertyChanged(nameof(ActiveAiProgress));
        }
    }

    private void OnEraserApplied(BitmapSource result)
    {
        _ = ApplyEditedImageAsync(result);
    }

    private async void OnAnnotationApplied(BitmapSource result)
    {
        // 先应用新图片，再退出标注模式，避免标注层先清空而新图未到导致闪烁
        await ApplyEditedImageAsync(result, switchToFit: false);
        ActiveEditMode = EditMode.None;
        if (_copyAfterAnnotationApply)
        {
            _copyAfterAnnotationApply = false;
            if (ScreenshotImage is not null)
                ClipboardHelper.TrySetImage(ScreenshotImage);
        }
    }

    partial void OnIsActualSizeChanged(bool value)
    {
        OnPropertyChanged(nameof(PreviewScaleModeText));
        OnPropertyChanged(nameof(ZoomDisplayText));
        OnPropertyChanged(nameof(ZoomCompactText));
        OnPropertyChanged(nameof(ZoomSliderValue));
    }

    partial void OnZoomFactorChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomDisplayText));
        OnPropertyChanged(nameof(ZoomCompactText));
        OnPropertyChanged(nameof(ZoomSliderValue));
    }

    partial void OnFitZoomFactorChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomDisplayText));
        OnPropertyChanged(nameof(ZoomCompactText));
        OnPropertyChanged(nameof(ZoomSliderValue));
    }

    partial void OnIsSavingChanged(bool value)
    {
        SaveToFileCommand.NotifyCanExecuteChanged();
    }

    public void SetManualZoomFactor(double zoomFactor)
    {
        ZoomFactor = Math.Clamp(zoomFactor, MinZoomFactor, MaxZoomFactor);
        IsActualSize = true;
    }

    public void SwitchToFitMode()
    {
        ZoomFactor = FitZoomFactor;
        IsActualSize = false;
    }

    private static string MiddleEllipsis(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength || maxLength < 7)
            return text ?? string.Empty;

        var ext = Path.GetExtension(text);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(text);

        if (string.IsNullOrEmpty(ext))
        {
            int head = (maxLength - 3) / 2;
            int tail = maxLength - 3 - head;
            return $"{text[..head]}...{text[^tail..]}";
        }

        int bodyLimit = Math.Max(4, maxLength - 3 - ext.Length);
        if (nameWithoutExt.Length <= bodyLimit)
            return text;

        int bodyHead = bodyLimit / 2;
        int bodyTail = bodyLimit - bodyHead;
        return $"{nameWithoutExt[..bodyHead]}...{nameWithoutExt[^bodyTail..]}{ext}";
    }

    /// <summary>换图或旋转前：确认退出标注/擦除等编辑模式。</summary>
    private async Task<bool> PrepareForImageReplaceAsync()
    {
        if (!await TryLeaveAnnotationModeAsync())
            return false;

        if (ActiveEditMode == EditMode.Eraser)
        {
            EraserPanel.CancelInpaintCommand.Execute(null);
            EraserPanel.ClearStrokesCommand.Execute(null);
        }

        ActiveEditMode = EditMode.None;
        return true;
    }

    [RelayCommand]
    private async Task OpenFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg|PNG 文件|*.png|JPEG 文件|*.jpg;*.jpeg"
        };

        if (dialog.ShowDialog() != true) return;
        if (!await PrepareForImageReplaceAsync()) return;

        IsFileOperationProcessing = true;
        FileOperationProgress = 0.05;
        FileOperationProgressText = "正在加载图片...";

        try
        {
            // 先释放旧图像引用，再加载新图，减少同时在内存中的大位图数量
            ScreenshotImage = null;

            var progress = new Progress<(double Value, string Text)>(p =>
            {
                FileOperationProgress = Math.Clamp(p.Value, 0, 1);
                FileOperationProgressText = p.Text;
            });

            Log.Information("加载图片: {FilePath}", dialog.FileName);
            var bitmap = await ImageIOService.LoadBitmapFromFileAsync(dialog.FileName, progress);

            SetCurrentImage(bitmap, switchToFit: true);
            ResetHistory();
            CaptureTime = Path.GetFileName(dialog.FileName);
            FileSize = ImageIOService.FormatFileSize(new FileInfo(dialog.FileName).Length);
            FileOperationProgress = 1.0;
            FileOperationProgressText = "图片加载完成";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载图片失败: {FilePath}", dialog.FileName);
            AppMessageBox.Show(
                string.Format("加载失败：{0}", ex.Message),
                "PixSnap 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsFileOperationProcessing = false;
        }
    }

    private bool CanSaveToFile() => ScreenshotImage is not null && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanSaveToFile))]
    private async Task SaveToFile()
    {
        if (ScreenshotImage is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 文件|*.png|JPEG 文件|*.jpg|BMP 文件|*.bmp",
            FileName = $"PixSnap_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var format = System.IO.Path.GetExtension(dialog.FileName).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(format)) format = "png";

        var imageSnapshot = ImageIOService.CreateFrozenSnapshot(ScreenshotImage);
        IsSaving = true;
        IsFileOperationProcessing = true;
        FileOperationProgress = 0.1;
        FileOperationProgressText = "正在保存图片...";
        Log.Information("保存图片: {FilePath}", dialog.FileName);
        try
        {
            var progress = new Progress<(double Value, string Text)>(p =>
            {
                FileOperationProgress = Math.Clamp(p.Value, 0, 1);
                FileOperationProgressText = p.Text;
            });

            await ImageIOService.SaveAsync(imageSnapshot, dialog.FileName, format, progress);
            FileOperationProgress = 1.0;
            FileOperationProgressText = "图片保存完成";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存图片失败: {FilePath}", dialog.FileName);
            AppMessageBox.Show(
                string.Format("保存失败：{0}", ex.Message),
                "PixSnap 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsSaving = false;
            IsFileOperationProcessing = false;
        }
    }

    [RelayCommand]
    private async Task CopyToClipboard()
    {
        if (ScreenshotImage is null) return;

        if (IsAnnotateMode && AnnotationPanel.Annotations.Count > 0)
        {
            await ApplyAnnotationAndCopy();
            return;
        }

        ClipboardHelper.TrySetImage(ScreenshotImage);
    }

    [RelayCommand]
    private async Task PasteFromClipboard()
    {
        var image = ClipboardHelper.TryGetImage();
        if (image is null) return;
        if (!await PrepareForImageReplaceAsync()) return;

        if (!image.IsFrozen) image.Freeze();
        SetCurrentImage(image, switchToFit: true);
        ResetHistory();
        CaptureTime = "剪贴板图片";
        FileSize = ImageIOService.FormatFileSize((long)image.PixelWidth * image.PixelHeight * 4);
    }

    [RelayCommand]
    private void TogglePreviewScale()
    {
        if (IsActualSize)
        {
            SwitchToFitMode();
            return;
        }

        SetManualZoomFactor(1.0);
    }

    [RelayCommand]
    private async Task RotateImage()
    {
        if (ScreenshotImage is null) return;
        if (!await PrepareForImageReplaceAsync()) return;

        IsRotating = true;
        try
        {
            var source = ScreenshotImage;
            if (!source.IsFrozen) source.Freeze();

            // UI 线程：格式转换 + 像素拷贝（纯内存操作，不影响渲染）
            var pbgra = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            int srcW = pbgra.PixelWidth, srcH = pbgra.PixelHeight;
            int srcStride = srcW * 4;
            var srcPixels = new byte[srcStride * srcH];
            pbgra.CopyPixels(srcPixels, srcStride, 0);

            // 后台线程：像素级旋转，对 4K/8K 大图不阻塞 UI
            var (dstPixels, dstW, dstH) = await Task.Run(() => Rotate90Clockwise(srcPixels, srcW, srcH));

            // UI 线程：创建新 WriteableBitmap 并提交
            int dstStride = dstW * 4;
            var rotated = new WriteableBitmap(dstW, dstH, 96, 96, PixelFormats.Pbgra32, null);
            rotated.WritePixels(new System.Windows.Int32Rect(0, 0, dstW, dstH), dstPixels, dstStride, 0);
            rotated.Freeze();

            await ApplyEditedImageAsync(rotated, switchToFit: false);
        }
        finally
        {
            IsRotating = false;
        }
    }

    /// <summary>顺时针旋转 90°，返回新像素数组及新宽高。在后台线程安全执行。</summary>
    private static (byte[] pixels, int width, int height) Rotate90Clockwise(byte[] src, int srcW, int srcH)
    {
        // 顺时针 90°：目标 (x, y) = (srcH-1-srcY, srcX)，目标宽=srcH、高=srcW
        int dstW = srcH, dstH = srcW;
        int srcStride = srcW * 4, dstStride = dstW * 4;
        var dst = new byte[dstStride * dstH];
        for (int sy = 0; sy < srcH; sy++)
        {
            for (int sx = 0; sx < srcW; sx++)
            {
                int srcOffset = sy * srcStride + sx * 4;
                int dstOffset = sx * dstStride + (srcH - 1 - sy) * 4;
                dst[dstOffset] = src[srcOffset];
                dst[dstOffset + 1] = src[srcOffset + 1];
                dst[dstOffset + 2] = src[srcOffset + 2];
                dst[dstOffset + 3] = src[srcOffset + 3];
            }
        }
        return (dst, dstW, dstH);
    }

    [RelayCommand]
    private async Task ToggleCrop()
    {
        if (ScreenshotImage is null) return;
        if (ActiveEditMode == EditMode.Crop)
        {
            ActiveEditMode = EditMode.None;
            return;
        }

        if (!await TryLeaveAnnotationModeAsync()) return;
        SwitchToFitMode();
        ActiveEditMode = EditMode.Crop;
        CropPanel.Initialize(ScreenshotImage.PixelWidth, ScreenshotImage.PixelHeight);
    }

    [RelayCommand]
    private async Task ToggleRoundCorner()
    {
        if (ScreenshotImage is null) return;
        if (ActiveEditMode == EditMode.RoundCorner)
        {
            ActiveEditMode = EditMode.None;
            return;
        }

        if (!await TryLeaveAnnotationModeAsync()) return;
        ActiveEditMode = EditMode.RoundCorner;
    }

    [RelayCommand]
    private async Task ToggleEraser()
    {
        if (ScreenshotImage is null) return;
        if (ActiveEditMode == EditMode.Eraser)
        {
            ActiveEditMode = EditMode.None;
            return;
        }

        if (!await TryLeaveAnnotationModeAsync()) return;
        SwitchToFitMode();
        ActiveEditMode = EditMode.Eraser;
    }

    /// <summary>取消 AI 擦除的推理（如正在运行）并退出擦除编辑模式。</summary>
    [RelayCommand]
    private void ExitEraserMode()
    {
        EraserPanel.CancelInpaintCommand.Execute(null);
        ExitEditMode();
    }

    /// <summary>手动触发 AI 擦除推理。</summary>
    [RelayCommand]
    private async Task ApplyEraser(BitmapSource? image)
    {
        if (image is null) return;
        await EraserPanel.RunInpaintAsync(image);
    }

    [RelayCommand]
    private async Task ToggleAnnotation()
    {
        if (ScreenshotImage is null) return;
        if (ActiveEditMode == EditMode.Annotate)
        {
            if (!await TryLeaveAnnotationModeAsync())
                return;
        }
        else
        {
            // 标注层位于 ScrollViewer 内部，须切换到实际大小模式
            if (!IsActualSize)
            {
                ZoomFactor = FitZoomFactor;
                IsActualSize = true;
            }
            ActiveEditMode = EditMode.Annotate;
        }
    }

    [RelayCommand]
    private async Task ApplyAnnotation()
    {
        if (ScreenshotImage is null || AnnotationPanel.Annotations.Count == 0) return;
        await AnnotationPanel.ApplyAnnotationsAsync(ScreenshotImage);
    }

    [RelayCommand(CanExecute = nameof(CanApplyAnnotationAndCopy))]
    private async Task ApplyAnnotationAndCopy()
    {
        if (ScreenshotImage is null) return;

        if (AnnotationPanel.Annotations.Count > 0)
        {
            try
            {
                _copyAfterAnnotationApply = true;
                await AnnotationPanel.ApplyAnnotationsAsync(ScreenshotImage);
            }
            finally
            {
                if (_copyAfterAnnotationApply)
                    _copyAfterAnnotationApply = false;
            }
            return;
        }

        ClipboardHelper.TrySetImage(ScreenshotImage);
    }

    private bool CanApplyAnnotationAndCopy() => ScreenshotImage is not null && IsAnnotateMode;

    [RelayCommand]
    private async Task ExitAnnotationMode()
    {
        await TryLeaveAnnotationModeAsync();
    }


    /// <summary>执行背景去除 AI 功能。</summary>
    [RelayCommand]
    private async Task RemoveBackground()
    {
        if (ScreenshotImage is null || IsAnyAiProcessing) return;
        if (!await PrepareForImageReplaceAsync()) return;

        // 关闭弹出菜单（处理期间弹层将阻断操作）
        IsAiPopupOpen = false;
        IsAiModuleProcessing = true;
        AiModuleProgress = 0;
        AiModuleProgressText = "正在准备去除背景...";
        var token = CreateAiCancellationToken();
        try
        {
            var progress = new Progress<(double Value, string Text)>(t =>
            {
                AiModuleProgress = t.Value;
                AiModuleProgressText = t.Text;
            });

            var result = await BackgroundRemovalService.RunAsync(ScreenshotImage, progress, token);
            if (result is null) return;

            await ApplyEditedImageAsync(result);
            AiModuleProgress = 1;
            AiModuleProgressText = "去除背景完成";
        }
        catch (OperationCanceledException)
        {
            AiModuleProgressText = string.Empty;
        }
        catch (FileNotFoundException ex)
        {
            Log.Error(ex, "去除背景模型缺失");
            AiModuleProgressText = string.Empty;
            AppMessageBox.Show(
                string.Format("AI 模型文件缺失，无法执行去除背景。\n\n{0}", ex.Message),
                "模型缺失",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "去除背景失败");
            AiModuleProgressText = string.Empty;
            AppMessageBox.Show(
                string.Format("去除背景失败：{0}", ex.Message),
                "操作失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsAiModuleProcessing = false;
        }
    }

    /// <summary>离线 OCR：PaddleOCR 识别并在图片原位显示文字。</summary>
    [RelayCommand]
    private async Task RecognizeText()
    {
        if (ScreenshotImage is null || IsAnyAiProcessing) return;

        ClearOcrOverlay();
        IsAiPopupOpen = false;
        IsAiModuleProcessing = true;
        AiModuleProgress = 0;
        AiModuleProgressText = "正在准备 PaddleOCR...";
        var token = CreateAiCancellationToken();
        try
        {
            var progress = new Progress<(double Value, string Text)>(t =>
            {
                AiModuleProgress = t.Value;
                AiModuleProgressText = t.Text;
            });

            var result = await OcrService.RecognizeAsync(ScreenshotImage, progress, token);
            if (result.Regions.Count == 0)
            {
                AppMessageBox.Show(
                    "未在图片中识别到文字。",
                    "扫描文本",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            foreach (var region in result.Regions)
                OcrRegions.Add(region);

            OnPropertyChanged(nameof(OcrRegionCountText));
            IsOcrOverlayVisible = true;
            AiModuleProgress = 1;
            AiModuleProgressText = string.Format("扫描完成，共 {0} 处", result.Regions.Count);
        }
        catch (OperationCanceledException)
        {
            AiModuleProgressText = string.Empty;
        }
        catch (OcrService.OcrNotAvailableException ex)
        {
            Log.Warning(ex, "OCR 不可用");
            AiModuleProgressText = string.Empty;
            AppMessageBox.Show(
                ex.Message,
                "扫描文本不可用",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OCR 识别失败");
            AiModuleProgressText = string.Empty;
            AppMessageBox.Show(
                string.Format("扫描文本失败：{0}", ex.Message),
                "操作失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsAiModuleProcessing = false;
        }
    }

    [RelayCommand]
    private void CopyAllOcrText()
    {
        if (OcrRegions.Count == 0) return;
        ClipboardHelper.TrySetText(string.Join(Environment.NewLine, OcrRegions.Select(r => r.Text)));
    }

    [RelayCommand]
    private void ExitOcrOverlay() => ClearOcrOverlay();

    private void ClearOcrOverlay()
    {
        IsOcrOverlayVisible = false;
        OcrRegions.Clear();
        OnPropertyChanged(nameof(OcrRegionCountText));
    }

    [RelayCommand]
    private async Task SuperResolution()
    {
        if (ScreenshotImage is null || IsAnyAiProcessing) return;
        if (!await PrepareForImageReplaceAsync()) return;

        // 关闭弹出菜单（处理期间弹层将阻断操作）
        IsAiPopupOpen = false;
        IsAiModuleProcessing = true;
        AiModuleProgress = 0;
        AiModuleProgressText = "正在准备超分辨率...";
        var token = CreateAiCancellationToken();
        try
        {
            var progress = new Progress<(double Value, string Text)>(t =>
            {
                AiModuleProgress = t.Value;
                AiModuleProgressText = t.Text;
            });

            var result = await SuperResolutionService.RunAsync(ScreenshotImage, progress, token);
            if (result is null) return;

            await ApplyEditedImageAsync(result);
            AiModuleProgress = 1;
            AiModuleProgressText = "超分辨率完成";
        }
        catch (OperationCanceledException)
        {
            AiModuleProgressText = string.Empty;
        }
        catch (FileNotFoundException ex)
        {
            Log.Error(ex, "超分辨率模型缺失");
            AiModuleProgressText = string.Empty;
            AppMessageBox.Show(
                string.Format("AI 模型文件缺失，无法执行超分辨率。\n\n{0}", ex.Message),
                "模型缺失",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "超分辨率失败");
            AiModuleProgressText = string.Empty;
            AppMessageBox.Show(
                string.Format("超分辨率失败：{0}", ex.Message),
                "操作失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsAiModuleProcessing = false;
        }
    }

    /// <summary>创建新的 AI 操作取消令牌，取消上一个正在进行的 AI 操作（若有）。</summary>
    private CancellationToken CreateAiCancellationToken()
    {
        _aiCts?.Cancel();
        _aiCts?.Dispose();
        _aiCts = new CancellationTokenSource();
        return _aiCts.Token;
    }

    private bool CanUndo() => ScreenshotImage is not null && (IsAnnotateMode ? AnnotationPanel.CanUndo : _history.CanUndo);

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (!CanUndo() || ScreenshotImage is null) return;

        if (IsAnnotateMode)
        {
            AnnotationPanel.UndoAnnotationCommand.Execute(null);
            NotifyUndoRedoStateChanged();
            return;
        }

        var previous = _history.Undo(ImageIOService.CreateFrozenSnapshot(ScreenshotImage));
        SetCurrentImage(previous, switchToFit: false);
        NotifyUndoRedoStateChanged();
    }

    private bool CanRedo() => ScreenshotImage is not null && (IsAnnotateMode ? AnnotationPanel.CanRedo : _history.CanRedo);

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (!CanRedo() || ScreenshotImage is null) return;

        if (IsAnnotateMode)
        {
            AnnotationPanel.RedoAnnotationCommand.Execute(null);
            NotifyUndoRedoStateChanged();
            return;
        }

        var next = _history.Redo(ImageIOService.CreateFrozenSnapshot(ScreenshotImage));
        SetCurrentImage(next, switchToFit: false);
        NotifyUndoRedoStateChanged();
    }

    [RelayCommand]
    private void ZoomIn()
    {
        var baseZoom = IsActualSize ? ZoomFactor : FitZoomFactor;
        SetManualZoomFactor(baseZoom * 1.1);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        var baseZoom = IsActualSize ? ZoomFactor : FitZoomFactor;
        SetManualZoomFactor(baseZoom / 1.1);
    }

    [RelayCommand]
    private void FitToWindow()
    {
        SwitchToFitMode();
    }

    [RelayCommand]
    private void OpenSettings() => _navigation.ShowSettings();

    [RelayCommand]
    private void OpenLogFolder() => _navigation.ShowLogViewer();

    /// <summary>从文件路径加载图片（拖拽打开时使用）。</summary>
    public async Task LoadFromFileAsync(string filePath)
    {
        if (!await PrepareForImageReplaceAsync())
            return;

        IsFileOperationProcessing = true;
        FileOperationProgress = 0.05;
        FileOperationProgressText = "正在加载图片...";
        try
        {
            ScreenshotImage = null;
            var progress = new Progress<(double Value, string Text)>(p =>
            {
                FileOperationProgress = Math.Clamp(p.Value, 0, 1);
                FileOperationProgressText = p.Text;
            });
            Log.Information("拖拽加载图片: {FilePath}", filePath);
            var bitmap = await ImageIOService.LoadBitmapFromFileAsync(filePath, progress);
            SetCurrentImage(bitmap, switchToFit: true);
            ResetHistory();
            CaptureTime = Path.GetFileName(filePath);
            FileSize = ImageIOService.FormatFileSize(new FileInfo(filePath).Length);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "拖拽加载图片失败: {FilePath}", filePath);
            AppMessageBox.Show(
                string.Format("加载失败：{0}", ex.Message),
                "PixSnap 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsFileOperationProcessing = false;
        }
    }

    /// <summary>
    /// 窗口关闭时调用：注销 Messenger、释放图像引用，阻止 GC 根保留。
    /// </summary>
    public void Cleanup()
    {
        Log.Debug("ScreenshotPreviewViewModel.Cleanup");
        _isClosing = true;
        EraserPanel.CancelInpaintCommand.Execute(null);

        // 取消所有正在进行的 AI 操作，避免访问已释放的 ONNX Session
        _aiCts?.Cancel();
        _aiCts?.Dispose();
        _aiCts = null;

        _fileSizeUpdateCts?.Cancel();
        _fileSizeUpdateCts?.Dispose();
        _fileSizeUpdateCts = null;

        try
        {
            if (_imageApplySemaphore.Wait(TimeSpan.FromSeconds(5)))
                _imageApplySemaphore.Release();
            else
                Log.Warning("等待图片应用任务超时，预览窗口仍将关闭");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "等待图片应用任务时出错");
        }

        // 断开子 ViewModel 事件，避免循环引用
        EraserPanel.InpaintApplied -= OnEraserApplied;
        EraserPanel.PropertyChanged -= OnEraserPanelPropertyChanged;
        CropPanel.PropertyChanged -= OnCropPanelPropertyChanged;
        RoundCornerPanel.PropertyChanged -= OnRoundCornerPanelPropertyChanged;
        AnnotationPanel.PropertyChanged -= OnAnnotationPanelPropertyChanged;
        AnnotationPanel.AnnotationApplied -= OnAnnotationApplied;
        AnnotationPanel.AnnotationCancelled -= OnAnnotationCancelled;

        // 确保从 Messenger 注销（兼容未来可能重新激活的场景）
        IsActive = false;

        // 显式置空，解除对大尺寸 BitmapSource 的引用，让 GC 及时回收
        ScreenshotImage = null;

        _history.Dispose();
        _imageApplySemaphore.Dispose();
    }

    protected override void OnActivated()
    {
        Messenger.RegisterAll(this);
    }

    protected override void OnDeactivated()
    {
        Messenger.UnregisterAll(this);
    }

    public void Receive(ScreenshotCapturedMessage message)
    {
        Log.Information("接收截图: 模式={Mode}, 尺寸={W}×{H}", message.CaptureMode, message.Screenshot.PixelWidth, message.Screenshot.PixelHeight);
        SetCurrentImage(message.Screenshot, switchToFit: true);
        ResetHistory();
        IsActualSize = false;
        ZoomFactor = 1.0;
        CaptureMode = message.CaptureMode;
        CaptureTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        // 自动保存
        if (SettingsService.ReadAutoSave())
        {
            var dir = SettingsService.ReadSaveDirectory();
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                _ = AutoSaveAsync(message.Screenshot, dir);
            }
        }
    }

    private async Task AutoSaveAsync(BitmapSource image, string directory)
    {
        var fileName = $"PixSnap_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        var filePath = Path.Combine(directory, fileName);
        try
        {
            var frozen = ImageIOService.CreateFrozenSnapshot(image);
            await ImageIOService.SavePngAsync(frozen, filePath);
            Log.Information("自动保存成功: {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "自动保存失败: {FilePath}", filePath);
        }
    }

    public void ApplyEditedImage(BitmapSource newImage, bool switchToFit = true)
    {
        _ = ApplyEditedImageAsync(newImage, switchToFit);
    }

    public async Task ApplyEditedImageAsync(BitmapSource newImage, bool switchToFit = true)
    {
        if (_isClosing) return;

        await _imageApplySemaphore.WaitAsync();
        try
        {
            if (_isClosing) return;
            await Task.Yield();

            if (ScreenshotImage is not null)
            {
                _history.PushUndo(ImageIOService.CreateSnapshotFast(ScreenshotImage), newImage);
            }

            SetCurrentImage(newImage, switchToFit);
            NotifyUndoRedoStateChanged();
        }
        finally
        {
            try
            {
                _imageApplySemaphore.Release();
            }
            catch (ObjectDisposedException)
            {
                // 窗口关闭 Cleanup 与进行中的图片应用竞态
            }
        }
    }

    private void SetCurrentImage(BitmapSource? image, bool switchToFit)
    {
        ClearOcrOverlay();
        ScreenshotImage = image;
        if (image is null)
        {
            ImageSize = string.Empty;
            FileSize = string.Empty;
            _fileSizeUpdateCts?.Cancel();
            return;
        }

        ImageSize = $"{image.PixelWidth} × {image.PixelHeight}";
        // 先显示即时估算值，避免大图编码阻塞 UI 线程；随后后台刷新为精确值。
        FileSize = ImageIOService.FormatFileSize((long)image.PixelWidth * image.PixelHeight * 4);
        if ((long)image.PixelWidth * image.PixelHeight < ExactFileSizePixelThreshold)
        {
            StartUpdateExactFileSizeAsync(image);
        }

        if (switchToFit)
            SwitchToFitMode();
    }

    private static Task<BitmapSource> LoadBitmapFromFileAsync(string filePath, IProgress<(double Value, string Text)>? progress = null)
    {
        return Task.Run(() =>
        {
            progress?.Report((0.2, "正在读取文件..."));
            using var stream = File.OpenRead(filePath);
            progress?.Report((0.55, "正在解码图片..."));
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            var bitmap = decoder.Frames[0];
            if (!bitmap.IsFrozen)
            {
                bitmap.Freeze();
            }

            progress?.Report((0.95, "正在准备显示..."));

            return (BitmapSource)bitmap;
        });
    }

    private static BitmapSource CreateFrozenSnapshot(BitmapSource source)
    {
        if (source.IsFrozen)
            return source;

        var copy = new WriteableBitmap(source);
        copy.Freeze();
        return copy;
    }

    private static BitmapSource CreateSnapshotFast(BitmapSource source)
    {
        // 使用引用快照，避免大图同步深拷贝导致 UI 卡顿。
        // 当前编辑流程均产生新对象，不会原位修改旧图，因此引用快照可安全用于撤销/重做。
        return source;
    }

    private void ResetHistory()
    {
        _history.Reset();
        NotifyUndoRedoStateChanged();
    }

    private void StartUpdateExactFileSizeAsync(BitmapSource image)
    {
        _fileSizeUpdateCts?.Cancel();
        _fileSizeUpdateCts?.Dispose();
        _fileSizeUpdateCts = new CancellationTokenSource();
        var token = _fileSizeUpdateCts.Token;

        // 必须在 UI 线程创建冻结快照，避免后台线程访问 UI 线程拥有的 BitmapSource
        var frozen = ImageIOService.CreateFrozenSnapshot(image);

        _ = Task.Run(() =>
        {
            var exact = ImageIOService.GetEncodedPngSize(frozen);
            if (token.IsCancellationRequested) return;

            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (!token.IsCancellationRequested && ReferenceEquals(ScreenshotImage, image))
                    FileSize = ImageIOService.FormatFileSize(exact);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }, token);
    }

    private void NotifyUndoRedoStateChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }
}