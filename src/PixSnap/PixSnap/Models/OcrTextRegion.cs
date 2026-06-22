using System.Windows;

namespace PixSnap.Models;

/// <summary>OCR 识别出的单行/单块文字及其在图片像素坐标系中的位置。</summary>
public sealed class OcrTextRegion
{
    public required string Text { get; init; }
    public required Rect PixelBounds { get; init; }
    public required Point[] BoxPoints { get; init; }
    public float Confidence { get; init; }
}
