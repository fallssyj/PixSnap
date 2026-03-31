using System.Windows;

namespace PixSnap.Models;

public enum CaptureSelectionMode
{
    FullScreen,
    Window,
    Region
}

public enum RecordingQuality
{
    /// <summary>标清 1080p ~4 Mbps</summary>
    Standard,
    /// <summary>高清 1080p ~12 Mbps</summary>
    High,
    /// <summary>原画 1080p ~24 Mbps</summary>
    Original
}

public sealed class CaptureSelection
{
    public required CaptureSelectionMode Mode { get; init; }
    public int ScreenIndex { get; init; } = -1;
    public IntPtr WindowHandle { get; init; }
    public string WindowTitle { get; init; } = string.Empty;
    public Rect Region { get; init; }

    /// <summary>窗口在屏幕物理像素坐标中的矩形（仅 Window 模式，用于从预截图裁剪）。</summary>
    public Rect WindowRect { get; init; }
    public bool IsRecording { get; init; }
    public bool EnableMicrophone { get; init; }
    public bool EnableSystemAudio { get; init; }
    public RecordingQuality Quality { get; init; } = RecordingQuality.High;

    /// <summary>录制区域的像素数（宽×高），用于动态计算码率。</summary>
    public long CapturePixelCount { get; init; }

    /// <summary>
    /// 根据画质等级和实际分辨率动态计算码率。
    /// 以 1080p (2_073_600 px) 为基准，按像素数线性缩放。
    /// </summary>
    public int VideoBitrate
    {
        get
        {
            const long basePixels = 1920L * 1080; // 1080p 基准
            int baseBitrate = Quality switch
            {
                RecordingQuality.Standard => 4_000_000,
                RecordingQuality.High => 12_000_000,
                RecordingQuality.Original => 24_000_000,
                _ => 12_000_000
            };

            if (CapturePixelCount <= 0 || CapturePixelCount <= basePixels)
                return baseBitrate;

            // 像素数超过 1080p 时按比例线性缩放
            return (int)Math.Min(
                (long)baseBitrate * CapturePixelCount / basePixels,
                int.MaxValue);
        }
    }
}