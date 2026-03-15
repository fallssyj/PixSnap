// 编码：UTF-8 BOM
// 矩形区域选择器窗口后台代码
// 全屏覆盖，鼠标拖拽选择截图区域

using PixSnap.Helpers;
using PixSnap.Services;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
// 消除命名空间冲突
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace PixSnap.View;

/// <summary>矩形截图区域选择窗口</summary>
public partial class RegionSelectorWindow : Window
{
    // ===== 事件 =====

    /// <summary>用户完成选区事件（物理像素坐标）</summary>
    public event Action<int, int, int, int>? RegionSelected;

    /// <summary>用户取消事件</summary>
    public event Action? Cancelled;

    // ===== 状态 =====

    private bool _isDragging = false;
    private Point _startPoint;
    private Point _currentPoint;
    private bool _hasSelection = false;

    /// <summary>DPI 缩放（WPF 逻辑坐标 → 物理像素）</summary>
    private double _dpiScale = 1.0;

    /// <summary>虚拟屏幕左上角（多显示器）</summary>
    private double _virtualLeft;
    private double _virtualTop;

    public RegionSelectorWindow()
    {
        InitializeComponent();

        // 覆盖整个虚拟屏幕（多显示器支持）
        var virtualScreen = GetVirtualScreenBounds();
        Left = virtualScreen.Left;
        Top = virtualScreen.Top;
        Width = virtualScreen.Width;
        Height = virtualScreen.Height;

        _virtualLeft = virtualScreen.Left;
        _virtualTop = virtualScreen.Top;

        Loaded += OnLoaded;
        KeyDown += OnKeyDown;

        // 隐藏选区和提示
        HideSelectionUI();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 保留 _dpiScale，供尺寸提示使用
        _dpiScale = DpiHelper.GetWindowDpiScale(this);

        // 使用 PointToScreen 直接获取第一/最后一个像素的物理坐标
        // 这是最可靠的 DPI 处理方式，自动支持多显示器不同 DPI
        var physTL = RootCanvas.PointToScreen(new Point(0, 0));
        var physBR = RootCanvas.PointToScreen(new Point(RootCanvas.ActualWidth, RootCanvas.ActualHeight));

        int px = (int)physTL.X;
        int py = (int)physTL.Y;
        int pw = (int)(physBR.X - physTL.X);
        int ph = (int)(physBR.Y - physTL.Y);

        try
        {
            var screenshot = new CaptureService().CaptureRegion(px, py, pw, ph);
            PreviewImage.Source = screenshot;
            PreviewImage.Width = RootCanvas.ActualWidth;
            PreviewImage.Height = RootCanvas.ActualHeight;
        }
        catch { /* 背景截图失败不影响选区操作 */ }
    }

    // ===== 鼠标事件 =====

    private void RootCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _startPoint = e.GetPosition(RootCanvas);
        _currentPoint = _startPoint;

        RootCanvas.CaptureMouse();
        _hasSelection = false;
        UpdateSelectionUI();
    }

    private void RootCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            _currentPoint = e.GetPosition(RootCanvas);
            _hasSelection = GetSelectionRect().Width > 4 && GetSelectionRect().Height > 4;
            UpdateSelectionUI();
        }
    }

    private void RootCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        _currentPoint = e.GetPosition(RootCanvas);
        RootCanvas.ReleaseMouseCapture();

        _hasSelection = GetSelectionRect().Width > 4 && GetSelectionRect().Height > 4;
        if (_hasSelection)
        {
            UpdateSelectionUI();
            ConfirmBar.Visibility = Visibility.Visible;
            PositionConfirmBar();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _hasSelection)
        {
            ConfirmCapture();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelCapture();
            e.Handled = true;
        }
    }

    private void BtnConfirm_Click(object sender, RoutedEventArgs e) => ConfirmCapture();
    private void BtnCancel_Click(object sender, RoutedEventArgs e) => CancelCapture();

    // ===== UI 更新 =====

    private void UpdateSelectionUI()
    {
        var rect = GetSelectionRect();
        if (rect.Width < 1 || rect.Height < 1)
        {
            HideSelectionUI();
            return;
        }

        double cw = RootCanvas.ActualWidth;
        double ch = RootCanvas.ActualHeight;

        // 更新四块遮罩
        SetMask(MaskTop,    0, 0, cw, rect.Top);
        SetMask(MaskBottom, 0, rect.Bottom, cw, ch - rect.Bottom);
        SetMask(MaskLeft,   0, rect.Top, rect.Left, rect.Height);
        SetMask(MaskRight,  rect.Right, rect.Top, cw - rect.Right, rect.Height);

        // 更新选区边框
        Canvas.SetLeft(SelectionBorder, rect.Left);
        Canvas.SetTop(SelectionBorder, rect.Top);
        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;
        SelectionBorder.Visibility = Visibility.Visible;

        // 更新手柄
        PositionHandle(HandleTL, rect.Left - 4, rect.Top - 4);
        PositionHandle(HandleTR, rect.Right - 4, rect.Top - 4);
        PositionHandle(HandleBL, rect.Left - 4, rect.Bottom - 4);
        PositionHandle(HandleBR, rect.Right - 4, rect.Bottom - 4);
        PositionHandle(HandleTC, rect.Left + rect.Width / 2 - 4, rect.Top - 4);
        PositionHandle(HandleBC, rect.Left + rect.Width / 2 - 4, rect.Bottom - 4);
        PositionHandle(HandleLC, rect.Left - 4, rect.Top + rect.Height / 2 - 4);
        PositionHandle(HandleRC, rect.Right - 4, rect.Top + rect.Height / 2 - 4);
        ShowHandles(true);

        // 更新尺寸提示：用 PointToScreen 计算真实物理像素数
        var physSel = RootCanvas.PointToScreen(new Point(rect.Left, rect.Top));
        var physSelBR = RootCanvas.PointToScreen(new Point(rect.Right, rect.Bottom));
        int dispW = Math.Max(0, (int)(physSelBR.X - physSel.X));
        int dispH = Math.Max(0, (int)(physSelBR.Y - physSel.Y));
        SizeTipText.Text = $"{dispW} × {dispH}";
        Canvas.SetLeft(SizeTip, rect.Left);
        Canvas.SetTop(SizeTip, rect.Top - 24);
        SizeTip.Visibility = Visibility.Visible;
    }

    private void HideSelectionUI()
    {
        // 遮罩全屏
        SetMask(MaskTop, 0, 0, ActualWidth, ActualHeight);
        SetMask(MaskBottom, 0, 0, 0, 0);
        SetMask(MaskLeft, 0, 0, 0, 0);
        SetMask(MaskRight, 0, 0, 0, 0);

        SelectionBorder.Visibility = Visibility.Collapsed;
        SizeTip.Visibility = Visibility.Collapsed;
        ConfirmBar.Visibility = Visibility.Collapsed;
        ShowHandles(false);
    }

    private static void SetMask(System.Windows.Shapes.Rectangle mask, double left, double top, double width, double height)
    {
        Canvas.SetLeft(mask, left);
        Canvas.SetTop(mask, top);
        mask.Width = Math.Max(0, width);
        mask.Height = Math.Max(0, height);
    }

    private static void PositionHandle(System.Windows.Shapes.Rectangle handle, double left, double top)
    {
        Canvas.SetLeft(handle, left);
        Canvas.SetTop(handle, top);
        handle.Visibility = Visibility.Visible;
    }

    private void ShowHandles(bool show)
    {
        var vis = show ? Visibility.Visible : Visibility.Collapsed;
        HandleTL.Visibility = HandleTR.Visibility = HandleBL.Visibility = HandleBR.Visibility =
        HandleTC.Visibility = HandleBC.Visibility = HandleLC.Visibility = HandleRC.Visibility = vis;
    }

    private void PositionConfirmBar()
    {
        var rect = GetSelectionRect();
        double barLeft = rect.Right - 80;
        double barTop = rect.Bottom + 8;

        // 防止超出屏幕
        if (barTop + 44 > ActualHeight) barTop = rect.Top - 52;
        if (barLeft < 0) barLeft = rect.Left;

        Canvas.SetLeft(ConfirmBar, barLeft);
        Canvas.SetTop(ConfirmBar, barTop);
    }

    // ===== 截图操作 =====

    private async void ConfirmCapture()
    {
        if (!_hasSelection) return;

        var rect = GetSelectionRect();

        // 先记录物理坐标（窗口关闭后 PointToScreen 不可用）
        var physTL = RootCanvas.PointToScreen(new Point(rect.Left, rect.Top));
        var physBR = RootCanvas.PointToScreen(new Point(rect.Right, rect.Bottom));

        int px = (int)physTL.X;
        int py = (int)physTL.Y;
        int pw = (int)(physBR.X - physTL.X);
        int ph = (int)(physBR.Y - physTL.Y);

        if (pw <= 0 || ph <= 0) { Close(); return; }

        // 先隐藏覆盖层，等待系统完成重绘，再截图，避免把选框截入画面
        Hide();
        await Task.Delay(80);

        RegionSelected?.Invoke(px, py, pw, ph);
        Close();
    }

    private void CancelCapture()
    {
        Cancelled?.Invoke();
        Close();
    }

    // ===== 辅助方法 =====

    /// <summary>获取当前选区（WPF 逻辑坐标，已规范化为正向矩形）</summary>
    private Rect GetSelectionRect()
    {
        double x = Math.Min(_startPoint.X, _currentPoint.X);
        double y = Math.Min(_startPoint.Y, _currentPoint.Y);
        double w = Math.Abs(_currentPoint.X - _startPoint.X);
        double h = Math.Abs(_currentPoint.Y - _startPoint.Y);
        return new Rect(x, y, w, h);
    }

    /// <summary>获取虚拟屏幕边界（多显示器支持）</summary>
    private static Rect GetVirtualScreenBounds()
    {
        return new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
    }
}
