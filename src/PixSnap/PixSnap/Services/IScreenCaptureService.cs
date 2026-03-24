using PixSnap.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PixSnap.Services;

/// <summary>
/// 屏幕截图服务接口：提供全屏、窗口、区域截图以及显示器/窗口枚举能力。
/// </summary>
public interface IScreenCaptureService
{
    /// <summary>截取指定显示器的全屏画面。</summary>
    Task<BitmapSource> CaptureFullScreenAsync(int screenIndex);
    /// <summary>截取指定窗口句柄的画面。</summary>
    Task<BitmapSource> CaptureWindowAsync(IntPtr hwnd, bool includeBorder = false);
    /// <summary>截取屏幕上指定矩形区域的画面。</summary>
    Task<BitmapSource> CaptureRegionAsync(Rect region);
    /// <summary>枚举所有可用显示器信息。</summary>
    List<ScreenInfo> GetScreens();
    /// <summary>枚举所有可见的顶层窗口。</summary>
    List<WindowInfo> GetWindows();
}