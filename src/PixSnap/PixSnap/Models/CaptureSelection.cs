using System;
using System.Windows;

namespace PixSnap.Models;

public enum CaptureSelectionMode
{
    FullScreen,
    Window,
    Region
}

public sealed class CaptureSelection
{
    public required CaptureSelectionMode Mode { get; init; }
    public int ScreenIndex { get; init; } = -1;
    public IntPtr WindowHandle { get; init; }
    public string WindowTitle { get; init; } = string.Empty;
    public Rect Region { get; init; }
}