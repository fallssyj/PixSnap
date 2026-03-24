using NativeScreenCapturer;
using PixSnap.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;

using PixWindowInfo = PixSnap.Models.WindowInfo;

namespace PixSnap.Services;

/// <summary>
/// 屏幕截图服务实现：通过 NativeScreenCapturer （C++/CLI）调用 Windows Graphics Capture API。
/// 所有截图操作均在 UI 线程上执行，以确保 BitmapSource 可直接用于 WPF 绑定。
/// </summary>
public sealed class ScreenCaptureService : IScreenCaptureService, IDisposable
{
    private readonly ScreenCapturer _nativeCapturer = new();

    public Task<BitmapSource> CaptureFullScreenAsync(int screenIndex)
    {
        Log.Information("截取全屏: 显示器 {Index}", screenIndex);
        return InvokeOnUiThreadAsync(() => _nativeCapturer.CaptureFullScreen(screenIndex));
    }

    public Task<BitmapSource> CaptureWindowAsync(IntPtr hwnd, bool includeBorder = false)
    {
        Log.Information("截取窗口: HWND={Hwnd:X8}, 包含边框={IncludeBorder}", hwnd, includeBorder);
        return InvokeOnUiThreadAsync(() => _nativeCapturer.CaptureWindow(hwnd, includeBorder));
    }

    public Task<BitmapSource> CaptureRegionAsync(Rect region)
    {
        Log.Information("截取区域: ({X},{Y}) {W}×{H}", (int)region.X, (int)region.Y, (int)region.Width, (int)region.Height);
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
        Log.Debug("枚举显示器: {Count} 个", count);

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
        Log.Debug("ScreenCaptureService 已释放");
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