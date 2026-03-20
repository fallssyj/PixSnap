using PixSnap.ViewModels;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    // 缩放热区宽度（像素）
    private const int ResizeBorderThickness = 5;

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

        StateChanged += OnWindowStateChanged;
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
    }

    private void AttachViewModelHandlers(ScreenshotPreviewViewModel? viewModel)
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void DetachViewModelHandlers(ScreenshotPreviewViewModel? viewModel)
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
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
        if (!viewModel.IsActualSize)
        {
            viewModel.SetManualZoomFactor(oldZoom);
            ActualSizeScrollViewer.UpdateLayout();
        }

        var zoomStep = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        var newZoom = Math.Clamp(
            oldZoom * zoomStep,
            ScreenshotPreviewViewModel.MinZoomFactor,
            ScreenshotPreviewViewModel.MaxZoomFactor);

        if (Math.Abs(newZoom - oldZoom) < 0.001)
        {
            return;
        }

        var contentX = (ActualSizeScrollViewer.HorizontalOffset + pointerPosition.X) / oldZoom;
        var contentY = (ActualSizeScrollViewer.VerticalOffset + pointerPosition.Y) / oldZoom;

        viewModel.SetManualZoomFactor(newZoom);

        ActualSizeScrollViewer.UpdateLayout();
        ActualSizeScrollViewer.ScrollToHorizontalOffset(Math.Max(0, contentX * newZoom - pointerPosition.X));
        ActualSizeScrollViewer.ScrollToVerticalOffset(Math.Max(0, contentY * newZoom - pointerPosition.Y));
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
}