using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

            // Skia 编码（CPU 密集）放到后台线程，避免卡 UI
            var cropped = await Task.Run(() => CropOnBackground(pixels, pixelW, pixelH, stride, x, y, w, h));
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

    private static BitmapSource CropOnBackground(byte[] pixels, int pixelW, int pixelH, int stride, int x, int y, int w, int h)
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

            return ConvertToBitmapSource(extracted);
        }
        finally
        {
            handle.Free();
        }
    }

    private static BitmapSource ConvertToBitmapSource(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new System.IO.MemoryStream(data.ToArray());
        var decoder = System.Windows.Media.Imaging.BitmapFrame.Create(
            stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.None,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        return decoder;
    }

    // 裁剪参数变化时刷新"应用"按钮的可用状态
    partial void OnCropXChanged(int value) => ApplyCommand.NotifyCanExecuteChanged();
    partial void OnCropYChanged(int value) => ApplyCommand.NotifyCanExecuteChanged();
    partial void OnCropWidthChanged(int value) => ApplyCommand.NotifyCanExecuteChanged();
    partial void OnCropHeightChanged(int value) => ApplyCommand.NotifyCanExecuteChanged();
    partial void OnIsCropProcessingChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();
}
