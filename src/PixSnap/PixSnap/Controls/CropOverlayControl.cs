using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;using Point = System.Windows.Point;

namespace PixSnap.Controls;

/// <summary>
/// 裁剪覆盖层控件：在图片预览区上显示半透明遮罩、裁剪矩形和 8 个拖拽手柄，
/// 支持整体拖动、按手柄调整尺寸、键盘微调和比例锁定。
/// </summary>
public class CropOverlayControl : Canvas
{
    // ── 视觉元素 ──────────────────────────────────────────────────────────
    private readonly Rectangle _maskTop, _maskBottom, _maskLeft, _maskRight;
    private readonly Rectangle _cropRect;
    private readonly Ellipse[] _handles = new Ellipse[8];

    private static readonly string[] HandleTags =
        ["TL", "TR", "BL", "BR", "TC", "BC", "ML", "MR"];

    private static readonly Cursor[] HandleCursors =
    [
        Cursors.SizeNWSE, Cursors.SizeNESW, Cursors.SizeNESW, Cursors.SizeNWSE,
        Cursors.SizeNS,   Cursors.SizeNS,   Cursors.SizeWE,   Cursors.SizeWE
    ];

    // ── 状态 ──────────────────────────────────────────────────────────────
    private Rect _bounds;              // 裁剪框在显示坐标系中的位置
    private Rect _imageDisplayRect;    // 图片在视口中的显示区域
    private int _imgPixelW, _imgPixelH;

    // 拖动整个裁剪框
    private bool _isDraggingRect;
    private Point _rectDragStart;
    private Rect _rectAtDragStart;

    // 拖动 handle
    private string? _dragHandle;
    private Point _handleDragStart;
    private Rect _handleDragStartRect;

    // 防止 SetPixelRect → CropRectChanged 回环
    private bool _externalUpdating;

    private const double MinSize = 10;
    private const double HandleDiameter = 10;

    // ── 公共属性 ──────────────────────────────────────────────────────────

    /// <summary>锁定的宽高比（0 = 自由裁剪）。</summary>
    public double LockedAspectRatio { get; set; }

    // ── 事件 ──────────────────────────────────────────────────────────────

    /// <summary>裁剪框像素坐标发生变化 (x, y, w, h)。</summary>
    public event Action<int, int, int, int>? CropRectChanged;

    /// <summary>用户按下 Enter 确认裁剪。</summary>
    public event Action? EnterPressed;

    /// <summary>用户按下 Escape 取消裁剪。</summary>
    public event Action? EscapePressed;

    // ── 构造 ──────────────────────────────────────────────────────────────

    public CropOverlayControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        IsHitTestVisible = true;

        var maskBrush = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
        maskBrush.Freeze();
        _maskTop = CreateMask(maskBrush);
        _maskBottom = CreateMask(maskBrush);
        _maskLeft = CreateMask(maskBrush);
        _maskRight = CreateMask(maskBrush);

        var strokeBrush = (SolidColorBrush)FindResource("SystemControlForegroundAccentBrush");
        _cropRect = new Rectangle
        {
            Stroke = strokeBrush,
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3],
            Fill = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            IsHitTestVisible = true
        };
        _cropRect.MouseLeftButtonDown += CropRect_MouseLeftButtonDown;
        _cropRect.MouseMove += CropRect_MouseMove;
        _cropRect.MouseLeftButtonUp += CropRect_MouseLeftButtonUp;

        Children.Add(_maskTop);
        Children.Add(_maskBottom);
        Children.Add(_maskLeft);
        Children.Add(_maskRight);
        Children.Add(_cropRect);

        for (int i = 0; i < 8; i++)
        {
            var handle = new Ellipse
            {
                Width = HandleDiameter,
                Height = HandleDiameter,
                Fill = Brushes.White,
                StrokeThickness = 0,
                Cursor = HandleCursors[i],
                Tag = HandleTags[i]
            };
            handle.MouseLeftButtonDown += Handle_MouseLeftButtonDown;
            handle.MouseMove += Handle_MouseMove;
            handle.MouseLeftButtonUp += Handle_MouseLeftButtonUp;
            _handles[i] = handle;
            Children.Add(handle);
        }

        KeyDown += OnKeyDown;
    }

    // ── 公共方法 ──────────────────────────────────────────────────────────

    /// <summary>初始化裁剪框为整个图片区域。</summary>
    public void Initialize(Rect imageDisplayRect, int imgPixelW, int imgPixelH)
    {
        _imageDisplayRect = imageDisplayRect;
        _imgPixelW = imgPixelW;
        _imgPixelH = imgPixelH;
        _bounds = imageDisplayRect;
        RefreshVisuals();
        FireCropRectChanged();
    }

    /// <summary>从外部像素坐标更新裁剪框（ViewModel 文本框输入时调用）。</summary>
    public void SetPixelRect(int cropX, int cropY, int cropW, int cropH,
                             Rect imageDisplayRect, int imgPixelW, int imgPixelH)
    {
        _imageDisplayRect = imageDisplayRect;
        _imgPixelW = imgPixelW;
        _imgPixelH = imgPixelH;

        var scaleX = imageDisplayRect.Width / imgPixelW;
        var scaleY = imageDisplayRect.Height / imgPixelH;

        _externalUpdating = true;
        _bounds = new Rect(
            imageDisplayRect.Left + cropX * scaleX,
            imageDisplayRect.Top + cropY * scaleY,
            cropW * scaleX,
            cropH * scaleY);
        RefreshVisuals();
        _externalUpdating = false;
    }

    /// <summary>视口尺寸变化时刷新（保持当前像素裁剪区域不变）。</summary>
    public void RefreshForResize(Rect newImageDisplayRect, int imgPixelW, int imgPixelH)
    {
        // 先用旧 imageDisplayRect 算出当前像素坐标
        var (px, py, pw, ph) = ComputePixelRect();

        _imageDisplayRect = newImageDisplayRect;
        _imgPixelW = imgPixelW;
        _imgPixelH = imgPixelH;

        // 用新 imageDisplayRect 重新计算显示坐标
        var scaleX = newImageDisplayRect.Width / imgPixelW;
        var scaleY = newImageDisplayRect.Height / imgPixelH;
        _bounds = new Rect(
            newImageDisplayRect.Left + px * scaleX,
            newImageDisplayRect.Top + py * scaleY,
            pw * scaleX,
            ph * scaleY);
        RefreshVisuals();
    }

    /// <summary>按图片像素坐标设置裁剪框。</summary>
    public void ApplyPixelBounds(int cropX, int cropY, int cropW, int cropH)
    {
        if (_imageDisplayRect.Width <= 0 || _imageDisplayRect.Height <= 0)
            return;

        var scaleX = _imageDisplayRect.Width / _imgPixelW;
        var scaleY = _imageDisplayRect.Height / _imgPixelH;

        _externalUpdating = true;
        _bounds = new Rect(
            _imageDisplayRect.Left + cropX * scaleX,
            _imageDisplayRect.Top + cropY * scaleY,
            cropW * scaleX,
            cropH * scaleY);
        RefreshVisuals();
        _externalUpdating = false;
        FireCropRectChanged();
    }

    // ── 内部方法 ──────────────────────────────────────────────────────────

    private (int X, int Y, int W, int H) ComputePixelRect()
    {
        if (_imageDisplayRect.Width <= 0 || _imageDisplayRect.Height <= 0)
            return (0, 0, _imgPixelW, _imgPixelH);

        var scaleX = (double)_imgPixelW / _imageDisplayRect.Width;
        var scaleY = (double)_imgPixelH / _imageDisplayRect.Height;

        int x = (int)Math.Round(Math.Clamp((_bounds.Left - _imageDisplayRect.Left) * scaleX, 0, _imgPixelW));
        int y = (int)Math.Round(Math.Clamp((_bounds.Top - _imageDisplayRect.Top) * scaleY, 0, _imgPixelH));
        int w = (int)Math.Round(Math.Clamp(_bounds.Width * scaleX, 1, _imgPixelW - x));
        int h = (int)Math.Round(Math.Clamp(_bounds.Height * scaleY, 1, _imgPixelH - y));
        return (x, y, w, h);
    }

    private void FireCropRectChanged()
    {
        if (_externalUpdating) return;
        var (x, y, w, h) = ComputePixelRect();
        CropRectChanged?.Invoke(x, y, w, h);
    }

    private void RefreshVisuals()
    {
        double vpW = ActualWidth, vpH = ActualHeight;
        var r = _bounds;

        // 4 块遮罩
        PlaceRect(_maskTop, 0, 0, vpW, r.Top);
        PlaceRect(_maskBottom, 0, r.Bottom, vpW, vpH - r.Bottom);
        PlaceRect(_maskLeft, 0, r.Top, r.Left, r.Height);
        PlaceRect(_maskRight, r.Right, r.Top, vpW - r.Right, r.Height);

        // 裁剪框
        SetLeft(_cropRect, r.Left);
        SetTop(_cropRect, r.Top);
        _cropRect.Width = Math.Max(0, r.Width);
        _cropRect.Height = Math.Max(0, r.Height);

        // 8 个 handle
        PlaceHandle(_handles[0], r.Left, r.Top);                       // TL
        PlaceHandle(_handles[1], r.Right, r.Top);                      // TR
        PlaceHandle(_handles[2], r.Left, r.Bottom);                    // BL
        PlaceHandle(_handles[3], r.Right, r.Bottom);                   // BR
        PlaceHandle(_handles[4], r.Left + r.Width / 2, r.Top);        // TC
        PlaceHandle(_handles[5], r.Left + r.Width / 2, r.Bottom);     // BC
        PlaceHandle(_handles[6], r.Left, r.Top + r.Height / 2);       // ML
        PlaceHandle(_handles[7], r.Right, r.Top + r.Height / 2);      // MR
    }

    private static Rectangle CreateMask(Brush fill) => new() { Fill = fill };

    private static void PlaceRect(Rectangle rect, double x, double y, double w, double h)
    {
        SetLeft(rect, x);
        SetTop(rect, y);
        rect.Width = Math.Max(0, w);
        rect.Height = Math.Max(0, h);
    }

    private static void PlaceHandle(Ellipse e, double cx, double cy)
    {
        SetLeft(e, cx - e.Width / 2);
        SetTop(e, cy - e.Height / 2);
    }

    // ── 裁剪框整体拖动 ────────────────────────────────────────────────────

    private void CropRect_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingRect = true;
        _rectDragStart = e.GetPosition(this);
        _rectAtDragStart = _bounds;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void CropRect_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingRect) return;
        var pos = e.GetPosition(this);
        var delta = pos - _rectDragStart;
        var img = _imageDisplayRect;
        var maxX = Math.Max(img.Left, img.Right - _bounds.Width);
        var maxY = Math.Max(img.Top, img.Bottom - _bounds.Height);
        var newX = Math.Clamp(_rectAtDragStart.Left + delta.X, img.Left, maxX);
        var newY = Math.Clamp(_rectAtDragStart.Top + delta.Y, img.Top, maxY);
        _bounds = new Rect(newX, newY, _bounds.Width, _bounds.Height);
        RefreshVisuals();
        FireCropRectChanged();
    }

    private void CropRect_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingRect = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    // ── 8 个 handle 拖拽 ──────────────────────────────────────────────────

    private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragHandle = (sender as FrameworkElement)?.Tag as string;
        _handleDragStart = e.GetPosition(this);
        _handleDragStartRect = _bounds;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void Handle_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragHandle is null) return;
        var pos = e.GetPosition(this);
        var dx = pos.X - _handleDragStart.X;
        var dy = pos.Y - _handleDragStart.Y;
        var r = _handleDragStartRect;

        double l = r.Left, t = r.Top, rr = r.Right, b = r.Bottom;
        switch (_dragHandle)
        {
            case "TL": l = Math.Min(l + dx, rr - MinSize); t = Math.Min(t + dy, b - MinSize); break;
            case "TR": rr = Math.Max(rr + dx, l + MinSize); t = Math.Min(t + dy, b - MinSize); break;
            case "BL": l = Math.Min(l + dx, rr - MinSize); b = Math.Max(b + dy, t + MinSize); break;
            case "BR": rr = Math.Max(rr + dx, l + MinSize); b = Math.Max(b + dy, t + MinSize); break;
            case "TC": t = Math.Min(t + dy, b - MinSize); break;
            case "BC": b = Math.Max(b + dy, t + MinSize); break;
            case "ML": l = Math.Min(l + dx, rr - MinSize); break;
            case "MR": rr = Math.Max(rr + dx, l + MinSize); break;
        }

        var img = _imageDisplayRect;
        l = Math.Max(img.Left, l);
        t = Math.Max(img.Top, t);
        rr = Math.Min(img.Right, rr);
        b = Math.Min(img.Bottom, b);
        if (rr - l < MinSize) rr = Math.Min(l + MinSize, img.Right);
        if (b - t < MinSize) b = Math.Min(t + MinSize, img.Bottom);

        // 比例锁定
        if (LockedAspectRatio > 0)
        {
            var ratio = LockedAspectRatio;
            double w = rr - l, h = b - t;
            double desiredH = w / ratio;
            if (desiredH > img.Height) { h = img.Height; w = h * ratio; }
            else h = desiredH;
            if (w > img.Width) { w = img.Width; h = w / ratio; }

            if (_dragHandle is "TL" or "ML" or "BL") rr = l + w; else l = rr - w;
            if (_dragHandle is "TL" or "TC" or "TR") b = t + h; else t = b - h;

            if (l < img.Left) { l = img.Left; rr = l + w; }
            if (t < img.Top) { t = img.Top; b = t + h; }
            if (rr > img.Right) { rr = img.Right; l = rr - w; }
            if (b > img.Bottom) { b = img.Bottom; t = b - h; }
        }

        _bounds = new Rect(l, t, rr - l, b - t);
        RefreshVisuals();
        FireCropRectChanged();
    }

    private void Handle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragHandle = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    // ── 键盘操作 ──────────────────────────────────────────────────────────

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        var img = _imageDisplayRect;
        double imgScaleX = img.Width > 0 && _imgPixelW > 0 ? img.Width / _imgPixelW : 1.0;
        double imgScaleY = img.Height > 0 && _imgPixelH > 0 ? img.Height / _imgPixelH : 1.0;
        double dX = step * imgScaleX;
        double dY = step * imgScaleY;

        switch (e.Key)
        {
            case Key.Left:
                _bounds = new Rect(Math.Max(img.Left, _bounds.Left - dX), _bounds.Top, _bounds.Width, _bounds.Height);
                break;
            case Key.Right:
                _bounds = new Rect(Math.Min(img.Right - _bounds.Width, _bounds.Left + dX), _bounds.Top, _bounds.Width, _bounds.Height);
                break;
            case Key.Up:
                _bounds = new Rect(_bounds.Left, Math.Max(img.Top, _bounds.Top - dY), _bounds.Width, _bounds.Height);
                break;
            case Key.Down:
                _bounds = new Rect(_bounds.Left, Math.Min(img.Bottom - _bounds.Height, _bounds.Top + dY), _bounds.Width, _bounds.Height);
                break;
            case Key.Enter:
                EnterPressed?.Invoke();
                e.Handled = true;
                return;
            case Key.Escape:
                EscapePressed?.Invoke();
                e.Handled = true;
                return;
            default:
                return;
        }

        RefreshVisuals();
        FireCropRectChanged();
        e.Handled = true;
    }
}
