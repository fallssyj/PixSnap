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
    // ── 窗口尺寸常量（XAML 通过 x:Static 引用）─────────────────────────
    public static readonly double DefaultWidth = 1040;
    public static readonly double DefaultHeight = 760;
    public static readonly double MinWindowWidth = 1000;
    public static readonly double MinWindowHeight = 520;

    // ── 内部常量 ──────────────────────────────────────────────────────
    private const int ResizeBorderThickness = 5;
    private const double OverscrollPadding = 200;
    private const double ZoomStepFactor = 1.1;
    private const double EraserSampleStepRatio = 0.30;
    private const double PopupWidthEstimate = 200;
    private const double PopupHeightEstimate = 94;
    private const double ColorGridWidth = 132;

    #region 字段

    // ── 窗口 / 生命周期 ──────────────────────────────────────────────
    private HwndSourceHook? _wndProcHook;

    // ── 缩放 / 平移 ──────────────────────────────────────────────────
    private bool _zoomTriggeredModeSwitch;
    private bool _isPanningPreview;
    private Point _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;

    // ── 裁剪 ─────────────────────────────────────────────────────────
    private bool _syncingCropRect;

    #endregion

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
                    RoundCornerPanel.ResetPosition();
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
            CropPanel.ResetPosition();
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