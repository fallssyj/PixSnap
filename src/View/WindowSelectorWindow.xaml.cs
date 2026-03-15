// 编码：UTF-8 BOM
// 窗口截图选择器：鼠标悬停自动高亮目标窗口，单击捕获

using PixSnap.Helpers;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace PixSnap.View;

/// <summary>
/// 鼠标悬停式窗口截图选择器。
/// 鼠标移动时自动高亮鼠标下方的窗口，单击确认截图，右键/Esc 取消。
/// </summary>
public partial class WindowSelectorWindow : Window
{
    /// <summary>用户选中了某个窗口 HWND</summary>
    public event Action<IntPtr>? WindowSelected;
    /// <summary>用户取消</summary>
    public event Action? Cancelled;

    private IntPtr _ownHwnd    = IntPtr.Zero;  // 本覆盖窗口句柄
    private IntPtr _hoveredHwnd = IntPtr.Zero; // 当前高亮的窗口

    public WindowSelectorWindow()
    {
        InitializeComponent();
        // 全屏覆盖所有显示器
        Left   = SystemParameters.VirtualScreenLeft;
        Top    = SystemParameters.VirtualScreenTop;
        Width  = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _ownHwnd = new WindowInteropHelper(this).Handle;
    }

    // ===== 鼠标移动：检测并高亮悬停窗口 =====

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        DetectAndHighlight();
    }

    private void DetectAndHighlight()
    {
        if (_ownHwnd == IntPtr.Zero) return;

        // 临时添加 WS_EX_TRANSPARENT，让 WindowFromPoint 能看到我们下面的窗口
        int origStyle = GetWindowLong(_ownHwnd, GWL_EXSTYLE);
        SetWindowLong(_ownHwnd, GWL_EXSTYLE, origStyle | WS_EX_TRANSPARENT);

        GetCursorPos(out var pt);
        var hwnd = WindowFromPoint(pt);

        SetWindowLong(_ownHwnd, GWL_EXSTYLE, origStyle); // 立即恢复

        if (hwnd == IntPtr.Zero || hwnd == _ownHwnd) return;

        // 找到根级顶层窗口（避免选中子控件）
        var root = GetAncestor(hwnd, GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        if (root == _ownHwnd) return;

        if (root == _hoveredHwnd) return; // 没有切换窗口，无需重绘

        _hoveredHwnd = root;
        UpdateHighlight(root);
    }

    private void UpdateHighlight(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var physRect))
        {
            HideHighlight();
            return;
        }

        var dpi = DpiHelper.GetWindowDpiScale(this);

        // 物理像素 → WPF 逻辑单位（相对于本覆盖窗口的 Left/Top）
        double wL = physRect.Left   / dpi - Left;
        double wT = physRect.Top    / dpi - Top;
        double wW = Math.Max(0, physRect.Width  / dpi);
        double wH = Math.Max(0, physRect.Height / dpi);

        // 高亮框定位
        System.Windows.Controls.Canvas.SetLeft(HighlightBorder, wL);
        System.Windows.Controls.Canvas.SetTop(HighlightBorder,  wT);
        HighlightBorder.Width      = wW;
        HighlightBorder.Height     = wH;
        HighlightBorder.Visibility = Visibility.Visible;

        // 获取窗口标题
        var sb = new StringBuilder(256);
        NativeMethods.GetWindowText(hwnd, sb, 256);
        TbWindowTitle.Text = sb.Length > 0 ? sb.ToString() : "(无标题)";

        // 标题标签贴高亮框顶部
        double labelTop = wT - 30;
        if (labelTop < 4) labelTop = wT + 4;
        System.Windows.Controls.Canvas.SetLeft(TitleLabel, wL + 4);
        System.Windows.Controls.Canvas.SetTop(TitleLabel,  labelTop);
        TitleLabel.Visibility = Visibility.Visible;
    }

    private void HideHighlight()
    {
        HighlightBorder.Visibility = Visibility.Collapsed;
        TitleLabel.Visibility      = Visibility.Collapsed;
    }

    // ===== 左键单击：截图选中窗口 =====

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_hoveredHwnd != IntPtr.Zero)
        {
            WindowSelected?.Invoke(_hoveredHwnd);
            Close();
        }
    }

    // ===== 右键：取消 =====

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Cancelled?.Invoke();
        Close();
    }

    // ===== Esc：取消 =====

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Cancelled?.Invoke();
            Close();
        }
    }

    // ===== Win32 API =====

    private const int  GWL_EXSTYLE      = -20;
    private const int  WS_EX_TRANSPARENT = 0x20;
    private const uint GA_ROOT           = 2;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativeMethods.POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativeMethods.POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);
}
