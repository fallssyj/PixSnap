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
    // ── 标注拖拽 / 文本输入 ─────────────────────────────────
    private AnnotationItem? _dragAnnotation;
    private Point _dragStartImagePoint;
    private Vector _dragStartOffset;
    private System.Windows.Controls.TextBox? _activeTextBox;
    // ── 标注缩放拖拽 ──────────────────────────────────────────
    private AnnotationItem? _resizeAnnotation;
    private int _resizeHandle = -1; // 0-7: TL,T,TR,R,BR,B,BL,L
    private Point _resizeStartStart;
    private Point _resizeStartEnd;
    private double _resizeStartFontSize;

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
        BindingOperations.ClearBinding(ActualSizeImage, Image.SourceProperty);
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
                {
                    UpdateAnnotationCanvasSize(vmImg);
                    UpdateEraserCanvasSize(vmImg);
                }
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
        if (DataContext is not ScreenshotPreviewViewModel viewModel) return;

        if (viewModel.IsActualSize)
        {
            // 实际大小/手动缩放模式：显示滚动条
            ActualSizeScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            ActualSizeScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            // 由滚轮触发的切换由滚轮 handler 自行定位，此处不覆盖
            if (!_zoomTriggeredModeSwitch)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    ActualSizeScrollViewer.ScrollToHorizontalOffset(OverscrollPadding);
                    ActualSizeScrollViewer.ScrollToVerticalOffset(OverscrollPadding);
                }, DispatcherPriority.Loaded);
            }
        }
        else
        {
            // 缩放以适应模式：隐藏滚动条，居中显示
            ActualSizeScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            ActualSizeScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            viewModel.ZoomFactor = viewModel.FitZoomFactor;
            CenterScrollViewerOnImage();
        }
    }

    /// <summary>将 ScrollViewer 滚动到图片居中位置。</summary>
    private void CenterScrollViewerOnImage()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (DataContext is not ScreenshotPreviewViewModel vm || vm.ScreenshotImage is null) return;
            ActualSizeScrollViewer.UpdateLayout();
            double displayW = vm.ScreenshotImage.Width * vm.ZoomFactor;
            double displayH = vm.ScreenshotImage.Height * vm.ZoomFactor;
            double vpW = ActualSizeScrollViewer.ViewportWidth;
            double vpH = ActualSizeScrollViewer.ViewportHeight;
            ActualSizeScrollViewer.ScrollToHorizontalOffset(
                Math.Max(0, OverscrollPadding - (vpW - displayW) / 2));
            ActualSizeScrollViewer.ScrollToVerticalOffset(
                Math.Max(0, OverscrollPadding - (vpH - displayH) / 2));
        }, DispatcherPriority.Loaded);
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

        // 适应模式下同步 ZoomFactor 并居中
        if (!viewModel.IsActualSize)
        {
            viewModel.ZoomFactor = viewModel.FitZoomFactor;
            CenterScrollViewerOnImage();
        }
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

        var pointerPosition = e.GetPosition(ActualSizeScrollViewer);
        var oldZoom = viewModel.ZoomFactor;
        var zoomStep = e.Delta > 0 ? ZoomStepFactor : 1.0 / ZoomStepFactor;
        var newZoom = Math.Clamp(
            oldZoom * zoomStep,
            ScreenshotPreviewViewModel.MinZoomFactor,
            ScreenshotPreviewViewModel.MaxZoomFactor);

        if (Math.Abs(newZoom - oldZoom) < 0.001)
        {
            return;
        }

        // 统一公式：从 ScrollViewer 滚动位置推算鼠标下方的图片内容坐标
        var contentX = (ActualSizeScrollViewer.HorizontalOffset + pointerPosition.X - OverscrollPadding) / oldZoom;
        var contentY = (ActualSizeScrollViewer.VerticalOffset + pointerPosition.Y - OverscrollPadding) / oldZoom;
        bool switching = !viewModel.IsActualSize;

        ApplyZoomAroundCursor(viewModel, newZoom, contentX, contentY, pointerPosition, isModeSwitch: switching);
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

    private void ActualSizeScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;

        if (IsScrollBarInteraction(e.OriginalSource as DependencyObject)) return;

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

    private void ActualSizeScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        EndPreviewPan();
    }

    private void ActualSizeScrollViewer_MouseLeave(object sender, MouseEventArgs e)
    {
        if (e.MiddleButton != MouseButtonState.Pressed)
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

    /// <summary>获取图片在 PreviewViewport 中的显示区域。</summary>
    private Rect GetImageDisplayRect()
    {
        if (DataContext is not ScreenshotPreviewViewModel vm || vm.ScreenshotImage is null)
            return new Rect(0, 0, PreviewViewport.ActualWidth, PreviewViewport.ActualHeight);

        try
        {
            // 利用 WPF 视觉树变换，精确计算图片经 LayoutTransform + ScrollViewer 偏移后的实际显示位置
            var transform = ActualSizeImage.TransformToAncestor(PreviewViewport);
            var topLeft = transform.Transform(new Point(0, 0));
            var bottomRight = transform.Transform(
                new Point(ActualSizeImage.ActualWidth, ActualSizeImage.ActualHeight));
            return new Rect(topLeft, bottomRight);
        }
        catch
        {
            // 降级：手动计算
            double zoom = vm.ZoomFactor;
            double displayW = vm.ScreenshotImage.Width * zoom;
            double displayH = vm.ScreenshotImage.Height * zoom;
            double left = OverscrollPadding - ActualSizeScrollViewer.HorizontalOffset;
            double top = OverscrollPadding - ActualSizeScrollViewer.VerticalOffset;
            return new Rect(left, top, displayW, displayH);
        }
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
            UpdateEraserCanvasSize(vm);
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

        // EraserCanvas 在缩放变换 Grid 内，与图片同处 DIP 空间
        // BrushSize 即 DIP 半径，乘 DPI 缩放转为图片像素半径
        double imgPixelRadius = vm.EraserPanel.BrushSize * GetAnnotationDpiScale();

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

    private void UpdateEraserCanvasSize(ScreenshotPreviewViewModel vm)
    {
        if (vm.ScreenshotImage is not null)
        {
            EraserCanvas.Width = vm.ScreenshotImage.Width;
            EraserCanvas.Height = vm.ScreenshotImage.Height;
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
            // 优先检测缩放手柄
            if (vm.AnnotationPanel.SelectedAnnotation is { } sel)
            {
                int handle = HitTestResizeHandle(sel, pos);
                if (handle >= 0 && sel.Tool is not AnnotationTool.Pen)
                {
                    _resizeAnnotation = sel;
                    _resizeHandle = handle;
                    _resizeStartStart = sel.Start;
                    _resizeStartEnd = sel.End;
                    _resizeStartFontSize = sel.FontSize;
                    AnnotationCanvas.Cursor = ResizeHandleCursors[handle];
                    AnnotationCanvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }

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

        var pos = e.GetPosition(AnnotationCanvas);

        // 非拖拽时更新鼠标指针（悬停在手柄上变为缩放光标）
        if (e.LeftButton != MouseButtonState.Pressed && vm.AnnotationPanel.SelectedTool == AnnotationTool.Pointer)
        {
            if (vm.AnnotationPanel.SelectedAnnotation is { } sel &&
                sel.Tool is not AnnotationTool.Pen)
            {
                int h = HitTestResizeHandle(sel, pos);
                AnnotationCanvas.Cursor = h >= 0 ? ResizeHandleCursors[h] : Cursors.Arrow;
            }
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;

        if (!TryGetImagePixelPoint(pos, out var imgPoint)) return;

        // 缩放拖拽
        if (_resizeAnnotation is not null)
        {
            ApplyResize(_resizeAnnotation, _resizeHandle, imgPoint);
            RedrawAnnotationOverlay();
            return;
        }

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

        // 缩放结束 → 提交到撤销栈
        if (_resizeAnnotation is not null)
        {
            if (_resizeAnnotation.Tool == AnnotationTool.Text)
            {
                // 文本缩放修改的是字号，通过 EditTextStyleAction 撤销
                if (Math.Abs(_resizeAnnotation.FontSize - _resizeStartFontSize) > 0.1)
                {
                    var item = _resizeAnnotation;
                    vm.AnnotationPanel.CommitTextResize(item, _resizeStartFontSize);
                }
            }
            else
            {
                vm.AnnotationPanel.CommitResize(_resizeAnnotation, _resizeStartStart, _resizeStartEnd);
            }
            _resizeAnnotation = null;
            _resizeHandle = -1;
            UpdateAnnotationCursor();
            AnnotationCanvas.ReleaseMouseCapture();
            RedrawAnnotationOverlay();
            e.Handled = true;
            return;
        }

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
            FontFamily = new System.Windows.Media.FontFamily(textItem.FontFamily),
            FontWeight = textItem.IsBold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = textItem.IsItalic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = new SolidColorBrush(textItem.StrokeColor),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(128, textItem.StrokeColor.R, textItem.StrokeColor.G, textItem.StrokeColor.B)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            AcceptsReturn = false,
            CaretBrush = new SolidColorBrush(textItem.StrokeColor),
            Tag = textItem
        };
        var inputDecs = new TextDecorationCollection();
        if (textItem.IsUnderline) inputDecs.Add(TextDecorations.Underline);
        if (textItem.IsStrikethrough) inputDecs.Add(TextDecorations.Strikethrough);
        if (inputDecs.Count > 0) tb.TextDecorations = inputDecs;
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
            {
                var pts = AnnotationViewModel.CalculateArrowPoints(
                    new Point(sx, sy), new Point(ex, ey), thickness);
                if (pts.Length == 0) break;

                var arrowPoly = new System.Windows.Shapes.Polygon { Fill = brush };
                foreach (var p in pts)
                    arrowPoly.Points.Add(p);
                AnnotationCanvas.Children.Add(arrowPoly);
                break;
            }

            case AnnotationTool.Rectangle:
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = Math.Abs(ex - sx),
                    Height = Math.Abs(ey - sy),
                    Stroke = brush,
                    StrokeThickness = thickness,
                    RadiusX = item.CornerRadius * d,
                    RadiusY = item.CornerRadius * d
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
                    FontSize = item.FontSize * d,
                    FontFamily = new System.Windows.Media.FontFamily(item.FontFamily),
                    FontWeight = item.IsBold ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = item.IsItalic ? FontStyles.Italic : FontStyles.Normal
                };
                var decs = new TextDecorationCollection();
                if (item.IsUnderline) decs.Add(TextDecorations.Underline);
                if (item.IsStrikethrough) decs.Add(TextDecorations.Strikethrough);
                if (decs.Count > 0) tb.TextDecorations = decs;
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

            case AnnotationTool.Blur:
            {
                double bw = Math.Abs(ex - sx), bh = Math.Abs(ey - sy);
                double bx = Math.Min(sx, ex), by = Math.Min(sy, ey);
                if (bw < 1 || bh < 1) break;

                // 从截图中裁剪模糊区域并用 BlurEffect 显示
                if (DataContext is ScreenshotPreviewViewModel bvm && bvm.ScreenshotImage is { } src)
                {
                    // 像素空间裁剪区域
                    double scale = 1.0 / d;
                    int px = (int)Math.Max(0, (bx - iox) * scale);
                    int py = (int)Math.Max(0, (by - ioy) * scale);
                    int pw = (int)Math.Min(src.PixelWidth - px, bw * scale);
                    int ph = (int)Math.Min(src.PixelHeight - py, bh * scale);
                    if (pw > 0 && ph > 0)
                    {
                        var cropped = new CroppedBitmap(src, new Int32Rect(px, py, pw, ph));
                        var blurImage = new System.Windows.Controls.Image
                        {
                            Source = cropped,
                            Width = bw,
                            Height = bh,
                            Stretch = System.Windows.Media.Stretch.Fill,
                            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = item.BlurRadius * d * 2 }
                        };
                        Canvas.SetLeft(blurImage, bx);
                        Canvas.SetTop(blurImage, by);
                        AnnotationCanvas.Children.Add(blurImage);
                    }
                }

                // 虚线边框
                var blurBorder = new System.Windows.Shapes.Rectangle
                {
                    Width = bw,
                    Height = bh,
                    Stroke = System.Windows.Media.Brushes.DodgerBlue,
                    StrokeThickness = 1,
                    StrokeDashArray = [4, 2],
                    Fill = null
                };
                Canvas.SetLeft(blurBorder, bx);
                Canvas.SetTop(blurBorder, by);
                AnnotationCanvas.Children.Add(blurBorder);
                break;
            }
        }

        // 选中标注绘制虚线选框 + 缩放手柄
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

                // 缩放手柄（Pen 不支持缩放，只显示选框）
                if (item.Tool is not AnnotationTool.Pen)
                {
                    // 8 个缩放手柄: TL(0), T(1), TR(2), R(3), BR(4), B(5), BL(6), L(7)
                    const double hs = 7;
                    double cx = bounds.X + bounds.Width / 2;
                    double cy = bounds.Y + bounds.Height / 2;
                    Point[] handlePositions =
                    [
                        new(bounds.X, bounds.Y),                         // TL
                        new(cx, bounds.Y),                               // T
                        new(bounds.X + bounds.Width, bounds.Y),          // TR
                        new(bounds.X + bounds.Width, cy),                // R
                        new(bounds.X + bounds.Width, bounds.Y + bounds.Height), // BR
                        new(cx, bounds.Y + bounds.Height),               // B
                        new(bounds.X, bounds.Y + bounds.Height),         // BL
                        new(bounds.X, cy),                               // L
                    ];

                    foreach (var hp in handlePositions)
                    {
                        var handle = new System.Windows.Shapes.Rectangle
                        {
                            Width = hs,
                            Height = hs,
                            Fill = System.Windows.Media.Brushes.White,
                            Stroke = System.Windows.Media.Brushes.DodgerBlue,
                            StrokeThickness = 1.2
                        };
                        Canvas.SetLeft(handle, hp.X - hs / 2);
                        Canvas.SetTop(handle, hp.Y - hs / 2);
                        AnnotationCanvas.Children.Add(handle);
                    }
                }
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
            case AnnotationTool.Blur:
                return new Rect(new Point(Math.Min(sx, ex), Math.Min(sy, ey)), new Point(Math.Max(sx, ex), Math.Max(sy, ey)));
            case AnnotationTool.Text:
                var textSize = AnnotationViewModel.MeasureTextSize(item);
                return new Rect(sx, sy, textSize.Width * d, textSize.Height * d);
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

    /// <summary>检测 canvas DIP 坐标是否落在选中标注的 8 个缩放手柄上。返回手柄索引 0-7 或 -1。</summary>
    private int HitTestResizeHandle(AnnotationItem selected, Point canvasPos)
    {
        double d = 1.0 / GetAnnotationDpiScale();
        var bounds = GetAnnotationScreenBounds(selected, d);
        if (bounds.Width <= 0 && bounds.Height <= 0) return -1;
        bounds.Inflate(4, 4);

        const double hs = 7;
        double cx = bounds.X + bounds.Width / 2;
        double cy = bounds.Y + bounds.Height / 2;
        Point[] handlePositions =
        [
            new(bounds.X, bounds.Y),
            new(cx, bounds.Y),
            new(bounds.X + bounds.Width, bounds.Y),
            new(bounds.X + bounds.Width, cy),
            new(bounds.X + bounds.Width, bounds.Y + bounds.Height),
            new(cx, bounds.Y + bounds.Height),
            new(bounds.X, bounds.Y + bounds.Height),
            new(bounds.X, cy),
        ];

        for (int i = 0; i < handlePositions.Length; i++)
        {
            if (Math.Abs(canvasPos.X - handlePositions[i].X) <= hs &&
                Math.Abs(canvasPos.Y - handlePositions[i].Y) <= hs)
                return i;
        }
        return -1;
    }

    private static readonly Cursor[] ResizeHandleCursors =
    [
        Cursors.SizeNWSE, // TL
        Cursors.SizeNS,   // T
        Cursors.SizeNESW, // TR
        Cursors.SizeWE,   // R
        Cursors.SizeNWSE, // BR
        Cursors.SizeNS,   // B
        Cursors.SizeNESW, // BL
        Cursors.SizeWE,   // L
    ];

    /// <summary>根据拖拽的手柄索引更新标注的 Start/End 坐标（像素空间）。</summary>
    private void ApplyResize(AnnotationItem item, int handle, Point imgPoint)
    {
        // 文本标注：通过缩放比例改变字号
        if (item.Tool == AnnotationTool.Text)
        {
            var origSize = AnnotationViewModel.MeasureTextSize(new AnnotationItem
            {
                Tool = AnnotationTool.Text, Text = item.Text,
                FontSize = _resizeStartFontSize, FontFamily = item.FontFamily,
                IsBold = item.IsBold, IsItalic = item.IsItalic
            });
            double ox = item.Offset.X, oy = item.Offset.Y;
            double sx = _resizeStartStart.X + ox;
            double sy = _resizeStartStart.Y + oy;

            // 根据拖拽方向计算缩放比例
            double scale = 1.0;
            switch (handle)
            {
                case 4: // BR — 最常用
                case 2: // TR
                case 6: // BL
                case 0: // TL
                    double newW = Math.Abs(imgPoint.X - sx);
                    double newH = Math.Abs(imgPoint.Y - sy);
                    double scaleW = origSize.Width > 1 ? newW / origSize.Width : 1;
                    double scaleH = origSize.Height > 1 ? newH / origSize.Height : 1;
                    scale = Math.Max(scaleW, scaleH);
                    break;
                case 3: case 7: // R, L — 水平
                    scale = origSize.Width > 1 ? Math.Abs(imgPoint.X - sx) / origSize.Width : 1;
                    break;
                case 1: case 5: // T, B — 垂直
                    scale = origSize.Height > 1 ? Math.Abs(imgPoint.Y - sy) / origSize.Height : 1;
                    break;
            }
            item.FontSize = Math.Clamp(_resizeStartFontSize * scale, 8, 200);

            // 同步面板显示
            if (DataContext is ScreenshotPreviewViewModel vm)
            {
                vm.AnnotationPanel.FontSize = item.FontSize;
            }
            return;
        }

        // 非文本标注：修改 Start/End
        double oox = item.Offset.X, ooy = item.Offset.Y;
        double l = Math.Min(_resizeStartStart.X, _resizeStartEnd.X) + oox;
        double t = Math.Min(_resizeStartStart.Y, _resizeStartEnd.Y) + ooy;
        double r = Math.Max(_resizeStartStart.X, _resizeStartEnd.X) + oox;
        double b = Math.Max(_resizeStartStart.Y, _resizeStartEnd.Y) + ooy;

        switch (handle)
        {
            case 0: l = imgPoint.X; t = imgPoint.Y; break; // TL
            case 1: t = imgPoint.Y; break;                 // T
            case 2: r = imgPoint.X; t = imgPoint.Y; break; // TR
            case 3: r = imgPoint.X; break;                 // R
            case 4: r = imgPoint.X; b = imgPoint.Y; break; // BR
            case 5: b = imgPoint.Y; break;                 // B
            case 6: l = imgPoint.X; b = imgPoint.Y; break; // BL
            case 7: l = imgPoint.X; break;                 // L
        }

        item.Start = new Point(l - oox, t - ooy);
        item.End = new Point(r - oox, b - ooy);
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