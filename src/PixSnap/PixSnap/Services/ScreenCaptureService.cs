using NativeScreenCapturer;
using PixSnap.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media.Imaging;

using PixWindowInfo = PixSnap.Models.WindowInfo;

namespace PixSnap.Services;

public sealed class ScreenCaptureService : IScreenCaptureService, IDisposable
{
    private readonly ScreenCapturer _nativeCapturer = new();

    public Task<BitmapSource> CaptureFullScreenAsync(int screenIndex)
    {
        return InvokeOnUiThreadAsync(() => _nativeCapturer.CaptureFullScreen(screenIndex));
    }

    public Task<BitmapSource> CaptureWindowAsync(IntPtr hwnd, bool includeBorder = false)
    {
        return InvokeOnUiThreadAsync(() => _nativeCapturer.CaptureWindow(hwnd, includeBorder));
    }

    public Task<BitmapSource> CaptureRegionAsync(Rect region)
    {
        return InvokeOnUiThreadAsync(() => _nativeCapturer.CaptureRegion(
            (int)Math.Round(region.X),
            (int)Math.Round(region.Y),
            (int)Math.Round(region.Width),
            (int)Math.Round(region.Height)));
    }

    public List<ScreenInfo> GetScreens()
    {
        var screens = new List<ScreenInfo>();
        var count = _nativeCapturer.GetScreenCount();

        for (var index = 0; index < count; index++)
        {
            var bounds = _nativeCapturer.GetScreenBounds(index);
            screens.Add(new ScreenInfo
            {
                Index = index,
                DisplayName = $"显示器 {index + 1}  ({bounds.Width} x {bounds.Height})",
                Bounds = bounds
            });
        }

        return screens;
    }

    public List<PixWindowInfo> GetWindows()
    {
        return _nativeCapturer
            .GetOpenWindows()
            .Select(window => new PixWindowInfo
            {
                Title = window.Title ?? string.Empty,
                Hwnd = window.Hwnd,
                ClassName = window.ClassName ?? string.Empty,
                Icon = window.Icon
            })
            .OrderBy(window => window.Title)
            .ToList();
    }

    public void Dispose()
    {
        _nativeCapturer.Dispose();
    }

    private static Task<BitmapSource> InvokeOnUiThreadAsync(Func<BitmapSource> captureAction)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return Task.FromResult(captureAction());
        }

        return dispatcher.InvokeAsync(captureAction, DispatcherPriority.Send).Task;
    }
}