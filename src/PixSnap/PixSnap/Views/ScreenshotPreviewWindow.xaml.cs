using PixSnap.Controls;
using PixSnap.Services;
using PixSnap.ViewModels;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MicaWPF.Controls;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace PixSnap.Views;

public partial class ScreenshotPreviewWindow : MicaWindow
{
    // ── 常量 ─────────────────────────────────────────────────────
    private const int ResizeBorderThickness = 5;
    private const double OverscrollPadding = 200;
    private const double ZoomStepFactor = 1.1;
    private const double EraserSampleStepRatio = 0.30;
    private const double PopupWidthEstimate = 200;
    private const double PopupHeightEstimate = 94;

    // ── 字段 ─────────────────────────────────────────────────────
    private HwndSourceHook? _wndProcHook;
    private bool _zoomTriggeredModeSwitch;
    private bool _isPanningPreview;
    private Point _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private bool _syncingCropRect;
    private bool _isEraserDrawing;
    private Point _lastEraserCanvasPoint;
    private bool _hasLastEraserCanvasPoint;
    private Polyline? _currentEraserStrokeVisual;
    private Border? _draggingPanel;
    private TranslateTransform? _draggingTransform;
    private Point _panelDragStart;
    private Point _panelTransformStart;
    // ── 标注拖拽 / 文本输入 / 中键平移 ───────────────────────
    private bool _isAnnotationPanning;
    private AnnotationItem? _dragAnnotation;
    private Point _dragStartImagePoint;
    private Vector _dragStartOffset;
    private System.Windows.Controls.TextBox? _activeTextBox;

    public ScreenshotPreviewWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
        StateChanged += OnWindowStateChanged;
        AllowDrop = true;
        Drop += OnDrop;
        DragOver += OnDragOver;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files?.Length > 0 && IsImageFile(files[0]))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length > 0 && IsImageFile(files[0]) &&
            DataContext is ScreenshotPreviewViewModel vm)
        {
            await vm.LoadFromFileAsync(files[0]);
        }
    }

    private static bool IsImageFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr hProcess);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    private void OnClosed(object? sender, EventArgs e)
    {
        // 1. 移除 WndProc 钩子，断开 HwndSource → Window 引用链
        RemoveWndProcHook();

        // 2. 断开所有事件订阅，释放 ViewModel 内部引用
        var viewModel = DataContext as ScreenshotPreviewViewModel;
        DetachViewModelHandlers(viewModel);
        viewModel?.Cleanup();

        // 3. 彻底移除 WPF 绑定表达式（光设 Source=null 只是覆盖值，绑定对象本身仍存在）
        BindingOperations.ClearBinding(FitPreviewImage, Image.SourceProperty);
        BindingOperations.ClearBinding(ActualSizeImage, Image.SourceProperty);
        FitPreviewImage.Source = null;
        ActualSizeImage.Source = null;

        // 4. 清除擦除画布视觉元素
        ClearEraserVisualStrokes();

        // 5. 断开整个可视化树 + DataContext，强制 WPF 渲染线程释放 MIL composition 资源
        Content = null;
        DataContext = null;

        // 6. GC 回收托管对象 + 运行终结器释放非托管像素内存 + 压缩 LOH
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // 7. 通知 Windows 立即释放进程工作集中已释放的物理页面
        //    否则 OS 会将已释放的页面保留在工作集中作为缓存，任务管理器显示的内存不会下降
        EmptyWorkingSet(GetCurrentProcess());
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModelHandlers(DataContext as ScreenshotPreviewViewModel);
        UpdatePreviewModeVisibility();
        UpdateFitZoomFactor();
        SyncWindowStateToViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModelHandlers(e.OldValue as ScreenshotPreviewViewModel);
        AttachViewModelHandlers(e.NewValue as ScreenshotPreviewViewModel);
        UpdatePreviewModeVisibility();
        UpdateFitZoomFactor();
        SyncWindowStateToViewModel();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ScreenshotPreviewViewModel.IsActualSize):
                UpdatePreviewModeVisibility();
                break;

            case nameof(ScreenshotPreviewViewModel.ScreenshotImage):
                UpdateFitZoomFactor();
                if (DataContext is ScreenshotPreviewViewModel vmImg)
                    UpdateAnnotationCanvasSize(vmImg);
                break;

            case nameof(ScreenshotPreviewViewModel.IsCropMode):
                UpdateCropOverlayVisibility();
                break;

            case nameof(ScreenshotPreviewViewModel.IsRoundCornerMode):
                RoundCornerPanel.Visibility = (DataContext is ScreenshotPreviewViewModel vm && vm.IsRoundCornerMode)
                    ? Visibility.Visible : Visibility.Collapsed;
                if (RoundCornerPanel.Visibility == Visibility.Visible)
                    RoundCornerPanelTranslate.X = RoundCornerPanelTranslate.Y = 0;
                break;

            case nameof(ScreenshotPreviewViewModel.IsEraserMode):
                UpdateEraserCanvasState();
                break;

            case nameof(ScreenshotPreviewViewModel.IsAnnotateMode):
                UpdateAnnotationCanvasState();
                break;
        }
    }

    private void AttachViewModelHandlers(ScreenshotPreviewViewModel? viewModel)
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.RecaptureRequested += OnRecaptureRequested;
            viewModel.CropPanel.CropApplied += OnCropApplied;
            viewModel.CropPanel.CropCancelled += OnCropCancelled;
            viewModel.CropPanel.PropertyChanged += OnCropPanelPropertyChanged;
            viewModel.RoundCornerPanel.RoundCornerApplied += OnRoundCornerApplied;
            viewModel.RoundCornerPanel.RoundCornerCancelled += OnRoundCornerCancelled;
            viewModel.EraserPanel.StrokesCleared += OnEraserStrokesCleared;
            viewModel.EraserPanel.InpaintApplied += OnEraserInpaintApplied;
            viewModel.AnnotationPanel.PropertyChanged += OnAnnotationPanelPropertyChanged;
            viewModel.AnnotationPanel.RequestRedraw += OnAnnotationRequestRedraw;

            CropOverlay.CropRectChanged += OnCropOverlayRectChanged;
            CropOverlay.EnterPressed += OnCropOverlayEnter;
            CropOverlay.EscapePressed += OnCropOverlayEscape;

            ActualSizeScrollViewer.ScrollChanged += OnScrollViewerScrollChanged;
        }
    }

    private void DetachViewModelHandlers(ScreenshotPreviewViewModel? viewModel)
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.RecaptureRequested -= OnRecaptureRequested;
            viewModel.CropPanel.CropApplied -= OnCropApplied;
            viewModel.CropPanel.CropCancelled -= OnCropCancelled;
            viewModel.CropPanel.PropertyChanged -= OnCropPanelPropertyChanged;
            viewModel.RoundCornerPanel.RoundCornerApplied -= OnRoundCornerApplied;
            viewModel.RoundCornerPanel.RoundCornerCancelled -= OnRoundCornerCancelled;
            viewModel.EraserPanel.StrokesCleared -= OnEraserStrokesCleared;
            viewModel.EraserPanel.InpaintApplied -= OnEraserInpaintApplied;
            viewModel.AnnotationPanel.PropertyChanged -= OnAnnotationPanelPropertyChanged;
            viewModel.AnnotationPanel.RequestRedraw -= OnAnnotationRequestRedraw;

            CropOverlay.CropRectChanged -= OnCropOverlayRectChanged;
            CropOverlay.EnterPressed -= OnCropOverlayEnter;
            CropOverlay.EscapePressed -= OnCropOverlayEscape;

            ActualSizeScrollViewer.ScrollChanged -= OnScrollViewerScrollChanged;
        }
    }

    private async void OnRecaptureRequested()
    {
        Close();
        if (Application.Current.MainWindow?.DataContext is MainViewModel mainVm)
            await mainVm.StartCaptureCommand.ExecuteAsync(null);
    }

    private void PreviewViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateFitZoomFactor();

        // 裁剪模式下窗口尺寸变化后，等布局完成再刷新裁剪框叠加层
        if (DataContext is ScreenshotPreviewViewModel vm && vm.IsCropMode && vm.ScreenshotImage is not null)
        {
            Dispatcher.InvokeAsync(() =>
            {
                var imgRect = GetImageDisplayRect();
                CropOverlay.RefreshForResize(imgRect, vm.ScreenshotImage.PixelWidth, vm.ScreenshotImage.PixelHeight);
            }, DispatcherPriority.Loaded);
        }
    }

    private void UpdatePreviewModeVisibility()
    {
        if (DataContext is not ScreenshotPreviewViewModel viewModel)
        {
            FitPreviewHost.Visibility = Visibility.Visible;
            ActualSizeScrollViewer.Visibility = Visibility.Collapsed;
            return;
        }

        var actualSizeVisibility = viewModel.IsActualSize ? Visibility.Visible : Visibility.Collapsed;
        var fitViewVisibility = viewModel.IsActualSize ? Visibility.Collapsed : Visibility.Visible;

        FitPreviewHost.Visibility = fitViewVisibility;
        ActualSizeScrollViewer.Visibility = actualSizeVisibility;

        // 切换到实际大小模式时，将初始滚动位置对齐到图片左上角（跳过 padding 空白）
        // 由滚轮触发的切换由滚轮 handler 自行定位，此处不覆盖
        if (viewModel.IsActualSize && !_zoomTriggeredModeSwitch)
        {
            Dispatcher.InvokeAsync(() =>
            {
                ActualSizeScrollViewer.ScrollToHorizontalOffset(OverscrollPadding);
                ActualSizeScrollViewer.ScrollToVerticalOffset(OverscrollPadding);
            }, DispatcherPriority.Loaded);
        }
    }

    private void UpdateFitZoomFactor()
    {
        if (DataContext is not ScreenshotPreviewViewModel viewModel)
        {
            return;
        }

        if (viewModel.ScreenshotImage is null || PreviewViewport.ActualWidth <= 0 || PreviewViewport.ActualHeight <= 0)
        {
            viewModel.FitZoomFactor = 1.0;
            return;
        }

        var imageWidth = viewModel.ScreenshotImage.Width > 0 ? viewModel.ScreenshotImage.Width : viewModel.ScreenshotImage.PixelWidth;
        var imageHeight = viewModel.ScreenshotImage.Height > 0 ? viewModel.ScreenshotImage.Height : viewModel.ScreenshotImage.PixelHeight;
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            viewModel.FitZoomFactor = 1.0;
            return;
        }

        var fitZoom = Math.Min(PreviewViewport.ActualWidth / imageWidth, PreviewViewport.ActualHeight / imageHeight);
        viewModel.FitZoomFactor = Math.Clamp(fitZoom, ScreenshotPreviewViewModel.MinZoomFactor, 1.0);
    }

    private void PreviewHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not ScreenshotPreviewViewModel viewModel || viewModel.ScreenshotImage is null)
        {
            return;
        }

        // 裁剪模式下禁止缩放，防止 overlay 与图片显示错位
        if (viewModel.IsCropMode)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;

        var pointerPosition = e.GetPosition(PreviewViewport);
        var oldZoom = viewModel.IsActualSize ? viewModel.ZoomFactor : viewModel.FitZoomFactor;
        var zoomStep = e.Delta > 0 ? ZoomStepFactor : 1.0 / ZoomStepFactor;
        var newZoom = Math.Clamp(
            oldZoom * zoomStep,
            ScreenshotPreviewViewModel.MinZoomFactor,
            ScreenshotPreviewViewModel.MaxZoomFactor);

        if (Math.Abs(newZoom - oldZoom) < 0.001)
        {
            return;
        }

        if (!viewModel.IsActualSize)
        {
            // 从"缩放以适应"模式切换：直接用 fit 几何推算鼠标下方的图片像素，
            // 无需先 UpdateLayout（避免 padding 空白闪烁）
            var imgW = viewModel.ScreenshotImage.PixelWidth;
            var imgH = viewModel.ScreenshotImage.PixelHeight;
            var originX = Math.Max(0d, (PreviewViewport.ActualWidth - imgW * oldZoom) / 2);
            var originY = Math.Max(0d, (PreviewViewport.ActualHeight - imgH * oldZoom) / 2);
            var contentX = (pointerPosition.X - originX) / oldZoom;
            var contentY = (pointerPosition.Y - originY) / oldZoom;

            ApplyZoomAroundCursor(viewModel, newZoom, contentX, contentY, pointerPosition, isModeSwitch: true);
            return;
        }

        // 已在实际大小模式：以鼠标为中心缩放，保持鼠标下方图片像素不动
        var hContentX = (ActualSizeScrollViewer.HorizontalOffset + pointerPosition.X - OverscrollPadding) / oldZoom;
        var hContentY = (ActualSizeScrollViewer.VerticalOffset + pointerPosition.Y - OverscrollPadding) / oldZoom;

        ApplyZoomAroundCursor(viewModel, newZoom, hContentX, hContentY, pointerPosition, isModeSwitch: false);
    }

    /// <summary>以鼠标为中心应用缩放并定位滚动偏移，保持鼠标下方图片像素不动。</summary>
    private void ApplyZoomAroundCursor(
        ScreenshotPreviewViewModel vm, double newZoom,
        double contentX, double contentY, Point pointer, bool isModeSwitch)
    {
        if (isModeSwitch) _zoomTriggeredModeSwitch = true;
        vm.SetManualZoomFactor(newZoom);
        if (isModeSwitch) _zoomTriggeredModeSwitch = false;

        ActualSizeScrollViewer.UpdateLayout();
        ActualSizeScrollViewer.ScrollToHorizontalOffset(Math.Max(0, OverscrollPadding + contentX * newZoom - pointer.X));
        ActualSizeScrollViewer.ScrollToVerticalOffset(Math.Max(0, OverscrollPadding + contentY * newZoom - pointer.Y));
    }

    private void ActualSizeScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ScreenshotPreviewViewModel viewModel || !viewModel.IsActualSize)
        {
            return;
        }

        // 标注模式下左键用于绘制，不做平移
        if (viewModel.IsAnnotateMode) return;

        if (IsScrollBarInteraction(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (ActualSizeScrollViewer.ScrollableWidth <= 0 && ActualSizeScrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        _isPanningPreview = true;
        _panStartPoint = e.GetPosition(ActualSizeScrollViewer);
        _panStartHorizontalOffset = ActualSizeScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = ActualSizeScrollViewer.VerticalOffset;
        ActualSizeScrollViewer.Cursor = Cursors.SizeAll;
        ActualSizeScrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void ActualSizeScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanningPreview)
        {
            return;
        }

        var currentPoint = e.GetPosition(ActualSizeScrollViewer);
        var delta = currentPoint - _panStartPoint;

        ActualSizeScrollViewer.ScrollToHorizontalOffset(Math.Max(0, _panStartHorizontalOffset - delta.X));
        ActualSizeScrollViewer.ScrollToVerticalOffset(Math.Max(0, _panStartVerticalOffset - delta.Y));
        e.Handled = true;
    }

    private void ActualSizeScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndPreviewPan();
    }

    private void ActualSizeScrollViewer_MouseLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPreviewPan();
        }
    }

    private void EndPreviewPan()
    {
        if (!_isPanningPreview)
        {
            return;
        }

        _isPanningPreview = false;
        ActualSizeScrollViewer.ReleaseMouseCapture();
        ActualSizeScrollViewer.ClearValue(CursorProperty);
    }

    private static bool IsScrollBarInteraction(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ScrollBar || current is Thumb)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 裁剪覆盖层（委托给 CropOverlayControl）
    // ══════════════════════════════════════════════════════════════════════════

    private void UpdateCropOverlayVisibility()
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;
        if (vm.IsCropMode)
        {
            CropOverlay.Visibility = Visibility.Visible;
            CropPanel.Visibility = Visibility.Visible;
            CropPanelTranslate.X = CropPanelTranslate.Y = 0;
            CropOverlay.LockedAspectRatio = vm.CropPanel.LockedAspectRatio;
            Dispatcher.InvokeAsync(() =>
            {
                PreviewViewport.UpdateLayout();
                var imgRect = GetImageDisplayRect();
                var img = vm.ScreenshotImage!;
                CropOverlay.Initialize(imgRect, img.PixelWidth, img.PixelHeight);
                CropOverlay.Focus();
            }, DispatcherPriority.Loaded);
        }
        else
        {
            CropOverlay.Visibility = Visibility.Collapsed;
            CropPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCropPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingCropRect) return;
        if (DataContext is not ScreenshotPreviewViewModel vm || vm.ScreenshotImage is null) return;

        if (e.PropertyName is nameof(CropViewModel.LockedAspectRatio))
        {
            CropOverlay.LockedAspectRatio = vm.CropPanel.LockedAspectRatio;
            return;
        }

        if (e.PropertyName is nameof(CropViewModel.CropX) or nameof(CropViewModel.CropY)
            or nameof(CropViewModel.CropWidth) or nameof(CropViewModel.CropHeight))
        {
            var imgRect = GetImageDisplayRect();
            var img = vm.ScreenshotImage;
            CropOverlay.SetPixelRect(
                vm.CropPanel.CropX, vm.CropPanel.CropY,
                vm.CropPanel.CropWidth, vm.CropPanel.CropHeight,
                imgRect, img.PixelWidth, img.PixelHeight);
        }
    }

    private void OnCropOverlayRectChanged(int x, int y, int w, int h)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;
        _syncingCropRect = true;
        try
        {
            vm.CropPanel.CropX = x;
            vm.CropPanel.CropY = y;
            vm.CropPanel.CropWidth = w;
            vm.CropPanel.CropHeight = h;
        }
        finally
        {
            _syncingCropRect = false;
        }
    }

    private void OnCropOverlayEnter()
    {
        if (DataContext is ScreenshotPreviewViewModel vm &&
            vm.CropPanel.ApplyCommand.CanExecute(vm.ScreenshotImage))
            vm.CropPanel.ApplyCommand.Execute(vm.ScreenshotImage);
    }

    private void OnCropOverlayEscape()
    {
        if (DataContext is ScreenshotPreviewViewModel vm)
            vm.CropPanel.CancelCommand.Execute(null);
    }

    /// <summary>获取图片在 FitPreviewHost 中居中显示的区域（像素）。</summary>
    private Rect GetImageDisplayRect()
    {
        if (DataContext is not ScreenshotPreviewViewModel vm || vm.ScreenshotImage is null)
            return new Rect(0, 0, PreviewViewport.ActualWidth, PreviewViewport.ActualHeight);

        var vpW = PreviewViewport.ActualWidth;
        var vpH = PreviewViewport.ActualHeight;
        var imgW = vm.ScreenshotImage.PixelWidth;
        var imgH = vm.ScreenshotImage.PixelHeight;
        var scale = Math.Min(vpW / imgW, vpH / imgH);
        scale = Math.Min(scale, 1.0); // StretchDirection=DownOnly
        var dW = imgW * scale;
        var dH = imgH * scale;
        return new Rect((vpW - dW) / 2, (vpH - dH) / 2, dW, dH);
    }

    /// <summary>将鼠标在 AnnotationCanvas 的本地坐标（即图片像素坐标）进行边界检查。</summary>
    private bool TryGetImagePixelPoint(Point canvasPos, out Point imgPixelPoint)
    {
        imgPixelPoint = default;
        if (DataContext is not ScreenshotPreviewViewModel vm || vm.ScreenshotImage is null)
            return false;

        double pw = vm.ScreenshotImage.PixelWidth;
        double ph = vm.ScreenshotImage.PixelHeight;
        double dpiScale = GetAnnotationDpiScale();

        // Canvas 坐标为 DIP 空间，乘以 DPI 缩放转为像素坐标
        double imgX = canvasPos.X * dpiScale;
        double imgY = canvasPos.Y * dpiScale;

        const double tolerance = 8;
        if (imgX < -tolerance || imgY < -tolerance ||
            imgX > pw + tolerance || imgY > ph + tolerance)
            return false;

        imgPixelPoint = new Point(Math.Clamp(imgX, 0, pw), Math.Clamp(imgY, 0, ph));
        return true;
    }

    private void OnCropApplied(BitmapSource newImage)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;
        vm.ApplyEditedImage(newImage);
        vm.ExitEditMode();
    }

    private void OnCropCancelled()
    {
        if (DataContext is ScreenshotPreviewViewModel vm)
            vm.ExitEditMode();
    }

    private void OnRoundCornerApplied(BitmapSource newImage)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;
        vm.ApplyEditedImage(newImage);
        vm.ExitEditMode();
    }

    private void OnRoundCornerCancelled()
    {
        if (DataContext is ScreenshotPreviewViewModel vm)
            vm.ExitEditMode();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // AI 擦除画布交互
    // ══════════════════════════════════════════════════════════════════════════

    private void UpdateEraserCanvasState()
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;
        var active = vm.IsEraserMode;

        EraserCanvas.IsHitTestVisible = active;
        EraserFloatingPanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

        if (active)
        {
            EraserFloatingPanelTranslate.X = EraserFloatingPanelTranslate.Y = 0;
        }
        else
        {
            // 退出时隐藏光标指示器、清除视觉笔画
            EraserCursorIndicator.Visibility = Visibility.Collapsed;
            ClearEraserVisualStrokes();
        }
    }

    private void UpdateEraserCursorIndicator(Point canvasPos)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;
        // BrushSize 直接作为屏幕显示半径使用
        double displayR = vm.EraserPanel.BrushSize;
        double diameter = displayR * 2;

        EraserCursorIndicator.Width = diameter;
        EraserCursorIndicator.Height = diameter;
        Canvas.SetLeft(EraserCursorIndicator, canvasPos.X - displayR);
        Canvas.SetTop(EraserCursorIndicator, canvasPos.Y - displayR);
        EraserCursorIndicator.Visibility = Visibility.Visible;
    }

    private void PaintEraserStroke(Point canvasPos)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;
        if (!TryGetImagePixelPoint(canvasPos, out var imgPt)) return;

        // BrushSize 是屏幕显示像素的半径，需换算为图片像素空间的半径再存入 Strokes
        var imgRect = GetImageDisplayRect();
        double imgPixelRadius = imgRect.Width > 0 && vm.ScreenshotImage is not null
            ? vm.EraserPanel.BrushSize * vm.ScreenshotImage.PixelWidth / imgRect.Width
            : vm.EraserPanel.BrushSize;

        vm.EraserPanel.Strokes.Add((imgPt, imgPixelRadius));

    }

    private void BeginEraserStrokeVisual(Point startPoint)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;

        _currentEraserStrokeVisual = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromArgb(56, 255, 80, 80)),
            StrokeThickness = Math.Max(2.0, vm.EraserPanel.BrushSize * 2.0),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };
        _currentEraserStrokeVisual.Points.Add(startPoint);
        EraserCanvas.Children.Add(_currentEraserStrokeVisual);
    }

    private void AddEraserStrokeVisualPoint(Point point)
    {
        _currentEraserStrokeVisual?.Points.Add(point);
    }

    private void PaintInterpolatedEraserStroke(Point from, Point to)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;

        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        // 算法采样用较小步长，保证遮罩连续
        double step = Math.Max(1.0, vm.EraserPanel.BrushSize * EraserSampleStepRatio);
        int segments = Math.Max(1, (int)Math.Ceiling(distance / step));

        for (int i = 1; i <= segments; i++)
        {
            double t = i / (double)segments;
            var p = new Point(from.X + dx * t, from.Y + dy * t);
            PaintEraserStroke(p);
            AddEraserStrokeVisualPoint(p);
        }
    }

    private void ClearEraserVisualStrokes()
    {
        // 移除所有视觉笔画，保留光标指示器
        for (int i = EraserCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (EraserCanvas.Children[i] != EraserCursorIndicator)
                EraserCanvas.Children.RemoveAt(i);
        }

        _currentEraserStrokeVisual = null;
        _hasLastEraserCanvasPoint = false;
        _isEraserDrawing = false;
    }

    private void OnEraserStrokesCleared()
    {
        Dispatcher.InvokeAsync(ClearEraserVisualStrokes);
    }

    private void OnEraserInpaintApplied(BitmapSource _)
    {
        // ViewModel 已更新 ScreenshotImage；FitZoomFactor 会由 PropertyChanged 触发刷新
        // 此处确保视觉笔画已清空（StrokesCleared 会先于此触发，双重保险）
        Dispatcher.InvokeAsync(ClearEraserVisualStrokes);
    }

    // ── EraserCanvas 鼠标事件 ────────────────────────────────────────────────

    private void EraserCanvas_MouseEnter(object sender, MouseEventArgs e)
    {
        UpdateEraserCursorIndicator(e.GetPosition(EraserCanvas));
    }

    private void EraserCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        EraserCursorIndicator.Visibility = Visibility.Collapsed;
        if (_isEraserDrawing)
        {
            _isEraserDrawing = false;
            EraserCanvas.ReleaseMouseCapture();
        }
        _hasLastEraserCanvasPoint = false;
        _currentEraserStrokeVisual = null;
    }

    private void EraserCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm || !vm.IsEraserMode) return;
        var pos = e.GetPosition(EraserCanvas);
        _isEraserDrawing = true;
        EraserCanvas.CaptureMouse();
        BeginEraserStrokeVisual(pos);
        PaintEraserStroke(pos);
        _lastEraserCanvasPoint = pos;
        _hasLastEraserCanvasPoint = true;
        e.Handled = true;
    }

    private void EraserCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(EraserCanvas);
        UpdateEraserCursorIndicator(pos);

        if (_isEraserDrawing && e.LeftButton == MouseButtonState.Pressed)
        {
            if (_hasLastEraserCanvasPoint)
                PaintInterpolatedEraserStroke(_lastEraserCanvasPoint, pos);
            else
                PaintEraserStroke(pos);

            _lastEraserCanvasPoint = pos;
            _hasLastEraserCanvasPoint = true;
        }
    }

    private void EraserCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isEraserDrawing) return;
        _isEraserDrawing = false;
        _hasLastEraserCanvasPoint = false;
        _currentEraserStrokeVisual = null;
        EraserCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 标注画布交互
    // ══════════════════════════════════════════════════════════════════════════

    private void OnAnnotationPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnnotationViewModel.SelectedTool))
        {
            UpdateAnnotationCursor();
            // 切换工具时清除选中
            if (DataContext is ScreenshotPreviewViewModel vm &&
                vm.AnnotationPanel.SelectedTool != AnnotationTool.Pointer)
            {
                vm.AnnotationPanel.SelectedAnnotation = null;
                RedrawAnnotationOverlay();
            }
        }
    }

    private void OnAnnotationRequestRedraw() => RedrawAnnotationOverlay();

    private void OnScrollViewerScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 标注画布已在 ScrollViewer 内部，随图片一起滚动，无需额外刷新
    }

    // ── 标注模式中键平移 ───────────────────────────────────

    private void AnnotationCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (DataContext is not ScreenshotPreviewViewModel vm || !vm.IsAnnotateMode || !vm.IsActualSize) return;

        _isAnnotationPanning = true;
        _panStartPoint = e.GetPosition(ActualSizeScrollViewer);
        _panStartHorizontalOffset = ActualSizeScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = ActualSizeScrollViewer.VerticalOffset;
        AnnotationCanvas.Cursor = Cursors.SizeAll;
        AnnotationCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void AnnotationCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !_isAnnotationPanning) return;
        _isAnnotationPanning = false;
        AnnotationCanvas.ReleaseMouseCapture();
        UpdateAnnotationCursor();
        e.Handled = true;
    }

    private void UpdateAnnotationCursor()
    {
        if (DataContext is not ScreenshotPreviewViewModel vm || !vm.IsAnnotateMode)
        {
            AnnotationCanvas.Cursor = null;
            return;
        }
        AnnotationCanvas.Cursor = vm.AnnotationPanel.SelectedTool == AnnotationTool.Pointer
            ? Cursors.Arrow
            : Cursors.Cross;
    }

    private void UpdateAnnotationCanvasState()
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;
        var active = vm.IsAnnotateMode;

        AnnotationCanvas.IsHitTestVisible = active;
        UpdateAnnotationCursor();
        AnnotationFloatingPanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

        if (active)
        {
            AnnotationFloatingPanelTranslate.X = AnnotationFloatingPanelTranslate.Y = 0;
            UpdateAnnotationCanvasSize(vm);
        }
        else
        {
            CommitActiveTextBox();
            AnnotationCanvas.Children.Clear();
        }
    }

    private void UpdateAnnotationCanvasSize(ScreenshotPreviewViewModel vm)
    {
        if (vm.ScreenshotImage is not null)
        {
            // 使用 DPI 感知的 DIP 尺寸，确保与 Image Stretch="None" 显示完全对齐
            AnnotationCanvas.Width = vm.ScreenshotImage.Width;   // = PixelWidth * 96 / DpiX
            AnnotationCanvas.Height = vm.ScreenshotImage.Height; // = PixelHeight * 96 / DpiY
        }
    }

    /// <summary>DPI 缩放因子：Canvas DIP → Image Pixel。96 DPI 时为 1.0。</summary>
    private double GetAnnotationDpiScale()
    {
        if (DataContext is ScreenshotPreviewViewModel vm && vm.ScreenshotImage is not null)
            return vm.ScreenshotImage.DpiX / 96.0;
        return 1.0;
    }

    private void AnnotationCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm || !vm.IsAnnotateMode) return;
        var pos = e.GetPosition(AnnotationCanvas);
        if (!TryGetImagePixelPoint(pos, out var imgPoint)) return;

        CommitActiveTextBox();

        if (vm.AnnotationPanel.SelectedTool == AnnotationTool.Pointer)
        {
            var hit = vm.AnnotationPanel.HitTest(imgPoint);
            if (hit is not null)
            {
                vm.AnnotationPanel.SelectedAnnotation = hit;
                _dragAnnotation = hit;
                _dragStartImagePoint = imgPoint;
                _dragStartOffset = hit.Offset;
                AnnotationCanvas.Cursor = Cursors.SizeAll;
                AnnotationCanvas.CaptureMouse();
                RedrawAnnotationOverlay();
            }
            else
            {
                // 点击空白区域取消选中
                vm.AnnotationPanel.SelectedAnnotation = null;
                RedrawAnnotationOverlay();
            }
            e.Handled = true;
            return;
        }

        vm.AnnotationPanel.BeginAnnotation(imgPoint);
        AnnotationCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void AnnotationCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm || !vm.IsAnnotateMode) return;

        // 中键平移
        if (_isAnnotationPanning && e.MiddleButton == MouseButtonState.Pressed)
        {
            var cur = e.GetPosition(ActualSizeScrollViewer);
            var delta = cur - _panStartPoint;
            ActualSizeScrollViewer.ScrollToHorizontalOffset(Math.Max(0, _panStartHorizontalOffset - delta.X));
            ActualSizeScrollViewer.ScrollToVerticalOffset(Math.Max(0, _panStartVerticalOffset - delta.Y));
            e.Handled = true;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(AnnotationCanvas);
        if (!TryGetImagePixelPoint(pos, out var imgPoint)) return;

        if (_dragAnnotation is not null)
        {
            var delta = imgPoint - _dragStartImagePoint;
            _dragAnnotation.Offset = _dragStartOffset + delta;
            RedrawAnnotationOverlay();
            return;
        }

        if (vm.AnnotationPanel.CurrentAnnotation is null) return;
        vm.AnnotationPanel.UpdateAnnotation(imgPoint);
        RedrawAnnotationOverlay();
    }

    private void AnnotationCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm || !vm.IsAnnotateMode) return;

        // 拖拽结束 → 提交移动到撤销栈
        if (_dragAnnotation is not null)
        {
            vm.AnnotationPanel.CommitMove(_dragAnnotation, _dragStartOffset);
            _dragAnnotation = null;
            UpdateAnnotationCursor();
            AnnotationCanvas.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (vm.AnnotationPanel.CurrentAnnotation is null) return;

        var isText = vm.AnnotationPanel.CurrentAnnotation.Tool == AnnotationTool.Text;
        vm.AnnotationPanel.EndAnnotation();
        AnnotationCanvas.ReleaseMouseCapture();

        if (isText && vm.AnnotationPanel.CurrentAnnotation is { } textItem)
        {
            ShowTextInputBox(textItem);
        }
        else
        {
            RedrawAnnotationOverlay();
        }
        e.Handled = true;
    }

    // ── 标注键盘快捷键 ────────────────────────────────────

    private void AnnotationCanvas_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm || !vm.IsAnnotateMode) return;
        if (_activeTextBox is not null) return; // 文本输入中不处理

        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            vm.AnnotationPanel.DeleteSelectedAnnotationCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            vm.AnnotationPanel.UndoAnnotationCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control)
        {
            vm.AnnotationPanel.RedoAnnotationCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (vm.AnnotationPanel.SelectedAnnotation is not null)
            {
                vm.AnnotationPanel.SelectedAnnotation = null;
                RedrawAnnotationOverlay();
            }
            e.Handled = true;
        }
    }

    // ── 文本输入框管理 ─────────────────────────────────────

    private void ShowTextInputBox(AnnotationItem textItem)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm || vm.ScreenshotImage is null) return;

        // 标注坐标为像素空间，Canvas 为 DIP 空间，需转换
        double d = 1.0 / GetAnnotationDpiScale();
        double sx = textItem.Start.X * d;
        double sy = textItem.Start.Y * d;

        var tb = new System.Windows.Controls.TextBox
        {
            MinWidth = 80,
            MaxWidth = Math.Max(120, vm.ScreenshotImage.Width - sx - 8),
            FontSize = Math.Max(10, textItem.FontSize * d),
            Foreground = new SolidColorBrush(textItem.StrokeColor),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(128, textItem.StrokeColor.R, textItem.StrokeColor.G, textItem.StrokeColor.B)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            AcceptsReturn = false,
            CaretBrush = new SolidColorBrush(textItem.StrokeColor),
            Tag = textItem
        };
        Canvas.SetLeft(tb, sx);
        Canvas.SetTop(tb, sy);
        AnnotationCanvas.Children.Add(tb);

        tb.LostFocus += OnTextBoxLostFocus;
        tb.KeyDown += OnTextBoxKeyDown;

        _activeTextBox = tb;
        tb.Focus();
    }

    private void OnTextBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Escape)
        {
            CommitActiveTextBox();
            e.Handled = true;
        }
    }

    private void OnTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        CommitActiveTextBox();
    }

    private void CommitActiveTextBox()
    {
        if (_activeTextBox is null || DataContext is not ScreenshotPreviewViewModel vm) return;

        var tb = _activeTextBox;
        _activeTextBox = null;

        tb.LostFocus -= OnTextBoxLostFocus;
        tb.KeyDown -= OnTextBoxKeyDown;

        var text = tb.Text?.Trim() ?? string.Empty;
        vm.AnnotationPanel.CommitTextAnnotation(text);

        AnnotationCanvas.Children.Remove(tb);
        RedrawAnnotationOverlay();
    }

    /// <summary>重绘所有标注到画布上（使用 WPF Shapes 实时预览）。</summary>
    private void RedrawAnnotationOverlay()
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;

        // 保留活跃的文本输入框
        var keepTextBox = _activeTextBox;
        AnnotationCanvas.Children.Clear();
        if (keepTextBox is not null)
            AnnotationCanvas.Children.Add(keepTextBox);

        if (vm.ScreenshotImage is null) return;

        var selected = vm.AnnotationPanel.SelectedAnnotation;

        foreach (var ann in vm.AnnotationPanel.Annotations)
            DrawAnnotationShape(ann, ann == selected);

        if (vm.AnnotationPanel.CurrentAnnotation is { } cur)
            DrawAnnotationShape(cur, false);
    }

    private void DrawAnnotationShape(AnnotationItem item, bool isSelected)
    {
        // 标注坐标为像素空间，Canvas 为 DIP 空间，需要缩放
        double d = 1.0 / GetAnnotationDpiScale(); // pixel → DIP

        var brush = new SolidColorBrush(item.StrokeColor);
        double thickness = item.StrokeWidth * d;

        double iox = item.Offset.X * d;
        double ioy = item.Offset.Y * d;
        double sx = item.Start.X * d + iox;
        double sy = item.Start.Y * d + ioy;
        double ex = item.End.X * d + iox;
        double ey = item.End.Y * d + ioy;

        switch (item.Tool)
        {
            case AnnotationTool.Arrow:
                var arrowLine = new System.Windows.Shapes.Line
                {
                    X1 = sx, Y1 = sy, X2 = ex, Y2 = ey,
                    Stroke = brush, StrokeThickness = thickness
                };
                AnnotationCanvas.Children.Add(arrowLine);

                double angle = Math.Atan2(ey - sy, ex - sx);
                double arrowLen = Math.Max(8, thickness * 5);
                double arrowAngle = Math.PI / 6;
                var p1 = new Point(ex - arrowLen * Math.Cos(angle - arrowAngle), ey - arrowLen * Math.Sin(angle - arrowAngle));
                var p2 = new Point(ex - arrowLen * Math.Cos(angle + arrowAngle), ey - arrowLen * Math.Sin(angle + arrowAngle));
                var arrowHead = new System.Windows.Shapes.Polygon
                {
                    Points = [new Point(ex, ey), p1, p2],
                    Fill = brush
                };
                AnnotationCanvas.Children.Add(arrowHead);
                break;

            case AnnotationTool.Rectangle:
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = Math.Abs(ex - sx),
                    Height = Math.Abs(ey - sy),
                    Stroke = brush,
                    StrokeThickness = thickness
                };
                Canvas.SetLeft(rect, Math.Min(sx, ex));
                Canvas.SetTop(rect, Math.Min(sy, ey));
                AnnotationCanvas.Children.Add(rect);
                break;

            case AnnotationTool.Ellipse:
                var ell = new System.Windows.Shapes.Ellipse
                {
                    Width = Math.Abs(ex - sx),
                    Height = Math.Abs(ey - sy),
                    Stroke = brush,
                    StrokeThickness = thickness
                };
                Canvas.SetLeft(ell, Math.Min(sx, ex));
                Canvas.SetTop(ell, Math.Min(sy, ey));
                AnnotationCanvas.Children.Add(ell);
                break;

            case AnnotationTool.Text:
                var tb = new TextBlock
                {
                    Text = item.Text,
                    Foreground = brush,
                    FontSize = item.FontSize * d
                };
                Canvas.SetLeft(tb, sx);
                Canvas.SetTop(tb, sy);
                AnnotationCanvas.Children.Add(tb);
                break;

            case AnnotationTool.Pen:
                if (item.PenPoints.Count >= 2)
                {
                    var polyline = new Polyline
                    {
                        Stroke = brush,
                        StrokeThickness = thickness,
                        StrokeLineJoin = PenLineJoin.Round
                    };
                    foreach (var pt in item.PenPoints)
                        polyline.Points.Add(new Point(pt.X * d + iox, pt.Y * d + ioy));
                    AnnotationCanvas.Children.Add(polyline);
                }
                break;
        }

        // 选中标注绘制虚线选框
        if (isSelected)
        {
            var bounds = GetAnnotationScreenBounds(item, d);
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                bounds.Inflate(4, 4);
                var selRect = new System.Windows.Shapes.Rectangle
                {
                    Width = bounds.Width,
                    Height = bounds.Height,
                    Stroke = System.Windows.Media.Brushes.DodgerBlue,
                    StrokeThickness = 1.5,
                    StrokeDashArray = [4, 2],
                    Fill = null
                };
                Canvas.SetLeft(selRect, bounds.X);
                Canvas.SetTop(selRect, bounds.Y);
                AnnotationCanvas.Children.Add(selRect);
            }
        }
    }

    private static Rect GetAnnotationScreenBounds(AnnotationItem item, double d)
    {
        double iox = item.Offset.X * d;
        double ioy = item.Offset.Y * d;
        double sx = item.Start.X * d + iox;
        double sy = item.Start.Y * d + ioy;
        double ex = item.End.X * d + iox;
        double ey = item.End.Y * d + ioy;

        switch (item.Tool)
        {
            case AnnotationTool.Arrow:
                return new Rect(new Point(Math.Min(sx, ex), Math.Min(sy, ey)), new Point(Math.Max(sx, ex), Math.Max(sy, ey)));
            case AnnotationTool.Rectangle:
            case AnnotationTool.Ellipse:
                return new Rect(new Point(Math.Min(sx, ex), Math.Min(sy, ey)), new Point(Math.Max(sx, ex), Math.Max(sy, ey)));
            case AnnotationTool.Text:
                return new Rect(sx, sy, Math.Max(Math.Abs(ex - sx), item.FontSize * d * Math.Max(1, item.Text.Length) * 0.6), item.FontSize * d * 1.4);
            case AnnotationTool.Pen:
                if (item.PenPoints.Count == 0) return Rect.Empty;
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var pt in item.PenPoints)
                {
                    double px = pt.X * d + iox;
                    double py = pt.Y * d + ioy;
                    if (px < minX) minX = px;
                    if (py < minY) minY = py;
                    if (px > maxX) maxX = px;
                    if (py > maxY) maxY = py;
                }
                return new Rect(minX, minY, maxX - minX, maxY - minY);
            default:
                return Rect.Empty;
        }
    }

    private void EditPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border panel) return;
        _draggingPanel = panel;
        _draggingTransform = panel.RenderTransform as TranslateTransform;
        _panelDragStart = e.GetPosition(this);
        _panelTransformStart = _draggingTransform is not null
            ? new Point(_draggingTransform.X, _draggingTransform.Y)
            : new Point(0, 0);
        panel.CaptureMouse();
        e.Handled = true;
    }

    private void EditPanel_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingPanel is null || _draggingTransform is null) return;
        var pos = e.GetPosition(this);
        var delta = pos - _panelDragStart;
        _draggingTransform.X = _panelTransformStart.X + delta.X;
        _draggingTransform.Y = _panelTransformStart.Y + delta.Y;
    }

    private void EditPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggingPanel?.ReleaseMouseCapture();
        _draggingPanel = null;
        _draggingTransform = null;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        SyncWindowStateToViewModel();
    }

    private void SyncWindowStateToViewModel()
    {
        if (DataContext is ScreenshotPreviewViewModel viewModel)
        {
            viewModel.IsMaximized = WindowState == WindowState.Maximized;
        }
    }

    // 注册 WndProc 钩子，实现边缘缩放
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _wndProcHook = WndProc;
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(_wndProcHook);
    }

    private void RemoveWndProcHook()
    {
        if (_wndProcHook is null) return;
        var helper = new WindowInteropHelper(this);
        if (helper.Handle != IntPtr.Zero)
        {
            var source = HwndSource.FromHwnd(helper.Handle);
            source?.RemoveHook(_wndProcHook);
        }
        _wndProcHook = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeWindowHelper.WM_NCHITTEST)
        {
            int hit = NativeWindowHelper.GetResizeHitTest(this, lParam, ResizeBorderThickness);
            if (!NativeWindowHelper.IsClientHit(hit))
            {
                handled = true;
                return new IntPtr(hit);
            }
        }
        else if (msg == NativeWindowHelper.WM_GETMINMAXINFO)
        {
            NativeWindowHelper.HandleGetMinMaxInfo(this, hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    // 拖拽移动：挂在外层 Border 的 MouseLeftButtonDown
    // Button 会标记事件为 Handled，因此点击按钮不会触发此方法
    private void OnWindowDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void PreviewViewport_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(PreviewViewport);

        // 控制弹出位置在预览区域可见范围内
        double x = Math.Clamp(pos.X, 0, Math.Max(0, PreviewViewport.ActualWidth - PopupWidthEstimate));
        double y = Math.Clamp(pos.Y, 0, Math.Max(0, PreviewViewport.ActualHeight - PopupHeightEstimate));

        // 将弹出位置切换为相对于预览区域的点击坐标
        AiModulePopup.PlacementTarget = PreviewViewport;
        AiModulePopup.Placement = PlacementMode.Relative;
        AiModulePopup.HorizontalOffset = x;
        AiModulePopup.VerticalOffset = y;

        // 直接更新 ViewModel，TwoWay 绑定将自动同步至 AiModulePopup.IsOpen
        if (DataContext is ScreenshotPreviewViewModel vm2)
            vm2.IsAiPopupOpen = true;
        e.Handled = true;
    }
}