using System;
using System.Windows.Media.Imaging;

namespace PixSnap.Models;

public sealed class WindowInfo
{
    public required string Title { get; init; }
    public required IntPtr Hwnd { get; init; }
    public required string ClassName { get; init; }
    public BitmapSource? Icon { get; init; }

    public override string ToString() => string.IsNullOrWhiteSpace(Title) ? ClassName : Title;
}