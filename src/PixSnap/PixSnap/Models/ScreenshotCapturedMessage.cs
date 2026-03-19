using System.Windows.Media.Imaging;

namespace PixSnap.Models;

public sealed record ScreenshotCapturedMessage(BitmapSource Screenshot, string CaptureMode);