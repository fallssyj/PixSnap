using PixSnap.Models;
using PixSnap.Services;
using PixSnap.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace PixSnap.Views;

// 该窗口负责输入处理、坐标换算和覆盖层绘制。
// 与截图结果相关的语义状态仍通过 RegionSelectorViewModel 暴露给 XAML 绑定。
public partial class RegionSelectorWindow : Window
{
    private const double InfoBubbleMargin = 24;
    private const double SnapThreshold = 8;

    private readonly IReadOnlyDictionary<IntPtr, WindowInfo> _windowsByHandle;
    private readonly IReadOnlyList<ScreenInfo> _screens;
    private readonly RegionSelectorViewModel _viewModel = new();
    private bool _isMouseDown;
    private bool _isDraggingSelection;
    private IntPtr _windowHandle;
    private Rect _overlayScreenBounds;
    private DpiScale _dpiScale;
    private WindowInfo? _hoveredWindow;
    private ScreenInfo? _hoveredScreen;
    private Point _mouseDownPoint;
    private Point _currentPoint;
    private bool _hintsAtBottom;
    private List<double>? _snapXEdges;
    private List<double>? _snapYEdges;

    public RegionSelectorWindow(IScreenCaptureService screenCaptureService)
    {
        InitializeComponent();
        DataContext = _viewModel;

        _screens = screenCaptureService.GetScreens();
        _windowsByHandle = screenCaptureService
            .GetWindows()
            .GroupBy(window => window.Hwnd)
            .ToDictionary(group => group.Key, group => group.First());

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Loaded += OnLoaded;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseRightButtonUp += OnMouseRightButtonUp;
        KeyDown += OnKeyDown;
    }

    public CaptureSelection? Selection { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _dpiScale = VisualTreeHelper.GetDpi(this);
        if (NativeWindowHelper.TryGetWindowRect(_windowHandle, out var overlayRect))
        {
            _overlayScreenBounds = overlayRect;
        }

        // Mask 和提示气泡属于纯视图层元素，初始化时直接按当前覆盖窗口尺寸定位。
        Mask.Width = Width;
        Mask.Height = Height;
        UpdateHintPanelPositions(null);

        // Activate() 在 WPF 内部调用 SetForegroundWindow，确保窗口获得 Win32 键盘焦点。
        // ShowDialog() 默认的 ShowWindow(SW_SHOW) 在进程无前台权限时（如从托盘触发）
        // 只能显示窗口，不能激活它，导致首次截图键盘事件完全无法接收。
        Activate();
        Focus();
        var cursorPos = NativeWindowHelper.GetCursorPosition();
        UpdateHover(cursorPos);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isMouseDown = true;
        _isDraggingSelection = false;
        _mouseDownPoint = e.GetPosition(this);
        _currentPoint = _mouseDownPoint;
        Selection = null;
        _viewModel.ClearSelection();
        SelectionRect.Visibility = Visibility.Collapsed;
        BuildSnapEdges();
        CaptureMouse();
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _currentPoint = e.GetPosition(this);

        if (_isMouseDown)
        {
            if (!_isDraggingSelection && IsDragGesture(_mouseDownPoint, _currentPoint))
            {
                _isDraggingSelection = true;
                WindowHighlightRect.Visibility = Visibility.Collapsed;
                _viewModel.ClearHighlight();
            }

            if (_isDraggingSelection)
            {
                UpdateSelection();
                return;
            }
        }

        UpdateHover(LocalPointToScreenPoint(_currentPoint));
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isMouseDown)
        {
            return;
        }

        _currentPoint = e.GetPosition(this);
        _isMouseDown = false;
        ReleaseMouseCapture();

        if (_isDraggingSelection)
        {
            _isDraggingSelection = false;
            UpdateSelection();
            var rect = NormalizeRect(_mouseDownPoint, _currentPoint);
            if (rect.Width > 0 && rect.Height > 0)
            {
                var screenRect = LocalRectToScreenRect(rect);
                Selection = new CaptureSelection
                {
                    Mode = CaptureSelectionMode.Region,
                    Region = screenRect
                };
                DialogResult = true;
            }

            return;
        }

        UpdateHover(LocalPointToScreenPoint(_currentPoint));

        if (_hoveredWindow is not null)
        {
            Selection = new CaptureSelection
            {
                Mode = CaptureSelectionMode.Window,
                WindowHandle = _hoveredWindow.Hwnd,
                WindowTitle = string.IsNullOrWhiteSpace(_hoveredWindow.Title) ? _hoveredWindow.ClassName : _hoveredWindow.Title
            };
            DialogResult = true;
        }
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        DialogResult = false;
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            return;
        }

        if (e.Key == Key.Space && _hoveredScreen is not null)
        {
            Selection = new CaptureSelection
            {
                Mode = CaptureSelectionMode.FullScreen,
                ScreenIndex = _hoveredScreen.Index
            };
            DialogResult = true;
        }
    }

    private void UpdateSelection()
    {
        var raw = NormalizeRect(_mouseDownPoint, _currentPoint);

        // 边缘吸附：将选区四边对齐到最近的窗口边缘
        double left = SnapValue(raw.Left, _snapXEdges);
        double top = SnapValue(raw.Top, _snapYEdges);
        double right = SnapValue(raw.Right, _snapXEdges);
        double bottom = SnapValue(raw.Bottom, _snapYEdges);
        var rect = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

        _viewModel.UpdateSelection(LocalRectToScreenRect(rect));

        // 矩形框是即时视觉反馈，直接操作命名元素比为每个像素属性建立绑定更清晰。
        SelectionRect.Visibility = rect.Width > 0 && rect.Height > 0 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(SelectionRect, rect.X);
        Canvas.SetTop(SelectionRect, rect.Y);
        SelectionRect.Width = rect.Width;
        SelectionRect.Height = rect.Height;

        // 拖拽中自动避让选区边缘，避免遮挡精确对齐。
        UpdateHintPanelPositions(rect);
    }

    private void BuildSnapEdges()
    {
        var xEdges = new HashSet<double>();
        var yEdges = new HashSet<double>();
        foreach (var win in _windowsByHandle.Values)
        {
            if (!NativeWindowHelper.TryGetWindowRect(win.Hwnd, out var wr)) continue;
            var local = ScreenRectToLocalRect(wr);
            xEdges.Add(local.Left);
            xEdges.Add(local.Right);
            yEdges.Add(local.Top);
            yEdges.Add(local.Bottom);
        }
        // 也加入屏幕边缘
        foreach (var scr in _screens)
        {
            var b = scr.Bounds;
            var local = ScreenRectToLocalRect(new Rect(b.X, b.Y, b.Width, b.Height));
            xEdges.Add(local.Left);
            xEdges.Add(local.Right);
            yEdges.Add(local.Top);
            yEdges.Add(local.Bottom);
        }
        _snapXEdges = xEdges.OrderBy(v => v).ToList();
        _snapYEdges = yEdges.OrderBy(v => v).ToList();
    }

    private static double SnapValue(double value, List<double>? edges)
    {
        if (edges is null || edges.Count == 0) return value;
        // Binary search for nearest edge within threshold
        int idx = edges.BinarySearch(value);
        if (idx < 0) idx = ~idx;
        double best = value;
        double bestDist = SnapThreshold + 1;
        for (int i = Math.Max(0, idx - 1); i <= Math.Min(edges.Count - 1, idx + 1); i++)
        {
            double dist = Math.Abs(edges[i] - value);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = edges[i];
            }
        }
        return bestDist <= SnapThreshold ? best : value;
    }

    private void UpdateHover(Point screenPoint)
    {
        _hoveredScreen = FindScreen(screenPoint);
        _hoveredWindow = FindWindowAtPoint(screenPoint);

        if (_hoveredWindow is not null && NativeWindowHelper.TryGetWindowRect(_hoveredWindow.Hwnd, out var windowRect))
        {
            var localRect = ScreenRectToLocalRect(windowRect);
            _viewModel.UpdateWindowHighlight(new Rect(windowRect.Left, windowRect.Top, windowRect.Width, windowRect.Height));

            // 高亮框的位置依赖 DPI 与窗口坐标换算，保留在 View 层处理更合适。
            WindowHighlightRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(WindowHighlightRect, localRect.X);
            Canvas.SetTop(WindowHighlightRect, localRect.Y);
            WindowHighlightRect.Width = localRect.Width;
            WindowHighlightRect.Height = localRect.Height;
            Canvas.SetLeft(InfoBubble, Math.Max(InfoBubbleMargin, localRect.X));
            Canvas.SetTop(InfoBubble, Math.Max(InfoBubbleMargin, localRect.Y - 58));
        }
        else
        {
            _viewModel.ClearHighlight();
            WindowHighlightRect.Visibility = Visibility.Collapsed;
            var localPoint = ScreenPointToLocalPoint(screenPoint);
            Canvas.SetLeft(InfoBubble, Math.Max(InfoBubbleMargin, localPoint.X + 18));
            Canvas.SetTop(InfoBubble, Math.Max(InfoBubbleMargin, localPoint.Y + 18));
        }

        // 底部提示始终固定在右下角（两个分支共享同一定位逻辑）
        PositionFooterHint();

        _viewModel.UpdateHover(_hoveredWindow, _hoveredScreen);
    }

    /// <summary>将底部操作提示固定在画布右下角。</summary>
    private void PositionFooterHint()
    {
        if (FooterHint is null) return;
        var canvasWidth  = RootCanvas.ActualWidth  > 0 ? RootCanvas.ActualWidth  : Width;
        var canvasHeight = RootCanvas.ActualHeight > 0 ? RootCanvas.ActualHeight : Height;
        var footerSize = MeasureElement(FooterHint);
        Canvas.SetLeft(FooterHint, Math.Max(InfoBubbleMargin, canvasWidth  - footerSize.Width  - InfoBubbleMargin));
        Canvas.SetTop(FooterHint,  Math.Max(InfoBubbleMargin, canvasHeight - footerSize.Height - InfoBubbleMargin));
    }

    private ScreenInfo? FindScreen(Point screenPoint)
    {
        return _screens.FirstOrDefault(screen =>
        {
            var bounds = screen.Bounds;
            return screenPoint.X >= bounds.Left
                && screenPoint.X < bounds.Right
                && screenPoint.Y >= bounds.Top
                && screenPoint.Y < bounds.Bottom;
        });
    }

    private WindowInfo? FindWindowAtPoint(Point screenPoint)
    {
        var hwnd = NativeWindowHelper.FindTopWindowAtPoint(screenPoint, _windowHandle);
        if (hwnd != IntPtr.Zero && _windowsByHandle.TryGetValue(hwnd, out var window))
            return window;
        return null;
    }

    private Rect ScreenRectToLocalRect(Rect screenRect)
    {
        var topLeft = ScreenPointToLocalPoint(new Point(screenRect.Left, screenRect.Top));
        var bottomRight = ScreenPointToLocalPoint(new Point(screenRect.Right, screenRect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private Rect LocalRectToScreenRect(Rect localRect)
    {
        var topLeft = LocalPointToScreenPoint(new Point(localRect.Left, localRect.Top));
        var bottomRight = LocalPointToScreenPoint(new Point(localRect.Right, localRect.Bottom));
        return new Rect(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Abs(bottomRight.X - topLeft.X),
            Math.Abs(bottomRight.Y - topLeft.Y));
    }

    private Point ScreenPointToLocalPoint(Point screenPoint)
    {
        // WGC 选择逻辑使用屏幕坐标，WPF 绘制使用设备无关单位，这里统一做一次转换。
        return new Point(
            (screenPoint.X - _overlayScreenBounds.Left) / _dpiScale.DpiScaleX,
            (screenPoint.Y - _overlayScreenBounds.Top) / _dpiScale.DpiScaleY);
    }

    private Point LocalPointToScreenPoint(Point localPoint)
    {
        return new Point(
            _overlayScreenBounds.Left + (localPoint.X * _dpiScale.DpiScaleX),
            _overlayScreenBounds.Top + (localPoint.Y * _dpiScale.DpiScaleY));
    }

    private static bool IsDragGesture(Point start, Point current)
    {
        return Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;
    }

    private static Rect NormalizeRect(Point start, Point end)
    {
        return new Rect(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X),
            Math.Abs(end.Y - start.Y));
    }

    private void UpdateHintPanelPositions(Rect? occupiedRect)
    {
        if (InfoBubble is null || FooterHint is null)
            return;

        var canvasWidth = RootCanvas.ActualWidth > 0 ? RootCanvas.ActualWidth : Width;
        var canvasHeight = RootCanvas.ActualHeight > 0 ? RootCanvas.ActualHeight : Height;

        var infoSize = MeasureElement(InfoBubble);
        var footerSize = MeasureElement(FooterHint);

        double infoX = InfoBubbleMargin;
        double footerX = Math.Max(InfoBubbleMargin, canvasWidth - footerSize.Width - InfoBubbleMargin);

        double infoTopY = InfoBubbleMargin;
        double infoBottomY = Math.Max(InfoBubbleMargin, canvasHeight - infoSize.Height - InfoBubbleMargin);
        double footerTopY = InfoBubbleMargin;
        double footerBottomY = Math.Max(InfoBubbleMargin, canvasHeight - footerSize.Height - InfoBubbleMargin);

        if (occupiedRect is { } rect && rect.Width > 0 && rect.Height > 0)
        {
            // 用“当前拖拽位置”做触发判定，而不是选区 Top，避免起点在上方时永远不翻转。
            double triggerY = Math.Clamp(_currentPoint.Y, 0, canvasHeight);
            double upperThreshold = canvasHeight * 0.45;
            double lowerThreshold = canvasHeight * 0.55;

            if (triggerY <= upperThreshold)
                _hintsAtBottom = true;
            else if (triggerY >= lowerThreshold)
                _hintsAtBottom = false;
        }
        else
        {
            _hintsAtBottom = false;
        }

        bool moveToBottom = _hintsAtBottom;

        Canvas.SetLeft(InfoBubble, infoX);
        Canvas.SetTop(InfoBubble, moveToBottom ? infoBottomY : infoTopY);

        Canvas.SetLeft(FooterHint, footerX);
        Canvas.SetTop(FooterHint, moveToBottom ? footerBottomY : footerTopY);
    }

    private static Size MeasureElement(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var measured = element.DesiredSize;
        var width = measured.Width > 0 ? measured.Width : element.ActualWidth;
        var height = measured.Height > 0 ? measured.Height : element.ActualHeight;
        return new Size(width, height);
    }
}