using PixSnap.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PixSnap.Services;

public interface IScreenCaptureService
{
    Task<BitmapSource> CaptureFullScreenAsync(int screenIndex);
    Task<BitmapSource> CaptureWindowAsync(IntPtr hwnd, bool includeBorder = false);
    Task<BitmapSource> CaptureRegionAsync(Rect region);
    List<ScreenInfo> GetScreens();
    List<WindowInfo> GetWindows();
}