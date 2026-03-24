using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSnap.Services;
using Serilog;
using SkiaSharp;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixSnap.ViewModels;

/// <summary>
/// 圆角面板的 ViewModel：管理圆角半径，并在后台线程执行 SkiaSharp 圆角裁切。
/// 对 4K/8K 大图全程不阻塞 UI 线程。
/// </summary>
public partial class RoundCornerViewModel : ObservableObject
{
    // ── 圆角半径（像素） ────────────────────────────────────────────────────

    [ObservableProperty]
    private int _cornerRadius = 20;

    /// <summary>圆角处理进行中时为 true，用于显示全窗口处理遮罩。</summary>
    [ObservableProperty]
    private bool _isProcessing;

    /// <summary>通知 View 应用圆角后的新图。</summary>
    public event Action<BitmapSource>? RoundCornerApplied;

    /// <summary>通知 View 关闭圆角模式。</summary>
    public event Action? RoundCornerCancelled;

    // ── 命令 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 异步应用圆角。
    /// UI 线程完成格式转换和像素拷贝（内存操作），SkiaSharp 绘制和 PNG 编码在后台线程执行。
    /// </summary>
    [RelayCommand]
    private async Task ApplyAsync(BitmapSource source)
    {
        Log.Information("应用圆角: 半径={Radius}px", CornerRadius);
        IsProcessing = true;
        try
        {
            if (!source.IsFrozen) source.Freeze();

            // UI 线程：FormatConvertedBitmap + CopyPixels（纯内存拷贝，不阻塞渲染）
            // 不使用 WriteableBitmap.BackBuffer，避免 SkiaSharp 无法识别外部指针的问题
            var pbgra = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            int w = pbgra.PixelWidth, h = pbgra.PixelHeight;
            int stride = w * 4;
            var pixels = new byte[stride * h];
            pbgra.CopyPixels(pixels, stride, 0);

            int radius = CornerRadius;

            // 后台线程：SkiaSharp 圆角绘制 + 直接像素拷贝，对大图不阻塞 UI
            var result = await Task.Run(() => ApplyRoundCornerOnBackground(pixels, w, h, stride, radius));

            RoundCornerApplied?.Invoke(result);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private void Cancel() => RoundCornerCancelled?.Invoke();

    // ── SkiaSharp 圆角处理（后台线程执行）────────────────────────────────────

    /// <summary>
    /// 在后台线程对已 pin 的像素缓冲区应用圆角蒙版，返回冻结前的 BitmapSource。
    /// 调用方负责 Freeze()。
    /// </summary>
    private static BitmapSource ApplyRoundCornerOnBackground(byte[] pixels, int w, int h, int stride, int cornerRadius)
    {
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            // 将托管像素数组 pin 到 SKBitmap，零拷贝读取源像素
            using var srcBitmap = new SKBitmap();
            srcBitmap.InstallPixels(info, handle.AddrOfPinnedObject(), stride);

            // 创建透明背景的目标 bitmap，以支持圆角透明区域
            using var dst = new SKBitmap(info);
            using var canvas = new SKCanvas(dst);
            canvas.Clear(SKColors.Transparent);

            // 将半径钳制到图片短边的一半，防止圆角过大导致内容消失
            var r = (float)Math.Clamp(cornerRadius, 0, Math.Min(w, h) / 2);
            using var roundPath = new SKPath();
            roundPath.AddRoundRect(new SKRect(0, 0, w, h), r, r);

            using var paint = new SKPaint { IsAntialias = true };
            canvas.ClipPath(roundPath, SKClipOperation.Intersect, antialias: true);
            canvas.DrawBitmap(srcBitmap, 0, 0, paint);

            var bitmapSource = SkiaInteropHelper.SKBitmapToBitmapSource(dst);
            bitmapSource.Freeze();
            return bitmapSource;
        }
        finally
        {
            handle.Free();
        }
    }
}
