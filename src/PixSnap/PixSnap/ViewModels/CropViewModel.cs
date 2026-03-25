using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SkiaSharp;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixSnap.ViewModels;

/// <summary>
/// 裁剪面板的 ViewModel：管理裁剪矩形的 X/Y/宽/高，并执行 SkiaSharp 裁剪。
/// </summary>
public partial class CropViewModel : ObservableObject
{
    private int _imagePixelWidth;
    private int _imagePixelHeight;

    // ── 比例锁定 ─────────────────────────────────────────────────────────────

    /// <summary>锁定的宽高比（W:H）。0 表示自由裁剪。</summary>
    [ObservableProperty]
    private double _lockedAspectRatio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AspectRatioDisplayText))]
    private string _aspectRatioText = "自由";

    public string AspectRatioDisplayText => string.Format("当前比例: {0}", AspectRatioText);

    /// <summary>设置固定比例并立即应用到当前裁剪框（以中心为基准）。</summary>
    public void SetAspectRatio(double ratioW, double ratioH, string label)
    {
        AspectRatioText = label;
        if (ratioW <= 0 || ratioH <= 0)
        {
            LockedAspectRatio = 0;
            return;
        }
        LockedAspectRatio = ratioW / ratioH;
        ApplyLockedRatio();
    }

    /// <summary>
    /// 命令参数格式: "ratioW:ratioH" 或 "ratioW:ratioH:label"。
    /// 省略 label 时，0:0 默认使用 "自由"，其余默认 "W:H"。
    /// </summary>
    [RelayCommand]
    private void SetAspectRatioFromParam(string? param)
    {
        if (string.IsNullOrEmpty(param)) return;
        var parts = param.Split(':', 3);
        if (parts.Length < 2) return;
        if (!double.TryParse(parts[0], out var w) || !double.TryParse(parts[1], out var h)) return;
        var label = parts.Length >= 3
            ? parts[2]
            : (w == 0 && h == 0 ? "自由" : $"{w}:{h}");
        SetAspectRatio(w, h, label);
    }

    /// <summary>以当前裁剪框中心为基准，按锁定比例计算最大可容纳矩形。</summary>
    private void ApplyLockedRatio()
    {
        if (LockedAspectRatio <= 0) return;

        double cx = CropX + CropWidth / 2.0;
        double cy = CropY + CropHeight / 2.0;

        // 尝试保持宽度不变
        int newW = CropWidth;
        int newH = (int)Math.Round(newW / LockedAspectRatio);
        if (newH > _imagePixelHeight)
        {
            newH = _imagePixelHeight;
            newW = (int)Math.Round(newH * LockedAspectRatio);
        }
        if (newW > _imagePixelWidth)
        {
            newW = _imagePixelWidth;
            newH = (int)Math.Round(newW / LockedAspectRatio);
        }

        int newX = (int)Math.Round(Math.Clamp(cx - newW / 2.0, 0, _imagePixelWidth - newW));
        int newY = (int)Math.Round(Math.Clamp(cy - newH / 2.0, 0, _imagePixelHeight - newH));

        CropX = newX;
        CropY = newY;
        CropWidth = newW;
        CropHeight = newH;
    }

    /// <summary>当比例锁定时，根据宽度变化自动调整高度（供 View 调用）。</summary>
    public void EnforceAspectRatioFromWidth()
    {
        if (LockedAspectRatio <= 0) return;
        int newH = (int)Math.Round(CropWidth / LockedAspectRatio);
        newH = Math.Clamp(newH, 1, _imagePixelHeight - CropY);
        CropHeight = newH;
    }

    /// <summary>当比例锁定时，根据高度变化自动调整宽度（供 View 调用）。</summary>
    public void EnforceAspectRatioFromHeight()
    {
        if (LockedAspectRatio <= 0) return;
        int newW = (int)Math.Round(CropHeight * LockedAspectRatio);
        newW = Math.Clamp(newW, 1, _imagePixelWidth - CropX);
        CropWidth = newW;
    }

    // ── 裁剪矩形参数（像素，以原始图片像素为单位） ────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private int _cropX;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private int _cropY;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private int _cropWidth = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private int _cropHeight = 100;

    public bool IsValid =>
        CropX >= 0 && CropY >= 0 &&
        CropWidth > 0 && CropHeight > 0 &&
        CropX + CropWidth <= _imagePixelWidth &&
        CropY + CropHeight <= _imagePixelHeight;

    /// <summary>
    /// 通知 View 执行裁剪并用新图替换原图的事件。
    /// </summary>
    public event Action<BitmapSource>? CropApplied;

    /// <summary>
    /// 通知 View 关闭裁剪模式。
    /// </summary>
    public event Action? CropCancelled;

    // ── 初始化 ───────────────────────────────────────────────────────────────

    public void Initialize(int imagePixelWidth, int imagePixelHeight)
    {
        _imagePixelWidth = imagePixelWidth;
        _imagePixelHeight = imagePixelHeight;
        Log.Debug("裁剪初始化: {W}×{H}", imagePixelWidth, imagePixelHeight);

        // 重置比例锁定
        LockedAspectRatio = 0;
        AspectRatioText = "自由";

        // 默认选取整张图片
        CropX = 0;
        CropY = 0;
        CropWidth = imagePixelWidth;
        CropHeight = imagePixelHeight;
    }

    // ── 处理状态 ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isCropProcessing;

    // ── 命令 ─────────────────────────────────────────────────────────────────

    private bool CanApply() => IsValid && !IsCropProcessing;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync(BitmapSource source)
    {
        Log.Information("应用裁剪: ({X},{Y}) {W}×{H}", CropX, CropY, CropWidth, CropHeight);
        IsCropProcessing = true;
        try
        {
            // 在 UI 线程完成格式转换与像素拷贝（快速内存拷贝）
            if (!source.IsFrozen) source.Freeze();
            var pbgra = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            int pixelW = pbgra.PixelWidth, pixelH = pbgra.PixelHeight;
            int stride = pixelW * 4;
            var pixels = new byte[stride * pixelH];
            pbgra.CopyPixels(pixels, stride, 0);

            var x = Math.Clamp(CropX, 0, _imagePixelWidth - 1);
            var y = Math.Clamp(CropY, 0, _imagePixelHeight - 1);
            var w = Math.Clamp(CropWidth, 1, _imagePixelWidth - x);
            var h = Math.Clamp(CropHeight, 1, _imagePixelHeight - y);

            // Skia 裁剪（CPU 密集）放到后台线程，返回原始像素
            var (croppedPixels, croppedStride) = await Task.Run(() => CropOnBackground(pixels, pixelW, pixelH, stride, x, y, w, h));

            // WriteableBitmap 必须在 UI 线程创建
            var cropped = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
            cropped.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), croppedPixels, croppedStride, 0);
            cropped.Freeze();
            CropApplied?.Invoke(cropped);
        }
        finally
        {
            IsCropProcessing = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        CropCancelled?.Invoke();
    }

    // ── SkiaSharp 裁剪（后台线程执行）────────────────────────────────────────

    private static (byte[] pixels, int stride) CropOnBackground(byte[] pixels, int pixelW, int pixelH, int stride, int x, int y, int w, int h)
    {
        var info = new SKImageInfo(pixelW, pixelH, SKColorType.Bgra8888, SKAlphaType.Premul);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var srcBitmap = new SKBitmap();
            srcBitmap.InstallPixels(info, handle.AddrOfPinnedObject(), stride);

            using var extracted = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(extracted);
            canvas.DrawBitmap(srcBitmap, SKRect.Create(x, y, w, h), SKRect.Create(0, 0, w, h));

            var outStride = extracted.RowBytes;
            var outPixels = new byte[outStride * h];
            Marshal.Copy(extracted.GetPixels(), outPixels, 0, outPixels.Length);
            return (outPixels, outStride);
        }
        finally
        {
            handle.Free();
        }
    }

    // 裁剪参数变化时刷新"应用"按钮的可用状态
    partial void OnCropXChanged(int value) => ApplyCommand.NotifyCanExecuteChanged();
    partial void OnCropYChanged(int value) => ApplyCommand.NotifyCanExecuteChanged();
    partial void OnCropWidthChanged(int value) => ApplyCommand.NotifyCanExecuteChanged();
    partial void OnCropHeightChanged(int value) => ApplyCommand.NotifyCanExecuteChanged();
    partial void OnIsCropProcessingChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();
}
