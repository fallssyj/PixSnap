using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
using System.Windows.Media.Imaging;

namespace PixSnap.ViewModels;

/// <summary>
/// 圆角面板的 ViewModel：管理圆角半径，并执行 SkiaSharp 圆角裁切。
/// </summary>
public partial class RoundCornerViewModel : ObservableObject
{
    // ── 圆角半径（像素） ────────────────────────────────────────────────────

    [ObservableProperty]
    private int _cornerRadius = 20;

    /// <summary>
    /// 通知 View 应用圆角后的新图。
    /// </summary>
    public event Action<BitmapSource>? RoundCornerApplied;

    /// <summary>
    /// 通知 View 关闭圆角模式。
    /// </summary>
    public event Action? RoundCornerCancelled;

    // ── 命令 ─────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Apply(BitmapSource source)
    {
        var result = ApplyRoundCornerWithSkia(source, CornerRadius);
        RoundCornerApplied?.Invoke(result);
    }

    [RelayCommand]
    private void Cancel()
    {
        RoundCornerCancelled?.Invoke();
    }

    // ── SkiaSharp 圆角处理 ───────────────────────────────────────────────────

    private static BitmapSource ApplyRoundCornerWithSkia(BitmapSource source, int radius)
    {
        var wb = new WriteableBitmap(source);
        wb.Lock();
        try
        {
            var w = wb.PixelWidth;
            var h = wb.PixelHeight;
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);

            // 创建透明背景的目标 bitmap
            using var dst = new SKBitmap(info);
            using var canvas = new SKCanvas(dst);
            canvas.Clear(SKColors.Transparent);

            // 将原图像素直接安装到 SKBitmap，不产生额外内存拷贝
            using var srcBitmap = new SKBitmap();
            srcBitmap.InstallPixels(info, wb.BackBuffer, wb.BackBufferStride);

            // 将半径钳制到图片短边的一半，避免过大的圆角
            var r = (float)Math.Clamp(radius, 0, Math.Min(w, h) / 2);
            var rect = new SKRect(0, 0, w, h);

            using var paint = new SKPaint { IsAntialias = true };
            using var roundPath = new SKPath();
            roundPath.AddRoundRect(rect, r, r);

            canvas.ClipPath(roundPath, SKClipOperation.Intersect, antialias: true);
            canvas.DrawBitmap(srcBitmap, 0, 0, paint);
            canvas.Flush();

            return ConvertToBitmapSource(dst);
        }
        finally
        {
            // 确保像素缓冲区始终被释放，即使处理过程中发生异常
            wb.Unlock();
        }
    }

    private static BitmapSource ConvertToBitmapSource(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new System.IO.MemoryStream(data.ToArray());
        return BitmapFrame.Create(
            stream,
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);
    }
}
