using PixSnap.ViewModels;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace PixSnap.Views;

public partial class ScreenshotPreviewWindow : Window
{
    private bool _isPanningPreview;
    private Point _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    // 是否由滚轮触发了模式切换（此时由滚轮 handler 负责设置滚动位置，无需 Dispatcher 重置）
    private bool _zoomTriggeredModeSwitch;
    // 缩放热区宽度（像素）
    private const int ResizeBorderThickness = 5;
    // 图片边缘可多拖动的额外空白（与 XAML 中 Border.Padding 保持一致）
    private const double OverscrollPadding = 200;

    // ── 擦除画刷交互 ─────────────────────────────────────────────
    private bool _isEraserDrawing;
    private Point _lastEraserCanvasPoint;
    private bool _hasLastEraserCanvasPoint;
    private Polyline? _currentEraserStrokeVisual;

    private const int WM_NCHITTEST = 0x0084;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int HTCLIENT = 1;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    public ScreenshotPreviewWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
        StateChanged += OnWindowStateChanged;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        var viewModel = DataContext as ScreenshotPreviewViewModel;
        DetachViewModelHandlers(viewModel);
        viewModel?.Cleanup();
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
        if (e.PropertyName == nameof(ScreenshotPreviewViewModel.IsActualSize))
        {
            UpdatePreviewModeVisibility();
            return;
        }

        if (e.PropertyName == nameof(ScreenshotPreviewViewModel.ScreenshotImage))
        {
            UpdateFitZoomFactor();
        }

        if (e.PropertyName == nameof(ScreenshotPreviewViewModel.IsCropMode))
        {
            UpdateCropOverlayVisibility();
        }

        if (e.PropertyName == nameof(ScreenshotPreviewViewModel.IsRoundCornerMode))
        {
            RoundCornerPanel.Visibility = (DataContext is ScreenshotPreviewViewModel vm && vm.IsRoundCornerMode)
                ? Visibility.Visible : Visibility.Collapsed;
            // 重置面板位置
            if (RoundCornerPanel.Visibility == Visibility.Visible)
                RoundCornerPanelTranslate.X = RoundCornerPanelTranslate.Y = 0;
        }

        if (e.PropertyName == nameof(ScreenshotPreviewViewModel.IsEraserMode))
        {
            UpdateEraserCanvasState();
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
            }, System.Windows.Threading.DispatcherPriority.Loaded);
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

        e.Handled = true;

        var pointerPosition = e.GetPosition(PreviewViewport);
        var oldZoom = viewModel.IsActualSize ? viewModel.ZoomFactor : viewModel.FitZoomFactor;
        var zoomStep = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
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

            // 直接切换到最终缩放，只触发一次 layout
            _zoomTriggeredModeSwitch = true;
            viewModel.SetManualZoomFactor(newZoom);
            _zoomTriggeredModeSwitch = false;

            ActualSizeScrollViewer.UpdateLayout();
            ActualSizeScrollViewer.ScrollToHorizontalOffset(Math.Max(0, OverscrollPadding + contentX * newZoom - pointerPosition.X));
            ActualSizeScrollViewer.ScrollToVerticalOffset(Math.Max(0, OverscrollPadding + contentY * newZoom - pointerPosition.Y));
            return;
        }

        // 已在实际大小模式：以鼠标为中心缩放，保持鼠标下方图片像素不动
        var hContentX = (ActualSizeScrollViewer.HorizontalOffset + pointerPosition.X - OverscrollPadding) / oldZoom;
        var hContentY = (ActualSizeScrollViewer.VerticalOffset + pointerPosition.Y - OverscrollPadding) / oldZoom;

        viewModel.SetManualZoomFactor(newZoom);

        ActualSizeScrollViewer.UpdateLayout();
        ActualSizeScrollViewer.ScrollToHorizontalOffset(Math.Max(0, OverscrollPadding + hContentX * newZoom - pointerPosition.X));
        ActualSizeScrollViewer.ScrollToVerticalOffset(Math.Max(0, OverscrollPadding + hContentY * newZoom - pointerPosition.Y));
    }

    private void ActualSizeScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ScreenshotPreviewViewModel viewModel || !viewModel.IsActualSize)
        {
            return;
        }

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

    private void ActualSizeScrollViewer_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
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

    private void ActualSizeScrollViewer_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
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
    // 裁剪覆盖层
    // ══════════════════════════════════════════════════════════════════════════

    // 裁剪框在 PreviewViewport 中的位置（device pixels，未缩放）
    private Rect _cropRect;
    // 当前拖拽的 handle 标识（TL/TR/BL/BR/TC/BC/ML/MR）或 null
    private string? _dragHandle;
    private Point _dragStart;
    private Rect _dragStartRect;

    private void UpdateCropOverlayVisibility()
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;
        if (vm.IsCropMode)
        {
            CropOverlayCanvas.Visibility = Visibility.Visible;
            CropPanel.Visibility = Visibility.Visible;
            CropPanelTranslate.X = CropPanelTranslate.Y = 0;
            // ToggleCrop 已将 IsActualSize 设为 false，等 Measure/Arrange 完成后再初始化裁剪框
            // DispatcherPriority.Loaded 在 layout 完成之后执行，确保 ActualWidth/Height 已更新
            Dispatcher.InvokeAsync(() =>
            {
                PreviewViewport.UpdateLayout();
                _cropRect = GetImageDisplayRect();
                RefreshCropOverlay();
                SyncCropRectToViewModel();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            CropOverlayCanvas.Visibility = Visibility.Collapsed;
            CropPanel.Visibility = Visibility.Collapsed;
        }
    }

    private bool _syncingCropRect;

    private void OnCropPanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_syncingCropRect) return;
        if (e.PropertyName is nameof(CropViewModel.CropX) or nameof(CropViewModel.CropY)
            or nameof(CropViewModel.CropWidth) or nameof(CropViewModel.CropHeight))
        {
            // 文本框输入改变了 ViewModel，将变化同步到 overlay 矩形
            SyncViewModelToCropRect();
        }
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

    private void RefreshCropOverlay()
    {
        var vp = new Rect(0, 0, PreviewViewport.ActualWidth, PreviewViewport.ActualHeight);
        var r = _cropRect;

        // 4 块遮罩
        SetMask(CropMaskTop, 0, 0, vp.Width, r.Top);
        SetMask(CropMaskBottom, 0, r.Bottom, vp.Width, vp.Height - r.Bottom);
        SetMask(CropMaskLeft, 0, r.Top, r.Left, r.Height);
        SetMask(CropMaskRight, r.Right, r.Top, vp.Width - r.Right, r.Height);

        // 裁剪框
        Canvas.SetLeft(CropRect, r.Left);
        Canvas.SetTop(CropRect, r.Top);
        CropRect.Width = Math.Max(0, r.Width);
        CropRect.Height = Math.Max(0, r.Height);

        // 8 个 handle（半径 5，中心在角 / 边中点）
        PlaceHandle(HandleTL, r.Left, r.Top);
        PlaceHandle(HandleTR, r.Right, r.Top);
        PlaceHandle(HandleBL, r.Left, r.Bottom);
        PlaceHandle(HandleBR, r.Right, r.Bottom);
        PlaceHandle(HandleTC, r.Left + r.Width / 2, r.Top);
        PlaceHandle(HandleBC, r.Left + r.Width / 2, r.Bottom);
        PlaceHandle(HandleML, r.Left, r.Top + r.Height / 2);
        PlaceHandle(HandleMR, r.Right, r.Top + r.Height / 2);
    }

    private static void SetMask(Rectangle rect, double x, double y, double w, double h)
    {
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        rect.Width = Math.Max(0, w);
        rect.Height = Math.Max(0, h);
    }

    private static void PlaceHandle(Ellipse e, double cx, double cy)
    {
        Canvas.SetLeft(e, cx - e.Width / 2);
        Canvas.SetTop(e, cy - e.Height / 2);
    }

    // 同步裁剪框坐标到 ViewModel（把 overlay 坐标换算为图片像素）
    private void SyncCropRectToViewModel()
    {
        if (DataContext is not ScreenshotPreviewViewModel vm || vm.ScreenshotImage is null) return;
        var imgRect = GetImageDisplayRect();
        if (imgRect.Width <= 0 || imgRect.Height <= 0) return;

        var scaleX = vm.ScreenshotImage.PixelWidth / imgRect.Width;
        var scaleY = vm.ScreenshotImage.PixelHeight / imgRect.Height;

        var r = _cropRect;
        _syncingCropRect = true;
        try
        {
            vm.CropPanel.CropX = (int)Math.Round(Math.Clamp((r.Left - imgRect.Left) * scaleX, 0, vm.ScreenshotImage.PixelWidth));
            vm.CropPanel.CropY = (int)Math.Round(Math.Clamp((r.Top - imgRect.Top) * scaleY, 0, vm.ScreenshotImage.PixelHeight));
            vm.CropPanel.CropWidth = (int)Math.Round(Math.Clamp(r.Width * scaleX, 1, vm.ScreenshotImage.PixelWidth - vm.CropPanel.CropX));
            vm.CropPanel.CropHeight = (int)Math.Round(Math.Clamp(r.Height * scaleY, 1, vm.ScreenshotImage.PixelHeight - vm.CropPanel.CropY));
        }
        finally
        {
            _syncingCropRect = false;
        }
    }

    // 同步 ViewModel 字段变化到裁剪框（用户在文本框输入时）
    private void SyncViewModelToCropRect()
    {
        if (DataContext is not ScreenshotPreviewViewModel vm || vm.ScreenshotImage is null) return;
        var imgRect = GetImageDisplayRect();
        if (imgRect.Width <= 0 || imgRect.Height <= 0) return;

        var scaleX = imgRect.Width / vm.ScreenshotImage.PixelWidth;
        var scaleY = imgRect.Height / vm.ScreenshotImage.PixelHeight;

        _cropRect = new Rect(
            imgRect.Left + vm.CropPanel.CropX * scaleX,
            imgRect.Top + vm.CropPanel.CropY * scaleY,
            vm.CropPanel.CropWidth * scaleX,
            vm.CropPanel.CropHeight * scaleY);
        RefreshCropOverlay();
    }

    // ── 裁剪框拖动（整体移动） ────────────────────────────────────────────────

    private bool _isDraggingCropRect;
    private Point _cropDragStart;
    private Rect _cropRectAtDragStart;

    private void CropRect_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingCropRect = true;
        _cropDragStart = e.GetPosition(CropOverlayCanvas);
        _cropRectAtDragStart = _cropRect;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void CropRect_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingCropRect) return;
        var pos = e.GetPosition(CropOverlayCanvas);
        var delta = pos - _cropDragStart;
        var img = GetImageDisplayRect();
        var maxX = Math.Max(img.Left, img.Right - _cropRect.Width);
        var maxY = Math.Max(img.Top, img.Bottom - _cropRect.Height);
        var newX = Math.Clamp(_cropRectAtDragStart.Left + delta.X, img.Left, maxX);
        var newY = Math.Clamp(_cropRectAtDragStart.Top + delta.Y, img.Top, maxY);
        _cropRect = new Rect(newX, newY, _cropRect.Width, _cropRect.Height);
        RefreshCropOverlay();
        SyncCropRectToViewModel();
    }

    private void CropRect_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingCropRect = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    // ── 8 个 handle 拖拽（改变尺寸） ─────────────────────────────────────────

    private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragHandle = (sender as FrameworkElement)?.Tag as string;
        _dragStart = e.GetPosition(CropOverlayCanvas);
        _dragStartRect = _cropRect;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void Handle_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragHandle is null) return;
        var pos = e.GetPosition(CropOverlayCanvas);
        var dx = pos.X - _dragStart.X;
        var dy = pos.Y - _dragStart.Y;
        var r = _dragStartRect;
        const double minSize = 10;

        double l = r.Left, t = r.Top, rr = r.Right, b = r.Bottom;
        switch (_dragHandle)
        {
            case "TL": l = Math.Min(l + dx, rr - minSize); t = Math.Min(t + dy, b - minSize); break;
            case "TR": rr = Math.Max(rr + dx, l + minSize); t = Math.Min(t + dy, b - minSize); break;
            case "BL": l = Math.Min(l + dx, rr - minSize); b = Math.Max(b + dy, t + minSize); break;
            case "BR": rr = Math.Max(rr + dx, l + minSize); b = Math.Max(b + dy, t + minSize); break;
            case "TC": t = Math.Min(t + dy, b - minSize); break;
            case "BC": b = Math.Max(b + dy, t + minSize); break;
            case "ML": l = Math.Min(l + dx, rr - minSize); break;
            case "MR": rr = Math.Max(rr + dx, l + minSize); break;
        }

        var img = GetImageDisplayRect();
        l = Math.Max(img.Left, l);
        t = Math.Max(img.Top, t);
        rr = Math.Min(img.Right, rr);
        b = Math.Min(img.Bottom, b);
        _cropRect = new Rect(l, t, rr - l, b - t);
        RefreshCropOverlay();
        SyncCropRectToViewModel();
    }

    private void Handle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragHandle = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    // ── 裁剪 / 圆角 ViewModel 事件回调 ────────────────────────────────────────

    private void OnCropApplied(System.Windows.Media.Imaging.BitmapSource newImage)
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

    private void OnRoundCornerApplied(System.Windows.Media.Imaging.BitmapSource newImage)
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

    /// <summary>将鼠标在 PreviewViewport 的位置换算为图片像素坐标。</summary>
    private bool TryGetImagePixelPoint(Point canvasPos, out Point imgPixelPoint)
    {
        imgPixelPoint = default;
        if (DataContext is not ScreenshotPreviewViewModel vm || vm.ScreenshotImage is null)
            return false;

        var imgRect = GetImageDisplayRect();
        if (imgRect.Width <= 0 || imgRect.Height <= 0)
            return false;

        // 仅在图片显示区域内有效
        if (!imgRect.Contains(canvasPos))
            return false;

        imgPixelPoint = new Point(
            (canvasPos.X - imgRect.X) / imgRect.Width * vm.ScreenshotImage.PixelWidth,
            (canvasPos.Y - imgRect.Y) / imgRect.Height * vm.ScreenshotImage.PixelHeight);
        return true;
    }

    /// <summary>将图片像素坐标的画刷半径换算为画布显示半径。</summary>
    private double GetDisplayBrushRadius(double pixelRadius)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm || vm.ScreenshotImage is null)
            return pixelRadius;
        var imgRect = GetImageDisplayRect();
        return pixelRadius * imgRect.Width / vm.ScreenshotImage.PixelWidth;
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
        double step = Math.Max(1.0, vm.EraserPanel.BrushSize * 0.30);
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

    private void OnEraserInpaintApplied(System.Windows.Media.Imaging.BitmapSource _)
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

    private async void EraserCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isEraserDrawing) return;
        _isEraserDrawing = false;
        _hasLastEraserCanvasPoint = false;
        _currentEraserStrokeVisual = null;
        EraserCanvas.ReleaseMouseCapture();
        e.Handled = true;

        // 鼠标抬起后立即触发 AI 修复
        if (DataContext is ScreenshotPreviewViewModel vm && vm.ScreenshotImage is not null)
            await vm.EraserPanel.RunInpaintAsync(vm.ScreenshotImage);
    }

    private Border? _draggingPanel;
    private TranslateTransform? _draggingTransform;
    private Point _panelDragStart;
    private Point _panelTransformStart;

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
        // 最大化时去掉圆角避免黑角，还原时恢复圆角
        var radius = WindowState == WindowState.Maximized
            ? new CornerRadius(0)
            : new CornerRadius(10);
        RootBorder.CornerRadius = radius;
        OverlayBorder.CornerRadius = radius;
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
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            int hit = GetResizeHitTest(lParam);
            if (hit != HTCLIENT)
            {
                handled = true;
                return new IntPtr(hit);
            }
        }
        else if (msg == WM_GETMINMAXINFO)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [DllImport("user32")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        IntPtr monitor = MonitorFromWindow(hwnd, 0x00000002 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitor, ref mi);
            var wa = mi.rcWork; // 工作区（排除任务栏）
                                // 以窗口客户端坐标表示：位置相对于左上角，尺寸为工作区大小
            mmi.ptMaxPosition.x = wa.left;
            mmi.ptMaxPosition.y = wa.top;
            mmi.ptMaxSize.x = wa.right - wa.left;
            mmi.ptMaxSize.y = wa.bottom - wa.top;
        }
        // 设置最小跟踪尺寸（考虑 DPI 缩放）
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            int minW = (int)Math.Round(MinWidth * dpi.DpiScaleX);
            int minH = (int)Math.Round(MinHeight * dpi.DpiScaleY);
            if (minW > 0) mmi.ptMinTrackSize.x = minW;
            if (minH > 0) mmi.ptMinTrackSize.y = minH;
        }
        catch { }
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private int GetResizeHitTest(IntPtr lParam)
    {
        // lParam 低16位=屏幕X，高16位=屏幕Y
        int screenX = unchecked((short)(lParam.ToInt32() & 0xFFFF));
        int screenY = unchecked((short)((lParam.ToInt32() >> 16) & 0xFFFF));
        Point pt = PointFromScreen(new Point(screenX, screenY));

        int x = (int)pt.X;
        int y = (int)pt.Y;
        int w = (int)ActualWidth;
        int h = (int)ActualHeight;
        int b = ResizeBorderThickness;

        bool onLeft = x < b;
        bool onRight = x > w - b;
        bool onTop = y < b;
        bool onBottom = y > h - b;

        if (onTop && onLeft) return HTTOPLEFT;
        if (onTop && onRight) return HTTOPRIGHT;
        if (onBottom && onLeft) return HTBOTTOMLEFT;
        if (onBottom && onRight) return HTBOTTOMRIGHT;
        if (onTop) return HTTOP;
        if (onBottom) return HTBOTTOM;
        if (onLeft) return HTLEFT;
        if (onRight) return HTRIGHT;

        return HTCLIENT;
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
        const double popupWidthEstimate = 200;
        const double popupHeightEstimate = 94;
        double x = Math.Clamp(pos.X, 0, Math.Max(0, PreviewViewport.ActualWidth - popupWidthEstimate));
        double y = Math.Clamp(pos.Y, 0, Math.Max(0, PreviewViewport.ActualHeight - popupHeightEstimate));

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