using System.Runtime.InteropServices;
using System.Windows;

namespace PixSnap.Services;
/// <summary>
/// Win32 窗口互操作辅助：窗口枚举与坐标查询。
/// </summary>
internal static class NativeWindowHelper
{
    // ── P/Invoke ──────────────────────────────────────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder lpClassName, int nMaxCount);

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

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NATIVEPOINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    private const uint GW_HWNDNEXT = 2;
    private const uint GA_ROOT = 2;

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
        // 优先使用系统命中测试，直接获取当前屏幕坐标下的窗口链路。
        var nativePoint = new NATIVEPOINT
        {
            x = (int)Math.Round(screenPoint.X),
            y = (int)Math.Round(screenPoint.Y)
        };

        var pointedWindow = WindowFromPoint(nativePoint);
        if (pointedWindow != IntPtr.Zero)
        {
            var rootWindow = GetAncestor(pointedWindow, GA_ROOT);
            if (rootWindow != IntPtr.Zero
                && rootWindow != excludeHwnd
                && IsCapturableTopLevelWindow(rootWindow))
            {
                return rootWindow;
            }
        }

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
