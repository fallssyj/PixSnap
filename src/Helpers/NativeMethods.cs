// 编码：UTF-8 BOM
// Win32 原生 API 封装，用于全局快捷键注册、窗口枚举等

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace PixSnap.Helpers;

/// <summary>Win32 API 及常量封装</summary>
internal static class NativeMethods
{
    // ===== 热键相关 =====

    /// <summary>注册全局热键</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    /// <summary>注销全局热键</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>热键消息 ID</summary>
    public const int WM_HOTKEY = 0x0312;

    /// <summary>修饰键：Alt</summary>
    public const uint MOD_ALT = 0x0001;
    /// <summary>修饰键：Ctrl</summary>
    public const uint MOD_CONTROL = 0x0002;
    /// <summary>修饰键：Shift</summary>
    public const uint MOD_SHIFT = 0x0004;
    /// <summary>修饰键：Win</summary>
    public const uint MOD_WIN = 0x0008;
    /// <summary>不重复触发</summary>
    public const uint MOD_NOREPEAT = 0x4000;

    // ===== DPI 相关 =====

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    // ===== 窗口枚举 =====

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // ===== 结构体 =====

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public Rect ToRect() => new Rect(Left, Top, Width, Height);
    }
}

/// <summary>DPI 工具方法</summary>
internal static class DpiHelper
{
    /// <summary>获取指定屏幕坐标处的 DPI 缩放比例</summary>
    public static double GetDpiScaleAt(System.Windows.Point screenPoint)
    {
        var pt = new NativeMethods.POINT { X = (int)screenPoint.X, Y = (int)screenPoint.Y };
        var monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            NativeMethods.GetDpiForMonitor(monitor, 0, out uint dpiX, out _);
            return dpiX / 96.0;
        }
        return 1.0;
    }

    /// <summary>获取 WPF 窗口的 DPI 缩放比例</summary>
    public static double GetWindowDpiScale(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }
}
