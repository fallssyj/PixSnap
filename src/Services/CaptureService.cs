// 编码：UTF-8 BOM
// 屏幕捕获服务，使用 Windows Graphics Capture API 捕获屏幕
// 支持全屏、矩形区域、指定窗口三种模式

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using PixSnap.Helpers;
using PixSnap.Models;

namespace PixSnap.Services;

/// <summary>屏幕捕获服务，封装 BitBlt 方式的全屏/区域截图</summary>
public class CaptureService
{
    // ===== GDI/BitBlt 相关 P/Invoke =====

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hDestDC, int x, int y, int cx, int cy,
        IntPtr hSrcDC, int x1, int y1, int dwRop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObj);

    private const int SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeMethods.RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    // ===== 虚拟屏幕信息（多显示器支持）=====

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>
    /// 捕获全屏（所有显示器组合的虚拟屏幕）
    /// </summary>
    public BitmapSource CaptureFullscreen()
    {
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        return CaptureRegion(x, y, w, h);
    }

    /// <summary>
    /// 捕获指定屏幕坐标矩形区域（像素坐标，非 WPF 逻辑坐标）
    /// </summary>
    public BitmapSource CaptureRegion(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("捕获区域宽高必须大于 0");

        IntPtr screenDC = GetDC(IntPtr.Zero);
        IntPtr memDC = CreateCompatibleDC(screenDC);
        IntPtr hBitmap = CreateCompatibleBitmap(screenDC, width, height);
        IntPtr oldObj = SelectObject(memDC, hBitmap);

        try
        {
            // 从屏幕 DC 将指定区域的内容复制到内存 DC
            BitBlt(memDC, 0, 0, width, height, screenDC, x, y, SRCCOPY);

            // 将 GDI 位图转换为 WPF BitmapSource
            var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            bitmapSource.Freeze(); // 跨线程安全
            return bitmapSource;
        }
        finally
        {
            SelectObject(memDC, oldObj);
            DeleteObject(hBitmap);
            DeleteDC(memDC);
            ReleaseDC(IntPtr.Zero, screenDC);
        }
    }

    /// <summary>
    /// 捕获指定窗口内容（使用 PrintWindow 可捕获被遮挡的窗口）
    /// </summary>
    public BitmapSource? CaptureWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return null;
        if (IsIconic(hWnd)) return null; // 最小化窗口不能捕获

        if (!GetWindowRect(hWnd, out var rect))
            return null;

        int width = rect.Width;
        int height = rect.Height;
        if (width <= 0 || height <= 0) return null;

        IntPtr screenDC = GetDC(IntPtr.Zero);
        IntPtr memDC = CreateCompatibleDC(screenDC);
        IntPtr hBitmap = CreateCompatibleBitmap(screenDC, width, height);
        IntPtr oldObj = SelectObject(memDC, hBitmap);

        try
        {
            // 使用 PrintWindow 渲染窗口（PW_RENDERFULLCONTENT=2，可捕获 DX/GL 内容）
            bool printed = PrintWindow(hWnd, memDC, 2);
            if (!printed)
            {
                // 回退到 BitBlt
                BitBlt(memDC, 0, 0, width, height, screenDC, rect.Left, rect.Top, SRCCOPY);
            }

            var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            bitmapSource.Freeze();
            return bitmapSource;
        }
        finally
        {
            SelectObject(memDC, oldObj);
            DeleteObject(hBitmap);
            DeleteDC(memDC);
            ReleaseDC(IntPtr.Zero, screenDC);
        }
    }

    /// <summary>
    /// 枚举所有可见且有标题的顶层窗口（用于窗口截图选择列表）
    /// </summary>
    public List<WindowInfo> EnumerateWindows()
    {
        var result = new List<WindowInfo>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;

            var sb = new System.Text.StringBuilder(256);
            NativeMethods.GetWindowText(hWnd, sb, 256);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            if (NativeMethods.GetWindowRect(hWnd, out var rect) && rect.Width > 50 && rect.Height > 50)
            {
                result.Add(new WindowInfo
                {
                    Handle = hWnd,
                    Title = title,
                    Bounds = rect.ToRect()
                });
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }
}

/// <summary>窗口信息</summary>
public class WindowInfo
{
    public IntPtr Handle { get; set; }
    public string Title { get; set; } = string.Empty;
    public Rect Bounds { get; set; }
}
