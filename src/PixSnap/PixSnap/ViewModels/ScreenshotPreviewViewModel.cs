using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PixSnap.Models;
using PixSnap.Services;
using PixSnap.Views;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace PixSnap.ViewModels;

/// <summary>截图预览窗口的编辑模式，各模式互斥，同一时刻只有一种可激活。</summary>
public enum EditMode { None, Crop, RoundCorner, Eraser }

public partial class ScreenshotPreviewViewModel : ObservableRecipient, IRecipient<ScreenshotCapturedMessage>
{
    public const double MinZoomFactor = 0.1;
    public const double MaxZoomFactor = 8.0;
    private const long LargeImagePixelThreshold = 16_000_000;
    private const long ExactFileSizePixelThreshold = 16_000_000;
    private const int NormalUndoLimit = 10;
    private const int LargeImageUndoLimit = 3;

    private readonly Stack<BitmapSource> _undoStack = [];
    private readonly Stack<BitmapSource> _redoStack = [];
    private readonly SemaphoreSlim _imageApplySemaphore = new(1, 1);
    private CancellationTokenSource? _fileSizeUpdateCts;
    private CancellationTokenSource? _aiCts;

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
    private bool _isMaximized;

    /// <summary>AI 功能弹出菜单是否展开。双向绑定到 Popup.IsOpen。</summary>
    [ObservableProperty]
    private bool _isAiPopupOpen;

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
    private EditMode _activeEditMode;

    partial void OnActiveEditModeChanged(EditMode oldValue, EditMode newValue)
    {
        OnPropertyChanged(nameof(IsCropMode));
        OnPropertyChanged(nameof(IsRoundCornerMode));
        OnPropertyChanged(nameof(IsEraserMode));
        OnPropertyChanged(nameof(IsEditPanelVisible));
    }

    public bool IsCropMode        => ActiveEditMode == EditMode.Crop;
    public bool IsRoundCornerMode => ActiveEditMode == EditMode.RoundCorner;
    public bool IsEraserMode      => ActiveEditMode == EditMode.Eraser;
    public bool IsEditPanelVisible => ActiveEditMode != EditMode.None;

    /// <summary>退出当前编辑模式，回到浏览状态。</summary>
    public void ExitEditMode() => ActiveEditMode = EditMode.None;

    public CropViewModel CropPanel { get; } = new();
    public RoundCornerViewModel RoundCornerPanel { get; } = new();
    public EraserViewModel EraserPanel { get; } = new();

    public string PreviewScaleModeText => IsActualSize ? "缩放以适应" : "缩放以原始";
    public string ZoomDisplayText => $"缩放 {(IsActualSize ? ZoomFactor : FitZoomFactor):P0}";
    public string ZoomCompactText => $"{(IsActualSize ? ZoomFactor : FitZoomFactor):P0}";
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

    public ScreenshotPreviewViewModel()
    {
        // 不向全局 Messenger 注册：每个预览窗口只对应一次截图，
        // 由 App 直接调用 Receive() 初始化，无需监听后续广播。
        EraserPanel.InpaintApplied += OnEraserApplied;
        EraserPanel.PropertyChanged += OnEraserPanelPropertyChanged;
        CropPanel.PropertyChanged += OnCropPanelPropertyChanged;
        RoundCornerPanel.PropertyChanged += OnRoundCornerPanelPropertyChanged;
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

    [RelayCommand]
    private async Task OpenFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg|PNG 文件|*.png|JPEG 文件|*.jpg;*.jpeg"
        };

        if (dialog.ShowDialog() != true) return;

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

            var bitmap = await LoadBitmapFromFileAsync(dialog.FileName, progress);

            SetCurrentImage(bitmap, switchToFit: true);
            ResetHistory();
            CaptureTime = Path.GetFileName(dialog.FileName);
            FileSize = FormatFileSize(new FileInfo(dialog.FileName).Length);
            FileOperationProgress = 1.0;
            FileOperationProgressText = "图片加载完成";
        }
        catch (Exception ex)
        {
            MessageBoxWindow.Show(
                $"加载失败：{ex.Message}",
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
            Filter = "PNG 文件|*.png",
            FileName = $"PixSnap_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var imageSnapshot = CreateFrozenSnapshot(ScreenshotImage);
        IsSaving = true;
        IsFileOperationProcessing = true;
        FileOperationProgress = 0.1;
        FileOperationProgressText = "正在保存图片...";
        try
        {
            var progress = new Progress<(double Value, string Text)>(p =>
            {
                FileOperationProgress = Math.Clamp(p.Value, 0, 1);
                FileOperationProgressText = p.Text;
            });

            await SavePngAsync(imageSnapshot, dialog.FileName, progress);
            FileOperationProgress = 1.0;
            FileOperationProgressText = "图片保存完成";
        }
        catch (Exception ex)
        {
            MessageBoxWindow.Show(
                $"保存失败：{ex.Message}",
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
    private void CopyToClipboard()
    {
        if (ScreenshotImage is not null)
        {
            Clipboard.SetImage(ScreenshotImage);
        }
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
                dst[dstOffset]     = src[srcOffset];
                dst[dstOffset + 1] = src[srcOffset + 1];
                dst[dstOffset + 2] = src[srcOffset + 2];
                dst[dstOffset + 3] = src[srcOffset + 3];
            }
        }
        return (dst, dstW, dstH);
    }

    [RelayCommand]
    private void ToggleCrop()
    {
        if (ScreenshotImage is null) return;
        SwitchToFitMode();
        if (ActiveEditMode == EditMode.Crop)
        {
            ActiveEditMode = EditMode.None;
        }
        else
        {
            ActiveEditMode = EditMode.Crop;
            CropPanel.Initialize(ScreenshotImage.PixelWidth, ScreenshotImage.PixelHeight);
        }
    }

    [RelayCommand]
    private void ToggleRoundCorner()
    {
        if (ScreenshotImage is null) return;
        ActiveEditMode = ActiveEditMode == EditMode.RoundCorner ? EditMode.None : EditMode.RoundCorner;
    }

    [RelayCommand]
    private void ToggleEraser()
    {
        if (ScreenshotImage is null) return;
        if (ActiveEditMode == EditMode.Eraser)
        {
            ActiveEditMode = EditMode.None;
            EraserPanel.ClearStrokesCommand.Execute(null);
        }
        else
        {
            SwitchToFitMode();
            ActiveEditMode = EditMode.Eraser;
        }
    }

    /// <summary>取消 AI 擦除的推理（如正在运行）并退出擦除编辑模式。</summary>
    [RelayCommand]
    private void ExitEraserMode()
    {
        EraserPanel.CancelInpaintCommand.Execute(null);
        ExitEditMode();
    }

    /// <summary>切换 AI 功能弹出菜单的显示状态。</summary>
    [RelayCommand]
    private void ToggleAiPopup() => IsAiPopupOpen = !IsAiPopupOpen;

    /// <summary>执行背景去除 AI 功能。</summary>
    [RelayCommand]
    private async Task RemoveBackground()
    {
        if (ScreenshotImage is null || IsAnyAiProcessing) return;

        // 关闭弹出菜单（处理期间遞层将阻断操作）
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
        catch (Exception ex)
        {
            AiModuleProgressText = $"去除背景失败：{ex.Message}";
        }
        finally
        {
            IsAiModuleProcessing = false;
        }
    }

    [RelayCommand]
    private async Task SuperResolution()
    {
        if (ScreenshotImage is null || IsAnyAiProcessing) return;

        // 关闭弹出菜单（处理期间遞层将阻断操作）
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
        catch (Exception ex)
        {
            AiModuleProgressText = $"超分辨率失败：{ex.Message}";
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

    private bool CanUndo() => ScreenshotImage is not null && _undoStack.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (!CanUndo() || ScreenshotImage is null) return;

        _redoStack.Push(CreateFrozenSnapshot(ScreenshotImage));
        var previous = _undoStack.Pop();
        SetCurrentImage(previous, switchToFit: false);
        NotifyUndoRedoStateChanged();
    }

    private bool CanRedo() => ScreenshotImage is not null && _redoStack.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (!CanRedo() || ScreenshotImage is null) return;

        _undoStack.Push(CreateFrozenSnapshot(ScreenshotImage));
        var next = _redoStack.Pop();
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
    private void Close(Window? window)
    {
        if (window is null) return;
        window.Close();
    }

    [RelayCommand]
    private void Minimize(Window? window)
    {
        if (window is null) return;
        window.WindowState = WindowState.Minimized;
        IsMaximized = false;
    }

    [RelayCommand]
    private void Maximize(Window? window)
    {
        if (window is null) return;
        var nextState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        window.WindowState = nextState;
        IsMaximized = nextState == WindowState.Maximized;
    }

    /// <summary>通知宿主窗口关闭当前预览并立即启动新一轮截图。</summary>
    public event Action? RecaptureRequested;

    [RelayCommand]
    private void Recapture()
    {
        RecaptureRequested?.Invoke();
    }

    /// <summary>
    /// 窗口关闭时调用：注销 Messenger、释放图像引用，阻止 GC 根保留。
    /// </summary>
    public void Cleanup()
    {
        // 取消所有正在进行的 AI 操作，避免访问已释放的 ONNX Session
        _aiCts?.Cancel();
        _aiCts?.Dispose();
        _aiCts = null;

        // 断开 EraserPanel 事件，避免循环引用
        EraserPanel.InpaintApplied -= OnEraserApplied;
        EraserPanel.PropertyChanged -= OnEraserPanelPropertyChanged;

        // 确保从 Messenger 注销（兼容未来可能重新激活的场景）
        IsActive = false;

        // 显式置空，解除对大尺寸 BitmapSource 的引用，让 GC 及时回收
        ScreenshotImage = null;

        _undoStack.Clear();
        _redoStack.Clear();
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
        SetCurrentImage(message.Screenshot, switchToFit: true);
        ResetHistory();
        IsActualSize = false;
        ZoomFactor = 1.0;
        CaptureMode = message.CaptureMode;
        CaptureTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    public void ApplyEditedImage(BitmapSource newImage, bool switchToFit = true)
    {
        _ = ApplyEditedImageAsync(newImage, switchToFit);
    }

    public async Task ApplyEditedImageAsync(BitmapSource newImage, bool switchToFit = true)
    {
        await _imageApplySemaphore.WaitAsync();
        try
        {
            await Task.Yield();

            if (ScreenshotImage is not null)
            {
                _undoStack.Push(CreateSnapshotFast(ScreenshotImage));
                TrimUndoStack(GetUndoLimit(newImage));
            }

            _redoStack.Clear();
            SetCurrentImage(newImage, switchToFit);
            NotifyUndoRedoStateChanged();
        }
        finally
        {
            _imageApplySemaphore.Release();
        }
    }

    private void SetCurrentImage(BitmapSource? image, bool switchToFit)
    {
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
        FileSize = FormatFileSize((long)image.PixelWidth * image.PixelHeight * 4);
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
        _undoStack.Clear();
        _redoStack.Clear();
        NotifyUndoRedoStateChanged();
    }

    private int GetUndoLimit(BitmapSource image)
    {
        long pixels = (long)image.PixelWidth * image.PixelHeight;
        return pixels >= LargeImagePixelThreshold ? LargeImageUndoLimit : NormalUndoLimit;
    }

    private void TrimUndoStack(int limit)
    {
        if (_undoStack.Count <= limit) return;

        var kept = _undoStack.Take(limit).Reverse().ToArray();
        _undoStack.Clear();
        foreach (var item in kept)
            _undoStack.Push(item);
    }

    private void StartUpdateExactFileSizeAsync(BitmapSource image)
    {
        _fileSizeUpdateCts?.Cancel();
        _fileSizeUpdateCts?.Dispose();
        _fileSizeUpdateCts = new CancellationTokenSource();
        var token = _fileSizeUpdateCts.Token;

        _ = Task.Run(() =>
        {
            var exact = GetEncodedPngSize(image);
            if (token.IsCancellationRequested) return;

            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (!token.IsCancellationRequested && ReferenceEquals(ScreenshotImage, image))
                    FileSize = FormatFileSize(exact);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }, token);
    }

    private void NotifyUndoRedoStateChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    // 编码为 PNG 流以获取压缩后的实际文件大小估算值
    private static long GetEncodedPngSize(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.Length;
    }

    private static Task SavePngAsync(BitmapSource bitmap, string filePath, IProgress<(double Value, string Text)>? progress = null)
    {
        return Task.Run(() =>
        {
            progress?.Report((0.25, "正在编码图片..."));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            progress?.Report((0.7, "正在写入磁盘..."));
            using var stream = File.Create(filePath);
            encoder.Save(stream);
            progress?.Report((0.95, "正在完成保存..."));
        });
    }

    private static string FormatFileSize(long byteCount)
    {
        const double kilo = 1024d;
        const double mega = kilo * 1024d;

        return byteCount switch
        {
            >= (long)mega => $"{byteCount / mega:0.0} MB",
            >= (long)kilo => $"{byteCount / kilo:0.0} KB",
            _ => $"{byteCount} B"
        };
    }
}