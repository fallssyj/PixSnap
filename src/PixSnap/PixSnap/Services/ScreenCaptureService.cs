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
/// 屏幕截图与录屏服务实现：通过 NativeScreenCapturer （C++/CLI）调用 Windows Graphics Capture API。
/// 所有截图操作均在 UI 线程上执行，以确保 BitmapSource 可直接用于 WPF 绑定。
/// </summary>
public sealed class ScreenCaptureService : IScreenCaptureService, IDisposable
{
    private readonly ScreenCapturer _nativeCapturer = new();

    public bool IsRecording => _nativeCapturer.IsRecording;

    public bool AudioInitFailed => _nativeCapturer.AudioInitFailed;

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
                DisplayName = string.Format("显示器 {0}  ({1} x {2})", index + 1, bounds.Width, bounds.Height),
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
        if (IsRecording)
            StopRecording();
        Log.Debug("ScreenCaptureService 已释放");
        _nativeCapturer.Dispose();
    }

    public void StartRecordingFullScreen(int screenIndex, string outputPath, bool enableMicrophone = false, bool enableSystemAudio = false, int videoBitrate = 8000000)
    {
        Log.Information("开始录制全屏: 显示器 {Index}, 输出 {Path}, 麦克风={Mic}, 系统声音={Sys}, 码率={Bitrate}", screenIndex, outputPath, enableMicrophone, enableSystemAudio, videoBitrate);
        _nativeCapturer.StartRecordingMonitor(screenIndex, outputPath, enableMicrophone, enableSystemAudio, videoBitrate);
        Log.Information("录制全屏已启动");
    }

    public void StartRecordingWindow(IntPtr hwnd, string outputPath, bool enableMicrophone = false, bool enableSystemAudio = false, int videoBitrate = 8000000)
    {
        Log.Information("开始录制窗口: HWND={Hwnd:X8}, 输出 {Path}, 麦克风={Mic}, 系统声音={Sys}, 码率={Bitrate}", hwnd, outputPath, enableMicrophone, enableSystemAudio, videoBitrate);
        _nativeCapturer.StartRecordingWindow(hwnd, outputPath, enableMicrophone, enableSystemAudio, videoBitrate);
        Log.Information("录制窗口已启动");
    }

    public void StartRecordingRegion(Rect region, string outputPath, bool enableMicrophone = false, bool enableSystemAudio = false, int videoBitrate = 8000000)
    {
        Log.Information("开始录制区域: ({X},{Y}) {W}×{H}, 输出 {Path}, 麦克风={Mic}, 系统声音={Sys}, 码率={Bitrate}",
            (int)region.X, (int)region.Y, (int)region.Width, (int)region.Height, outputPath, enableMicrophone, enableSystemAudio, videoBitrate);
        _nativeCapturer.StartRecordingRegion(
            (int)Math.Round(region.X),
            (int)Math.Round(region.Y),
            (int)Math.Round(region.Width),
            (int)Math.Round(region.Height),
            outputPath,
            enableMicrophone,
            enableSystemAudio,
            videoBitrate);
        Log.Information("录制区域已启动");
    }

    public void PauseRecording()
    {
        _nativeCapturer.PauseRecording();
    }

    public void ResumeRecording()
    {
        _nativeCapturer.ResumeRecording();
    }

    public void StopRecording()
    {
        Log.Information("停止录制");
        _nativeCapturer.StopRecording();
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