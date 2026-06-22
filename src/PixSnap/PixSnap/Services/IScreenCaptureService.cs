using PixSnap.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PixSnap.Services;

/// <summary>
/// 屏幕截图与录屏服务接口：提供全屏、窗口、区域截图/录制以及显示器/窗口枚举能力。
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

    /// <summary>开始录制指定显示器。</summary>
    void StartRecordingFullScreen(int screenIndex, string outputPath, bool enableMicrophone = false, bool enableSystemAudio = false, int videoBitrate = 8000000);
    /// <summary>开始录制指定窗口。</summary>
    void StartRecordingWindow(IntPtr hwnd, string outputPath, bool enableMicrophone = false, bool enableSystemAudio = false, int videoBitrate = 8000000);
    /// <summary>开始录制屏幕上指定矩形区域。</summary>
    void StartRecordingRegion(Rect region, string outputPath, bool enableMicrophone = false, bool enableSystemAudio = false, int videoBitrate = 8000000);
    /// <summary>暂停当前录制。</summary>
    void PauseRecording();
    /// <summary>恢复录制。</summary>
    void ResumeRecording();
    /// <summary>停止当前录制并完成文件写入。</summary>
    void StopRecording();
    /// <summary>在后台线程停止录制并完成文件写入，避免阻塞 UI。</summary>
    Task StopRecordingAsync();
    /// <summary>当前是否正在录制。</summary>
    bool IsRecording { get; }
    /// <summary>音频设备初始化是否失败（录制启动后查询）。</summary>
    bool AudioInitFailed { get; }
}