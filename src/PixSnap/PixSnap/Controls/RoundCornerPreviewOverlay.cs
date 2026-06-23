using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PixSnap.Controls;

/// <summary>
/// 圆角编辑预览蒙版：在将被裁掉的四角叠加半透明遮罩，并以圆角描边标示保留区域。
/// </summary>
public class RoundCornerPreviewOverlay : Canvas
{
    private readonly Path _maskPath;
    private readonly Rectangle _borderRect;

    private static readonly Brush MaskBrush = CreateFrozenMaskBrush();

    public RoundCornerPreviewOverlay()
    {
        IsHitTestVisible = false;

        _maskPath = new Path
        {
            Fill = MaskBrush,
            IsHitTestVisible = false
        };

        _borderRect = new Rectangle
        {
            StrokeThickness = 1.5,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };

        Children.Add(_maskPath);
        Children.Add(_borderRect);

        Loaded += (_, _) =>
        {
            if (TryFindResource("SystemControlForegroundAccentBrush") is Brush accent)
                _borderRect.Stroke = accent;
        };
    }

    public void Update(Rect imageDisplayRect, int imgPixelW, int imgPixelH, int cornerRadiusPx)
    {
        if (imageDisplayRect.Width <= 0 || imageDisplayRect.Height <= 0 || imgPixelW <= 0 || imgPixelH <= 0)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;

        double scale = imageDisplayRect.Width / imgPixelW;
        double radius = Math.Clamp(
            cornerRadiusPx * scale,
            0,
            Math.Min(imageDisplayRect.Width, imageDisplayRect.Height) / 2);

        var outer = new RectangleGeometry(imageDisplayRect);
        var inner = new RectangleGeometry(imageDisplayRect, radius, radius);
        _maskPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);

        _borderRect.Width = imageDisplayRect.Width;
        _borderRect.Height = imageDisplayRect.Height;
        _borderRect.RadiusX = radius;
        _borderRect.RadiusY = radius;
        SetLeft(_borderRect, imageDisplayRect.Left);
        SetTop(_borderRect, imageDisplayRect.Top);
    }

    public void Hide() => Visibility = Visibility.Collapsed;

    private static SolidColorBrush CreateFrozenMaskBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0));
        brush.Freeze();
        return brush;
    }
}
