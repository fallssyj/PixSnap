using PixSnap.Models;
using PixSnap.Services;
using PixSnap.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private readonly Dictionary<int, BitmapSource>? _preCaptures;
    private readonly IReadOnlyList<(IntPtr Hwnd, Rect Rect, string Title, string ClassName)>? _windowSnapshot;
    private readonly RegionSelectorViewModel _viewModel = new();
    private bool _isMouseDown;
    private bool _isDraggingSelection;
    private IntPtr _windowHandle;
    private WindowInfo? _hoveredWindow;
    private Rect _hoveredWindowRect;
    private ScreenInfo? _hoveredScreen;
    private Point _mouseDownPoint;
    private Point _currentPoint;
    private bool _hintsAtBottom;
    private List<double>? _snapXEdges;
    private List<double>? _snapYEdges;

    public RegionSelectorWindow(
        IScreenCaptureService screenCaptureService,
        Dictionary<int, BitmapSource>? preCaptures = null,
        IReadOnlyList<(IntPtr Hwnd, Rect Rect, string Title, string ClassName)>? windowSnapshot = null)
    {
        InitializeComponent();
        DataContext = _viewModel;

        _screens = screenCaptureService.GetScreens();
        _preCaptures = preCaptures;
        _windowSnapshot = windowSnapshot;
        _windowsByHandle = _windowSnapshot is { Count: > 0 }
            ? _windowSnapshot
                .GroupBy(window => window.Hwnd)
                .ToDictionary(
                    group => group.Key,
                    group => new WindowInfo
                    {
                        Hwnd = group.Key,
                        Title = group.First().Title,
                        ClassName = group.First().ClassName,
                        Icon = null
                    })
            : screenCaptureService
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

        // 将预截取的屏幕截图作为不透明背景插入到 Canvas 最底层，
        // 这样即使选区窗口抢走了焦点，用户看到的仍然是截图前的桌面状态。
        if (_preCaptures is not null)
        {
            int insertIndex = 0;
            foreach (var screen in _screens)
            {
                if (_preCaptures.TryGetValue(screen.Index, out var capture))
                {
                    var bounds = screen.Bounds;
                    var screenRect = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    var localRect = ScreenRectToLocalRect(screenRect);
                    var image = new Image
                    {
                        Source = capture,
                        Width = localRect.Width,
                        Height = localRect.Height,
                        Stretch = Stretch.Fill,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(image, localRect.X);
                    Canvas.SetTop(image, localRect.Y);
                    RootCanvas.Children.Insert(insertIndex++, image);
                }
            }
        }

        // Mask 和提示气泡属于纯视图层元素，初始化时直接按当前覆盖窗口尺寸定位。
        Mask.Width = Width;
        Mask.Height = Height;
        UpdateHintPanelPositions(null);
        PositionModeTogglePanel();

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
                    Region = screenRect,
                    IsRecording = _viewModel.IsRecordingMode,
                    EnableMicrophone = _viewModel.EnableMicrophone,
                    EnableSystemAudio = _viewModel.EnableSystemAudio,
                    Quality = _viewModel.RecordingQuality,
                    CapturePixelCount = (long)screenRect.Width * (long)screenRect.Height
                };
                DialogResult = true;
            }

            return;
        }

        UpdateHover(LocalPointToScreenPoint(_currentPoint));

        if (_hoveredWindow is not null)
        {
            var windowPixels = (long)_hoveredWindowRect.Width * (long)_hoveredWindowRect.Height;
            Selection = new CaptureSelection
            {
                Mode = CaptureSelectionMode.Window,
                WindowHandle = _hoveredWindow.Hwnd,
                WindowTitle = string.IsNullOrWhiteSpace(_hoveredWindow.Title) ? _hoveredWindow.ClassName : _hoveredWindow.Title,
                WindowRect = _hoveredWindowRect,
                IsRecording = _viewModel.IsRecordingMode,
                EnableMicrophone = _viewModel.EnableMicrophone,
                EnableSystemAudio = _viewModel.EnableSystemAudio,
                Quality = _viewModel.RecordingQuality,
                CapturePixelCount = windowPixels
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
                ScreenIndex = _hoveredScreen.Index,
                IsRecording = _viewModel.IsRecordingMode,
                EnableMicrophone = _viewModel.EnableMicrophone,
                EnableSystemAudio = _viewModel.EnableSystemAudio,
                Quality = _viewModel.RecordingQuality,
                CapturePixelCount = (long)_hoveredScreen.Bounds.Width * _hoveredScreen.Bounds.Height
            };
            DialogResult = true;
        }

        if (e.Key == Key.Tab)
        {
            _viewModel.IsRecordingMode = !_viewModel.IsRecordingMode;
            ApplyModeToggleVisuals();
            e.Handled = true;
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
        // 预快照中可能包含已隐藏的焦点敏感窗口，也加入吸附边缘
        if (_windowSnapshot is not null)
        {
            foreach (var (_, snapRect, _, _) in _windowSnapshot)
            {
                var local = ScreenRectToLocalRect(snapRect);
                xEdges.Add(local.Left);
                xEdges.Add(local.Right);
                yEdges.Add(local.Top);
                yEdges.Add(local.Bottom);
            }
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
        _hoveredWindow = FindWindowAtPoint(screenPoint, out _hoveredWindowRect);

        if (_hoveredWindow is not null && _hoveredWindowRect is { Width: > 0, Height: > 0 })
        {
            var localRect = ScreenRectToLocalRect(_hoveredWindowRect);
            _viewModel.UpdateWindowHighlight(new Rect(_hoveredWindowRect.Left, _hoveredWindowRect.Top, _hoveredWindowRect.Width, _hoveredWindowRect.Height));

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
        var canvasWidth = RootCanvas.ActualWidth > 0 ? RootCanvas.ActualWidth : Width;
        var canvasHeight = RootCanvas.ActualHeight > 0 ? RootCanvas.ActualHeight : Height;
        var footerSize = MeasureElement(FooterHint);
        Canvas.SetLeft(FooterHint, Math.Max(InfoBubbleMargin, canvasWidth - footerSize.Width - InfoBubbleMargin));
        Canvas.SetTop(FooterHint, Math.Max(InfoBubbleMargin, canvasHeight - footerSize.Height - InfoBubbleMargin));
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
        return FindWindowAtPoint(screenPoint, out _);
    }

    /// <summary>
    /// 在指定屏幕坐标点查找窗口。
    /// 截屏模式下使用预快照（反映截图瞬间的真实 Z 序），
    /// 录屏模式下使用实时检测（录屏需要活窗口句柄）。
    /// </summary>
    private WindowInfo? FindWindowAtPoint(Point screenPoint, out Rect windowRect)
    {
        windowRect = Rect.Empty;

        // 录屏模式必须使用实时检测（录屏 API 需要活窗口句柄）
        bool useSnapshot = _windowSnapshot is not null && !_viewModel.IsRecordingMode;

        if (useSnapshot)
        {
            foreach (var (snapHwnd, snapRect, snapTitle, snapClassName) in _windowSnapshot!)
            {
                if (snapHwnd != _windowHandle && snapRect.Contains(screenPoint))
                {
                    windowRect = snapRect;

                    if (_windowsByHandle.TryGetValue(snapHwnd, out var snapWindow))
                        return snapWindow;

                    return new WindowInfo
                    {
                        Hwnd = snapHwnd,
                        Title = snapTitle,
                        ClassName = snapClassName
                    };
                }
            }
            return null;
        }

        // 无预快照或录屏模式：实时检测
        var hwnd = NativeWindowHelper.FindTopWindowAtPoint(screenPoint, _windowHandle);
        if (hwnd != IntPtr.Zero && _windowsByHandle.TryGetValue(hwnd, out var window))
        {
            if (NativeWindowHelper.TryGetWindowRect(hwnd, out windowRect))
                return window;
        }

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
        // 交给 WPF 自身做屏幕像素 <-> DIP 转换，避免手工缓存窗口矩形与 DPI 在某些缩放比下产生偏移。
        return PointFromScreen(screenPoint);
    }

    private Point LocalPointToScreenPoint(Point localPoint)
    {
        return PointToScreen(localPoint);
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

    // ── 截屏 / 录屏 模式切换 ─────────────────────────────────────────────────

    private void ScreenshotModeBtn_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.IsRecordingMode = false;
        ApplyModeToggleVisuals();
        e.Handled = true;
    }

    private void RecordingModeBtn_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.IsRecordingMode = true;
        ApplyModeToggleVisuals();
        e.Handled = true;
    }

    private void ApplyModeToggleVisuals()
    {
        var accent = FindResource("OverlaySelectionStrokeBrush") as Brush ?? Brushes.CornflowerBlue;
        var recording = _viewModel.IsRecordingMode;

        ScreenshotModeBtn.Background = recording ? Brushes.Transparent : accent;
        ScreenshotModeBtnText.Foreground = recording ? (FindResource("OverlayFooterForegroundBrush") as Brush ?? Brushes.White) : Brushes.White;

        RecordingModeBtn.Background = recording ? accent : Brushes.Transparent;
        RecordingModeBtnText.Foreground = recording ? Brushes.White : (FindResource("OverlayFooterForegroundBrush") as Brush ?? Brushes.White);

        FooterHintText.Text = recording
            ? "左键单击高亮窗口录屏，左键拖动录矩形，Space 录当前显示器，Tab 切换模式，Esc / 右键退出"
            : "左键单击高亮窗口截图，左键拖动截矩形，Space 截当前显示器，Tab 切换模式，Esc / 右键退出";

        // 音频按钮仅在录屏模式显示
        var audioVisibility = recording ? Visibility.Visible : Visibility.Collapsed;
        AudioSeparator.Visibility = audioVisibility;
        MicToggleBtn.Visibility = audioVisibility;
        SysAudioToggleBtn.Visibility = audioVisibility;

        // 画质按钮仅在录屏模式显示
        QualitySeparator.Visibility = audioVisibility;
        QualityStandardBtn.Visibility = audioVisibility;
        QualityHighBtn.Visibility = audioVisibility;
        QualityOriginalBtn.Visibility = audioVisibility;

        if (recording)
        {
            ApplyAudioToggleVisuals();
            ApplyQualityToggleVisuals();
        }

        // 重新定位（尺寸可能变化）
        PositionModeTogglePanel();

        // 切换后刷新悬停提示文本
        var cursorPos = NativeWindowHelper.GetCursorPosition();
        UpdateHover(cursorPos);
    }

    private void MicToggleBtn_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.EnableMicrophone = !_viewModel.EnableMicrophone;
        ApplyAudioToggleVisuals();
        e.Handled = true;
    }

    private void SysAudioToggleBtn_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.EnableSystemAudio = !_viewModel.EnableSystemAudio;
        ApplyAudioToggleVisuals();
        e.Handled = true;
    }

    private void ApplyAudioToggleVisuals()
    {
        var accent = FindResource("OverlaySelectionStrokeBrush") as Brush ?? Brushes.CornflowerBlue;
        var dimFg = FindResource("OverlayFooterForegroundBrush") as Brush ?? Brushes.White;

        MicToggleBtn.Background = _viewModel.EnableMicrophone ? accent : Brushes.Transparent;
        MicToggleIcon.Foreground = _viewModel.EnableMicrophone ? Brushes.White : dimFg;
        MicToggleIcon.Opacity = _viewModel.EnableMicrophone ? 1.0 : 0.5;

        SysAudioToggleBtn.Background = _viewModel.EnableSystemAudio ? accent : Brushes.Transparent;
        SysAudioToggleIcon.Foreground = _viewModel.EnableSystemAudio ? Brushes.White : dimFg;
        SysAudioToggleIcon.Opacity = _viewModel.EnableSystemAudio ? 1.0 : 0.5;
    }

    private void QualityStandardBtn_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.RecordingQuality = RecordingQuality.Standard;
        ApplyQualityToggleVisuals();
        e.Handled = true;
    }

    private void QualityHighBtn_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.RecordingQuality = RecordingQuality.High;
        ApplyQualityToggleVisuals();
        e.Handled = true;
    }

    private void QualityOriginalBtn_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.RecordingQuality = RecordingQuality.Original;
        ApplyQualityToggleVisuals();
        e.Handled = true;
    }

    private void ApplyQualityToggleVisuals()
    {
        var accent = FindResource("OverlaySelectionStrokeBrush") as Brush ?? Brushes.CornflowerBlue;
        var dimFg = FindResource("OverlayFooterForegroundBrush") as Brush ?? Brushes.White;
        var quality = _viewModel.RecordingQuality;

        QualityStandardBtn.Background = quality == RecordingQuality.Standard ? accent : Brushes.Transparent;
        QualityStandardText.Foreground = quality == RecordingQuality.Standard ? Brushes.White : dimFg;
        QualityStandardText.Opacity = quality == RecordingQuality.Standard ? 1.0 : 0.5;

        QualityHighBtn.Background = quality == RecordingQuality.High ? accent : Brushes.Transparent;
        QualityHighText.Foreground = quality == RecordingQuality.High ? Brushes.White : dimFg;
        QualityHighText.Opacity = quality == RecordingQuality.High ? 1.0 : 0.5;

        QualityOriginalBtn.Background = quality == RecordingQuality.Original ? accent : Brushes.Transparent;
        QualityOriginalText.Foreground = quality == RecordingQuality.Original ? Brushes.White : dimFg;
        QualityOriginalText.Opacity = quality == RecordingQuality.Original ? 1.0 : 0.5;
    }

    private void PositionModeTogglePanel()
    {
        if (ModeTogglePanel is null) return;
        var canvasWidth = RootCanvas.ActualWidth > 0 ? RootCanvas.ActualWidth : Width;
        var toggleSize = MeasureElement(ModeTogglePanel);
        Canvas.SetLeft(ModeTogglePanel, (canvasWidth - toggleSize.Width) / 2);
        Canvas.SetTop(ModeTogglePanel, InfoBubbleMargin);
    }
}