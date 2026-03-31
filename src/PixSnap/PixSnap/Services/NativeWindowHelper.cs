using Serilog;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace PixSnap.Services;

/// <summary>
/// Win32 窗口互操作辅助：提供无边框窗口的边缘缩放命中测试与最大化工作区约束。
/// </summary>
internal static class NativeWindowHelper
{
    // ── Win32 消息常量 ────────────────────────────────────────────────────
    public const int WM_NCHITTEST     = 0x0084;
    public const int WM_GETMINMAXINFO = 0x0024;

    private const int HTCLIENT      = 1;
    private const int HTLEFT        = 10;
    private const int HTRIGHT       = 11;
    private const int HTTOP         = 12;
    private const int HTTOPLEFT     = 13;
    private const int HTTOPRIGHT    = 14;
    private const int HTBOTTOM      = 15;
    private const int HTBOTTOMLEFT  = 16;
    private const int HTBOTTOMRIGHT = 17;

    // ── P/Invoke ──────────────────────────────────────────────────────────

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ── 公共方法 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 根据鼠标屏幕坐标（lParam）判断命中的缩放边/角区域。
    /// 返回 HT* 常量，HTCLIENT 表示非缩放区域。
    /// </summary>
    public static int GetResizeHitTest(Window window, IntPtr lParam, int borderThickness)
    {
        int screenX = unchecked((short)(lParam.ToInt32() & 0xFFFF));
        int screenY = unchecked((short)((lParam.ToInt32() >> 16) & 0xFFFF));
        Point pt = window.PointFromScreen(new Point(screenX, screenY));

        int x = (int)pt.X;
        int y = (int)pt.Y;
        int w = (int)window.ActualWidth;
        int h = (int)window.ActualHeight;
        int b = borderThickness;

        bool onLeft   = x < b;
        bool onRight  = x > w - b;
        bool onTop    = y < b;
        bool onBottom = y > h - b;

        if (onTop && onLeft)     return HTTOPLEFT;
        if (onTop && onRight)    return HTTOPRIGHT;
        if (onBottom && onLeft)  return HTBOTTOMLEFT;
        if (onBottom && onRight) return HTBOTTOMRIGHT;
        if (onTop)    return HTTOP;
        if (onBottom) return HTBOTTOM;
        if (onLeft)   return HTLEFT;
        if (onRight)  return HTRIGHT;

        return HTCLIENT;
    }

    /// <summary>命中值是否为客户区（即非缩放边角）。</summary>
    public static bool IsClientHit(int hitTest) => hitTest == HTCLIENT;

    /// <summary>
    /// 处理 WM_GETMINMAXINFO：将最大化尺寸约束为工作区（排除任务栏），
    /// 并按 DPI 缩放设置最小跟踪尺寸。
    /// </summary>
    public static void HandleGetMinMaxInfo(Window window, IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        IntPtr monitor = MonitorFromWindow(hwnd, 0x00000002 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitor, ref mi);
            var wa = mi.rcWork;
            mmi.ptMaxPosition.x = wa.left;
            mmi.ptMaxPosition.y = wa.top;
            mmi.ptMaxSize.x     = wa.right  - wa.left;
            mmi.ptMaxSize.y     = wa.bottom - wa.top;
        }
        try
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            int minW = (int)Math.Round(window.MinWidth * dpi.DpiScaleX);
            int minH = (int)Math.Round(window.MinHeight * dpi.DpiScaleY);
            if (minW > 0) mmi.ptMinTrackSize.x = minW;
            if (minH > 0) mmi.ptMinTrackSize.y = minH;
        }
        catch (Exception ex) { Log.Warning(ex, "HandleGetMinMaxInfo DPI 获取失败，使用默认值"); }
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    // ── 窗口枚举 / 坐标辅助 ──────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct NATIVERECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NATIVEPOINT { public int x, y; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRectNative(IntPtr hwnd, out NATIVERECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NATIVEPOINT point);

    private const uint GW_HWNDNEXT = 2;

    /// <summary>获取指定窗口的屏幕矩形区域。</summary>
    public static bool TryGetWindowRect(IntPtr hwnd, out Rect rect)
    {
        rect = Rect.Empty;
        if (!GetWindowRectNative(hwnd, out var native))
            return false;

        rect = new Rect(
            native.left,
            native.top,
            native.right - native.left,
            native.bottom - native.top);
        return rect.Width > 0 && rect.Height > 0;
    }

    /// <summary>获取当前光标的屏幕坐标。</summary>
    public static Point GetCursorPosition()
    {
        GetCursorPos(out var pt);
        return new Point(pt.x, pt.y);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    private const int DWMWA_CLOAKED = 14;

    /// <summary>从顶层窗口开始枚举 Z 序，返回包含指定屏幕坐标点的第一个可见窗口句柄（排除指定句柄）。</summary>
    public static IntPtr FindTopWindowAtPoint(Point screenPoint, IntPtr excludeHwnd)
    {
        var hwnd = GetTopWindow(IntPtr.Zero);
        while (hwnd != IntPtr.Zero)
        {
            if (hwnd != excludeHwnd
                && IsCapturableTopLevelWindow(hwnd)
                && TryGetWindowRect(hwnd, out var rect)
                && rect.Contains(screenPoint))
            {
                return hwnd;
            }
            hwnd = GetWindow(hwnd, GW_HWNDNEXT);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 按 Z 序从前到后快照所有可见窗口的矩形（包括无标题的弹窗：下拉菜单、工具提示等）。
    /// 返回列表保持 Z 序（索引 0 = 最前层），用于在窗口已隐藏后仍能按位置匹配。
    /// </summary>
    public static List<(IntPtr Hwnd, Rect Rect, string Title, string ClassName)> SnapshotWindowRects(IntPtr excludeHwnd = default)
    {
        var result = new List<(IntPtr, Rect, string, string)>();
        var hwnd = GetTopWindow(IntPtr.Zero);
        while (hwnd != IntPtr.Zero)
        {
            if (hwnd != excludeHwnd
                && IsVisibleTopLevelWindow(hwnd)
                && TryGetWindowRect(hwnd, out var rect))
            {
                var titleBuf = new System.Text.StringBuilder(256);
                GetWindowTextW(hwnd, titleBuf, titleBuf.Capacity);
                var classBuf = new System.Text.StringBuilder(256);
                GetClassNameW(hwnd, classBuf, classBuf.Capacity);
                result.Add((hwnd, rect, titleBuf.ToString(), classBuf.ToString()));
            }
            hwnd = GetWindow(hwnd, GW_HWNDNEXT);
        }
        return result;
    }

    /// <summary>宽松过滤：仅要求可见、非最小化、非 Cloaked，不要求有标题（以包含弹窗/下拉菜单/工具提示）。</summary>
    private static bool IsVisibleTopLevelWindow(IntPtr hwnd)
    {
        return IsWindowVisible(hwnd) && !IsIconic(hwnd) && !IsWindowCloaked(hwnd);
    }

    private static bool IsCapturableTopLevelWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsWindowCloaked(hwnd))
        {
            return false;
        }

        var titleLength = GetWindowTextLengthW(hwnd);
        if (titleLength <= 0)
        {
            return false;
        }

        var title = new System.Text.StringBuilder(titleLength + 1);
        _ = GetWindowTextW(hwnd, title, title.Capacity);
        return title.Length > 0;
    }

    private static bool IsWindowCloaked(IntPtr hwnd)
    {
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
    }
}
