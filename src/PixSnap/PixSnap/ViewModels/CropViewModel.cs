using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
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

    // ── 命令 ─────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(IsValid))]
    private void Apply(BitmapSource source)
    {
        var cropped = CropWithSkia(source);
        CropApplied?.Invoke(cropped);
    }

    [RelayCommand]
    private void Cancel()
    {
        CropCancelled?.Invoke();
    }

    // ── SkiaSharp 裁剪 ───────────────────────────────────────────────────────

    private BitmapSource CropWithSkia(BitmapSource source)
    {
        // 将 WPF BitmapSource 锁定像素缓冲区，直接安装到 SKBitmap 避免额外内存拷贝
        var wb = new System.Windows.Media.Imaging.WriteableBitmap(source);
        wb.Lock();
        try
        {
            var info = new SKImageInfo(wb.PixelWidth, wb.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var srcBitmap = new SKBitmap();
            srcBitmap.InstallPixels(info, wb.BackBuffer, wb.BackBufferStride);

            // 将裁剪区域钳制到图片实际范围内
            var x = Math.Clamp(CropX, 0, _imagePixelWidth - 1);
            var y = Math.Clamp(CropY, 0, _imagePixelHeight - 1);
            var w = Math.Clamp(CropWidth, 1, _imagePixelWidth - x);
            var h = Math.Clamp(CropHeight, 1, _imagePixelHeight - y);
            var rect = new SKRectI(x, y, x + w, y + h);

            using var subset = new SKBitmap();
            srcBitmap.ExtractSubset(subset, rect);

            return ConvertToBitmapSource(subset);
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
}
