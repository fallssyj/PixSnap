using iNKORE.UI.WPF.Modern.Common.IconKeys;
using iNKORE.UI.WPF.Modern.Controls;
using PixSnap.Services;
using PixSnap.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PixSnap.Controls;

public partial class AnnotationOverlayControl : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(AnnotationViewModel), typeof(AnnotationOverlayControl),
            new PropertyMetadata(null, OnViewModelChanged));

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(AnnotationOverlayControl),
            new PropertyMetadata(false, OnIsActiveChanged));

    public static readonly DependencyProperty ImageSourceProperty =
        DependencyProperty.Register(nameof(ImageSource), typeof(BitmapSource), typeof(AnnotationOverlayControl),
            new PropertyMetadata(null, OnImageSourceChanged));

    public AnnotationViewModel? ViewModel
    {
        get => (AnnotationViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public BitmapSource? ImageSource
    {
        get => (BitmapSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    public AnnotationOverlayControl()
    {
        InitializeComponent();
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not AnnotationOverlayControl control)
            return;

        if (e.OldValue is AnnotationViewModel oldVm)
        {
            oldVm.PropertyChanged -= control.OnViewModelPropertyChanged;
            oldVm.RequestRedraw -= control.OnAnnotationRequestRedraw;
        }

        if (e.NewValue is AnnotationViewModel newVm)
        {
            newVm.PropertyChanged += control.OnViewModelPropertyChanged;
            newVm.RequestRedraw += control.OnAnnotationRequestRedraw;
        }

        control.RedrawAnnotationOverlay();
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not AnnotationOverlayControl control)
            return;

        var active = (bool)e.NewValue;
        control.IsHitTestVisible = active;
        control.AnnotationCanvas.IsHitTestVisible = active;
        control.UpdateAnnotationCursor();

        if (active)
        {
            control.UpdateAnnotationCanvasSize();
            control.AnnotationCanvas.Focus();
        }
        else
        {
            control.CommitActiveTextBox();
            control.AnnotationCanvas.Children.Clear();
        }
    }

    private static void OnImageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AnnotationOverlayControl control)
            control.UpdateAnnotationCanvasSize();
    }
    // ══════════════════════════════════════════════════════════════════════════
    // 标注画布交互
    // ══════════════════════════════════════════════════════════════════════════

    #region 标注字段

    // ── 标注拖拽 / 文本输入 ──────────────────────────────────────────
    private AnnotationItem? _dragAnnotation;
    private Point _dragStartImagePoint;
    private Vector _dragStartOffset;
    private System.Windows.Controls.TextBox? _activeTextBox;
    private bool _isEditingExistingText;
    private List<UIElement>? _dragElements;

    // ── 标注缩放拖拽 ─────────────────────────────────────────────────
    private AnnotationItem? _resizeAnnotation;
    private int _resizeHandle = -1;
    private Point _resizeStartStart;
    private Point _resizeStartEnd;
    private double _resizeStartFontSize;
    private List<Point>? _resizeStartPenPoints;

    #endregion

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnnotationViewModel.SelectedTool))
        {
            UpdateAnnotationCursor();
            // 切换工具时清除选中
            if (ViewModel is not null &&
                ViewModel.SelectedTool != AnnotationTool.Pointer)
            {
                ViewModel.SelectedAnnotation = null;
                RedrawAnnotationOverlay();
            }
        }
    }

    private void OnAnnotationRequestRedraw() => RedrawAnnotationOverlay();

    private void UpdateAnnotationCursor()
    {
        if (ViewModel is null || !IsActive)
        {
            AnnotationCanvas.Cursor = null;
            return;
        }

        AnnotationCanvas.Cursor = ViewModel.SelectedTool == AnnotationTool.Pointer
            ? Cursors.Arrow
            : Cursors.Cross;
    }

    private void UpdateAnnotationCanvasSize()
    {
        if (ImageSource is null)
            return;

        AnnotationCanvas.Width = ImageSource.Width;
        AnnotationCanvas.Height = ImageSource.Height;
    }

    private bool TryGetImagePixelPoint(Point canvasPos, out Point imgPixelPoint)
    {
        imgPixelPoint = default;
        if (ImageSource is null)
            return false;

        double pw = ImageSource.PixelWidth;
        double ph = ImageSource.PixelHeight;
        double dpiScale = GetAnnotationDpiScale();
        double imgX = canvasPos.X * dpiScale;
        double imgY = canvasPos.Y * dpiScale;

        const double tolerance = 8;
        if (imgX < -tolerance || imgY < -tolerance ||
            imgX > pw + tolerance || imgY > ph + tolerance)
            return false;

        imgPixelPoint = new Point(Math.Clamp(imgX, 0, pw), Math.Clamp(imgY, 0, ph));
        return true;
    }

    /// <summary>DPI 缩放因子：Canvas DIP → Image Pixel。96 DPI 时为 1.0。</summary>
    private double GetAnnotationDpiScale()
    {
        if (ViewModel is not null && ImageSource is not null)
            return ImageSource.DpiX / 96.0;
        return 1.0;
    }

    private void AnnotationCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null || !IsActive) return;
        var pos = e.GetPosition(AnnotationCanvas);
        if (!TryGetImagePixelPoint(pos, out var imgPoint)) return;

        CommitActiveTextBox();

        if (ViewModel.SelectedTool == AnnotationTool.Pointer)
        {
            // 双击文本标注 → 重新编辑
            if (e.ClickCount == 2)
            {
                var dblHit = ViewModel.HitTest(imgPoint);
                if (dblHit is { Tool: AnnotationTool.Text })
                {
                    var editItem = ViewModel.BeginEditExistingText(dblHit);
                    if (editItem is not null)
                    {
                        _isEditingExistingText = true;
                        ShowTextInputBoxForExisting(editItem);
                    }
                    e.Handled = true;
                    return;
                }
            }

            // 优先检测缩放手柄
            if (ViewModel.SelectedAnnotation is { } sel)
            {
                int handle = HitTestResizeHandle(sel, pos);
                if (handle >= 0)
                {
                    _resizeAnnotation = sel;
                    _resizeHandle = handle;
                    _resizeStartStart = sel.Start;
                    _resizeStartEnd = sel.End;
                    _resizeStartFontSize = sel.FontSize;
                    _resizeStartPenPoints = sel.Tool == AnnotationTool.Pen ? [.. sel.PenPoints] : null;
                    AnnotationCanvas.Cursor = ResizeHandleCursors[handle];
                    AnnotationCanvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }

            var hit = ViewModel.HitTest(imgPoint);
            if (hit is not null)
            {
                ViewModel.SelectedAnnotation = hit;
                _dragAnnotation = hit;
                _dragStartImagePoint = imgPoint;
                _dragStartOffset = hit.Offset;
                AnnotationCanvas.Cursor = Cursors.SizeAll;
                AnnotationCanvas.CaptureMouse();
                // 标记被拖动标注及其选中框的所有 UI 元素
                RedrawAnnotationOverlay();
                _dragElements = [];
                foreach (UIElement child in AnnotationCanvas.Children)
                {
                    if (child is FrameworkElement fe && fe.Tag == hit)
                        _dragElements.Add(child);
                }
            }
            else
            {
                // 点击空白区域取消选中
                ViewModel.SelectedAnnotation = null;
                RedrawAnnotationOverlay();
            }
            e.Handled = true;
            return;
        }

        ViewModel.BeginAnnotation(imgPoint);
        AnnotationCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void AnnotationCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (ViewModel is null || !IsActive) return;

        var pos = e.GetPosition(AnnotationCanvas);

        // 非拖拽时更新鼠标指针（悬停在手柄上变为缩放光标）
        if (e.LeftButton != MouseButtonState.Pressed && ViewModel.SelectedTool == AnnotationTool.Pointer)
        {
            if (ViewModel.SelectedAnnotation is { } sel)
            {
                int h = HitTestResizeHandle(sel, pos);
                AnnotationCanvas.Cursor = h >= 0 ? ResizeHandleCursors[h] : Cursors.Arrow;
            }
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;

        if (!TryGetImagePixelPoint(pos, out var imgPoint)) return;

        // 缩放拖拽
        if (_resizeAnnotation is not null)
        {
            ApplyResize(_resizeAnnotation, _resizeHandle, imgPoint);
            RedrawAnnotationOverlay();
            return;
        }

        if (_dragAnnotation is not null)
        {
            var delta = imgPoint - _dragStartImagePoint;
            _dragAnnotation.Offset = _dragStartOffset + delta;

            // 脏区优化：仅平移被拖动标注的 UI 元素，不重建整个画布
            if (_dragElements is { Count: > 0 })
            {
                double d = 1.0 / GetAnnotationDpiScale();
                double dx = (delta.X - (_dragStartOffset == default ? 0 : 0)) * d;
                double dy = (delta.Y - 0) * d;
                // 增量 = 当前 Offset 与初始 Offset 的像素差值转 DIP
                double tdx = (_dragAnnotation.Offset.X - _dragStartOffset.X) * d;
                double tdy = (_dragAnnotation.Offset.Y - _dragStartOffset.Y) * d;
                foreach (var el in _dragElements)
                {
                    if (el.RenderTransform is TranslateTransform tt)
                    {
                        tt.X = tdx;
                        tt.Y = tdy;
                    }
                    else
                    {
                        el.RenderTransform = new TranslateTransform(tdx, tdy);
                    }
                }
            }
            else
            {
                RedrawAnnotationOverlay();
            }
            return;
        }

        if (ViewModel.CurrentAnnotation is null) return;
        ViewModel.UpdateAnnotation(imgPoint);
        RedrawAnnotationOverlay();
    }

    private void AnnotationCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null || !IsActive) return;

        // 缩放结束 → 提交到撤销栈
        if (_resizeAnnotation is not null)
        {
            if (_resizeAnnotation.Tool == AnnotationTool.Text)
            {
                // 文本缩放修改的是字号，通过 EditTextStyleAction 撤销
                if (Math.Abs(_resizeAnnotation.FontSize - _resizeStartFontSize) > 0.1)
                {
                    var item = _resizeAnnotation;
                    ViewModel.CommitTextResize(item, _resizeStartFontSize);
                }
            }
            else if (_resizeAnnotation.Tool == AnnotationTool.Pen && _resizeStartPenPoints is not null)
            {
                ViewModel.CommitPenResize(_resizeAnnotation, _resizeStartPenPoints, _resizeStartStart, _resizeStartEnd);
            }
            else
            {
                ViewModel.CommitResize(_resizeAnnotation, _resizeStartStart, _resizeStartEnd);
            }
            _resizeAnnotation = null;
            _resizeHandle = -1;
            _resizeStartPenPoints = null;
            UpdateAnnotationCursor();
            AnnotationCanvas.ReleaseMouseCapture();
            RedrawAnnotationOverlay();
            e.Handled = true;
            return;
        }

        // 拖拽结束 → 提交移动到撤销栈
        if (_dragAnnotation is not null)
        {
            ViewModel.CommitMove(_dragAnnotation, _dragStartOffset);
            _dragAnnotation = null;
            _dragElements = null;
            UpdateAnnotationCursor();
            AnnotationCanvas.ReleaseMouseCapture();
            RedrawAnnotationOverlay(); // 最终全量刷新，清除临时 RenderTransform
            e.Handled = true;
            return;
        }

        if (ViewModel.CurrentAnnotation is null) return;

        var isText = ViewModel.CurrentAnnotation.Tool == AnnotationTool.Text;
        ViewModel.EndAnnotation();
        AnnotationCanvas.ReleaseMouseCapture();

        if (isText && ViewModel.CurrentAnnotation is { } textItem)
        {
            ShowTextInputBox(textItem);
        }
        else
        {
            RedrawAnnotationOverlay();
        }
        e.Handled = true;
    }

    // ── 标注键盘快捷键 ────────────────────────────────────

    private void AnnotationCanvas_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (ViewModel is null || !IsActive) return;
        if (_activeTextBox is not null) return; // 文本输入中不处理

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D)
        {
            ViewModel.DuplicateSelectedAnnotationCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None && ViewModel.TrySelectToolFromKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            ViewModel.DeleteSelectedAnnotationCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (ViewModel.SelectedAnnotation is not null)
            {
                ViewModel.SelectedAnnotation = null;
                RedrawAnnotationOverlay();
            }
            e.Handled = true;
        }
    }

    // ── 文本输入框管理 ─────────────────────────────────────

    private void ShowTextInputBox(AnnotationItem textItem)
    {
        if (ViewModel is null || ImageSource is null) return;

        // 标注坐标为像素空间，Canvas 为 DIP 空间，需转换
        double d = 1.0 / GetAnnotationDpiScale();
        double sx = textItem.Start.X * d;
        double sy = textItem.Start.Y * d;

        var tb = new System.Windows.Controls.TextBox
        {
            MinWidth = 80,
            MaxWidth = Math.Max(120, ImageSource.Width - sx - 8),
            FontSize = Math.Max(10, textItem.FontSize * d),
            FontFamily = new System.Windows.Media.FontFamily(textItem.FontFamily),
            FontWeight = textItem.IsBold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = textItem.IsItalic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = new SolidColorBrush(textItem.StrokeColor),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(128, textItem.StrokeColor.R, textItem.StrokeColor.G, textItem.StrokeColor.B)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            AcceptsReturn = false,
            CaretBrush = new SolidColorBrush(textItem.StrokeColor),
            Tag = textItem
        };
        var inputDecs = new TextDecorationCollection();
        if (textItem.IsUnderline) inputDecs.Add(TextDecorations.Underline);
        if (textItem.IsStrikethrough) inputDecs.Add(TextDecorations.Strikethrough);
        if (inputDecs.Count > 0) tb.TextDecorations = inputDecs;
        Canvas.SetLeft(tb, sx);
        Canvas.SetTop(tb, sy);
        AnnotationCanvas.Children.Add(tb);

        tb.LostFocus += OnTextBoxLostFocus;
        tb.KeyDown += OnTextBoxKeyDown;

        _activeTextBox = tb;
        tb.Focus();
    }

    private void OnTextBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Escape)
        {
            CommitActiveTextBox();
            e.Handled = true;
        }
    }

    private void OnTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        CommitActiveTextBox();
    }

    private void CommitActiveTextBox()
    {
        if (_activeTextBox is null || ViewModel is null) return;

        var tb = _activeTextBox;
        _activeTextBox = null;

        tb.LostFocus -= OnTextBoxLostFocus;
        tb.KeyDown -= OnTextBoxKeyDown;

        var text = tb.Text?.Trim() ?? string.Empty;

        if (_isEditingExistingText)
        {
            _isEditingExistingText = false;
            ViewModel.CommitEditExistingText(text);
        }
        else
        {
            ViewModel.CommitTextAnnotation(text);
        }

        AnnotationCanvas.Children.Remove(tb);
        RedrawAnnotationOverlay();
    }

    /// <summary>为双击编辑已有文本标注打开 TextBox（预填内容）。</summary>
    private void ShowTextInputBoxForExisting(AnnotationItem textItem)
    {
        if (ViewModel is null || ImageSource is null) return;

        double d = 1.0 / GetAnnotationDpiScale();
        double sx = (textItem.Start.X + textItem.Offset.X) * d;
        double sy = (textItem.Start.Y + textItem.Offset.Y) * d;

        var tb = new System.Windows.Controls.TextBox
        {
            Text = textItem.Text,
            MinWidth = 80,
            MaxWidth = Math.Max(120, ImageSource.Width - sx - 8),
            FontSize = Math.Max(10, textItem.FontSize * d),
            FontFamily = new System.Windows.Media.FontFamily(textItem.FontFamily),
            FontWeight = textItem.IsBold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = textItem.IsItalic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = new SolidColorBrush(textItem.StrokeColor),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(128, textItem.StrokeColor.R, textItem.StrokeColor.G, textItem.StrokeColor.B)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            AcceptsReturn = false,
            CaretBrush = new SolidColorBrush(textItem.StrokeColor),
            Tag = textItem
        };
        var inputDecs = new TextDecorationCollection();
        if (textItem.IsUnderline) inputDecs.Add(TextDecorations.Underline);
        if (textItem.IsStrikethrough) inputDecs.Add(TextDecorations.Strikethrough);
        if (inputDecs.Count > 0) tb.TextDecorations = inputDecs;
        Canvas.SetLeft(tb, sx);
        Canvas.SetTop(tb, sy);

        // 先刷新 overlay 但去掉被编辑的文本标注的显示
        RedrawAnnotationOverlay();
        AnnotationCanvas.Children.Add(tb);

        tb.LostFocus += OnTextBoxLostFocus;
        tb.KeyDown += OnTextBoxKeyDown;

        _activeTextBox = tb;
        tb.Focus();
        tb.SelectAll();
    }

    /// <summary>重绘所有标注到画布上（使用 WPF Shapes 实时预览）。</summary>
    private void RedrawAnnotationOverlay()
    {
        if (ViewModel is null) return;

        // 保留活跃的文本输入框
        var keepTextBox = _activeTextBox;
        AnnotationCanvas.Children.Clear();
        if (keepTextBox is not null)
            AnnotationCanvas.Children.Add(keepTextBox);

        if (ImageSource is null) return;

        var selected = ViewModel.SelectedAnnotation;

        foreach (var ann in ViewModel.Annotations)
            DrawAnnotationShape(ann, ann == selected);

        if (ViewModel.CurrentAnnotation is { } cur)
            DrawAnnotationShape(cur, false);
    }

    private void DrawAnnotationShape(AnnotationItem item, bool isSelected)
    {
        double d = 1.0 / GetAnnotationDpiScale();
        var brush = new SolidColorBrush(item.StrokeColor);
        double thickness = item.StrokeWidth * d;
        double iox = item.Offset.X * d, ioy = item.Offset.Y * d;
        double sx = item.Start.X * d + iox, sy = item.Start.Y * d + ioy;
        double ex = item.End.X * d + iox, ey = item.End.Y * d + ioy;

        switch (item.Tool)
        {
            case AnnotationTool.Arrow: DrawArrow(item, brush, thickness, sx, sy, ex, ey); break;
            case AnnotationTool.Rectangle: DrawRectangle(item, brush, thickness, d, sx, sy, ex, ey); break;
            case AnnotationTool.Ellipse: DrawEllipse(item, brush, thickness, sx, sy, ex, ey); break;
            case AnnotationTool.Number: DrawNumber(item, brush, d, sx, sy); break;
            case AnnotationTool.Text: DrawText(item, brush, d, sx, sy); break;
            case AnnotationTool.Pen: DrawPen(item, brush, thickness, d, iox, ioy); break;
            case AnnotationTool.Blur: DrawBlur(item, d, iox, ioy, sx, sy, ex, ey); break;
        }

        if (isSelected) DrawSelectionFrame(item, d);
    }

    private void DrawArrow(AnnotationItem item, SolidColorBrush brush, double thickness,
        double sx, double sy, double ex, double ey)
    {
        var pts = AnnotationViewModel.CalculateArrowPoints(new Point(sx, sy), new Point(ex, ey), thickness);
        if (pts.Length == 0) return;
        var poly = new System.Windows.Shapes.Polygon { Fill = brush, Tag = item };
        foreach (var p in pts) poly.Points.Add(p);
        AnnotationCanvas.Children.Add(poly);
    }

    private void DrawRectangle(AnnotationItem item, SolidColorBrush brush, double thickness,
        double d, double sx, double sy, double ex, double ey)
    {
        var rect = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Abs(ex - sx),
            Height = Math.Abs(ey - sy),
            Stroke = brush,
            StrokeThickness = thickness,
            RadiusX = item.CornerRadius * d,
            RadiusY = item.CornerRadius * d,
            Tag = item
        };
        if (item.HasFill)
            rect.Fill = CreateFillBrush(item);
        Canvas.SetLeft(rect, Math.Min(sx, ex));
        Canvas.SetTop(rect, Math.Min(sy, ey));
        AnnotationCanvas.Children.Add(rect);
    }

    private void DrawEllipse(AnnotationItem item, SolidColorBrush brush, double thickness,
        double sx, double sy, double ex, double ey)
    {
        var ell = new System.Windows.Shapes.Ellipse
        {
            Width = Math.Abs(ex - sx),
            Height = Math.Abs(ey - sy),
            Stroke = brush,
            StrokeThickness = thickness,
            Tag = item
        };
        if (item.HasFill)
            ell.Fill = CreateFillBrush(item);
        Canvas.SetLeft(ell, Math.Min(sx, ex));
        Canvas.SetTop(ell, Math.Min(sy, ey));
        AnnotationCanvas.Children.Add(ell);
    }

    private static SolidColorBrush CreateFillBrush(AnnotationItem item)
    {
        var alpha = (byte)Math.Clamp(Math.Round(item.FillOpacity / 100.0 * 255), 0, 255);
        return new SolidColorBrush(Color.FromArgb(alpha, item.StrokeColor.R, item.StrokeColor.G, item.StrokeColor.B));
    }

    private void DrawNumber(AnnotationItem item, SolidColorBrush brush, double d, double sx, double sy)
    {
        double size = Math.Max(Math.Abs(item.End.X - item.Start.X), Math.Abs(item.End.Y - item.Start.Y)) * d;
        if (size < 1) size = AnnotationViewModel.NumberBadgeDiameter * d;

        var circle = new System.Windows.Shapes.Ellipse
        {
            Width = size,
            Height = size,
            Fill = brush,
            Tag = item
        };
        Canvas.SetLeft(circle, sx);
        Canvas.SetTop(circle, sy);
        AnnotationCanvas.Children.Add(circle);

        var luminance = item.StrokeColor.R * 0.299 + item.StrokeColor.G * 0.587 + item.StrokeColor.B * 0.114;
        var textBrush = luminance > 180 ? Brushes.Black : Brushes.White;

        var label = new TextBlock
        {
            Text = item.Text,
            Foreground = textBrush,
            FontSize = Math.Max(10, item.FontSize * d),
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            Width = size,
            Tag = item
        };
        Canvas.SetLeft(label, sx);
        Canvas.SetTop(label, sy + size * 0.22);
        AnnotationCanvas.Children.Add(label);
    }

    private void DrawText(AnnotationItem item, SolidColorBrush brush, double d, double sx, double sy)
    {
        var textSize = AnnotationViewModel.MeasureTextSize(item);
        double pad = item.FontSize * d * 0.15;

        if (item.HasTextBackground && item.TextBackgroundColor.A > 0)
        {
            var bg = new System.Windows.Shapes.Rectangle
            {
                Width = textSize.Width * d + pad * 2,
                Height = textSize.Height * d + pad * 2,
                Fill = new SolidColorBrush(item.TextBackgroundColor),
                RadiusX = 4 * d,
                RadiusY = 4 * d,
                Tag = item
            };
            Canvas.SetLeft(bg, sx - pad);
            Canvas.SetTop(bg, sy - pad);
            AnnotationCanvas.Children.Add(bg);
        }

        var tb = new TextBlock
        {
            Text = item.Text,
            Foreground = brush,
            FontSize = item.FontSize * d,
            FontFamily = new System.Windows.Media.FontFamily(item.FontFamily),
            FontWeight = item.IsBold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = item.IsItalic ? FontStyles.Italic : FontStyles.Normal,
            Tag = item
        };
        var decs = new TextDecorationCollection();
        if (item.IsUnderline) decs.Add(TextDecorations.Underline);
        if (item.IsStrikethrough) decs.Add(TextDecorations.Strikethrough);
        if (decs.Count > 0) tb.TextDecorations = decs;
        Canvas.SetLeft(tb, sx);
        Canvas.SetTop(tb, sy);
        AnnotationCanvas.Children.Add(tb);
    }

    private void DrawPen(AnnotationItem item, SolidColorBrush brush, double thickness,
        double d, double iox, double ioy)
    {
        if (item.PenPoints.Count < 2) return;
        var path = new System.Windows.Shapes.Path
        {
            Data = AnnotationPenHelper.BuildSmoothPath(item.PenPoints, iox, ioy, d),
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Tag = item
        };
        AnnotationCanvas.Children.Add(path);
    }

    private void DrawBlur(AnnotationItem item, double d, double iox, double ioy,
        double sx, double sy, double ex, double ey)
    {
        double bw = Math.Abs(ex - sx), bh = Math.Abs(ey - sy);
        double bx = Math.Min(sx, ex), by = Math.Min(sy, ey);
        if (bw < 1 || bh < 1) return;

        if (ImageSource is { } src)
        {
            double scale = 1.0 / d;
            int px = (int)Math.Max(0, (bx - iox) * scale);
            int py = (int)Math.Max(0, (by - ioy) * scale);
            int pw = (int)Math.Min(src.PixelWidth - px, bw * scale);
            int ph = (int)Math.Min(src.PixelHeight - py, bh * scale);
            if (pw > 0 && ph > 0)
            {
                var cropped = new CroppedBitmap(src, new Int32Rect(px, py, pw, ph));
                BitmapSource previewSource = cropped;

                if (item.UseMosaic)
                {
                    int blockSize = Math.Max(4, (int)Math.Round(item.BlurRadius));
                    int smallW = Math.Max(1, pw / blockSize);
                    int smallH = Math.Max(1, ph / blockSize);
                    var down = new TransformedBitmap(cropped, new ScaleTransform(
                        smallW / (double)pw, smallH / (double)ph));
                    down.Freeze();
                    var up = new TransformedBitmap(down, new ScaleTransform(
                        pw / (double)smallW, ph / (double)smallH));
                    up.Freeze();
                    previewSource = up;
                }

                var blurImage = new System.Windows.Controls.Image
                {
                    Source = previewSource,
                    Width = bw,
                    Height = bh,
                    Stretch = System.Windows.Media.Stretch.Fill,
                    Tag = item
                };

                if (!item.UseMosaic)
                {
                    blurImage.Effect = new System.Windows.Media.Effects.BlurEffect
                    {
                        Radius = item.BlurRadius * d * 3
                    };
                }
                else
                {
                    RenderOptions.SetBitmapScalingMode(blurImage, BitmapScalingMode.NearestNeighbor);
                }

                Canvas.SetLeft(blurImage, bx);
                Canvas.SetTop(blurImage, by);
                AnnotationCanvas.Children.Add(blurImage);
            }
        }

        var border = new System.Windows.Shapes.Rectangle
        {
            Width = bw,
            Height = bh,
            Stroke = System.Windows.Media.Brushes.DodgerBlue,
            StrokeThickness = 1,
            StrokeDashArray = [4, 2],
            Fill = null,
            Tag = item
        };
        Canvas.SetLeft(border, bx);
        Canvas.SetTop(border, by);
        AnnotationCanvas.Children.Add(border);
    }

    private void DrawSelectionFrame(AnnotationItem item, double d)
    {
        var bounds = GetAnnotationScreenBounds(item, d);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var accent = GetAccentBrush();
        bounds.Inflate(5, 5);
        var selRect = new System.Windows.Shapes.Rectangle
        {
            Width = bounds.Width,
            Height = bounds.Height,
            Stroke = accent,
            StrokeThickness = 2,
            StrokeDashArray = [6, 3],
            Fill = null,
            Tag = item
        };
        Canvas.SetLeft(selRect, bounds.X);
        Canvas.SetTop(selRect, bounds.Y);
        AnnotationCanvas.Children.Add(selRect);

        const double hs = 8;
        double cx = bounds.X + bounds.Width / 2, cy = bounds.Y + bounds.Height / 2;
        Point[] handles =
        [
            new(bounds.X, bounds.Y),                                 // TL
            new(cx, bounds.Y),                                       // T
            new(bounds.X + bounds.Width, bounds.Y),                  // TR
            new(bounds.X + bounds.Width, cy),                        // R
            new(bounds.X + bounds.Width, bounds.Y + bounds.Height),  // BR
            new(cx, bounds.Y + bounds.Height),                       // B
            new(bounds.X, bounds.Y + bounds.Height),                 // BL
            new(bounds.X, cy),                                       // L
        ];
        foreach (var hp in handles)
        {
            var handle = new System.Windows.Shapes.Rectangle
            {
                Width = hs,
                Height = hs,
                Fill = System.Windows.Media.Brushes.White,
                Stroke = accent,
                StrokeThickness = 1.5,
                Tag = item
            };
            Canvas.SetLeft(handle, hp.X - hs / 2);
            Canvas.SetTop(handle, hp.Y - hs / 2);
            AnnotationCanvas.Children.Add(handle);
        }
    }

    private static Brush GetAccentBrush()
        => Application.Current.TryFindResource("SystemControlForegroundAccentBrush") as Brush
           ?? System.Windows.Media.Brushes.DodgerBlue;

    private static Rect GetAnnotationScreenBounds(AnnotationItem item, double d)
    {
        double iox = item.Offset.X * d;
        double ioy = item.Offset.Y * d;
        double sx = item.Start.X * d + iox;
        double sy = item.Start.Y * d + ioy;
        double ex = item.End.X * d + iox;
        double ey = item.End.Y * d + ioy;

        switch (item.Tool)
        {
            case AnnotationTool.Arrow:
                return new Rect(new Point(Math.Min(sx, ex), Math.Min(sy, ey)), new Point(Math.Max(sx, ex), Math.Max(sy, ey)));
            case AnnotationTool.Rectangle:
            case AnnotationTool.Ellipse:
            case AnnotationTool.Blur:
            case AnnotationTool.Number:
                return new Rect(new Point(Math.Min(sx, ex), Math.Min(sy, ey)), new Point(Math.Max(sx, ex), Math.Max(sy, ey)));
            case AnnotationTool.Text:
                var textSize = AnnotationViewModel.MeasureTextSize(item);
                return new Rect(sx, sy, textSize.Width * d, textSize.Height * d);
            case AnnotationTool.Pen:
                if (item.PenPoints.Count == 0) return Rect.Empty;
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var pt in item.PenPoints)
                {
                    double px = pt.X * d + iox;
                    double py = pt.Y * d + ioy;
                    if (px < minX) minX = px;
                    if (py < minY) minY = py;
                    if (px > maxX) maxX = px;
                    if (py > maxY) maxY = py;
                }
                return new Rect(minX, minY, maxX - minX, maxY - minY);
            default:
                return Rect.Empty;
        }
    }

    /// <summary>检测 canvas DIP 坐标是否落在选中标注的 8 个缩放手柄上。返回手柄索引 0-7 或 -1。</summary>
    private int HitTestResizeHandle(AnnotationItem selected, Point canvasPos)
    {
        double d = 1.0 / GetAnnotationDpiScale();
        var bounds = GetAnnotationScreenBounds(selected, d);
        if (bounds.Width <= 0 && bounds.Height <= 0) return -1;
        bounds.Inflate(4, 4);

        const double hs = 7;
        double cx = bounds.X + bounds.Width / 2;
        double cy = bounds.Y + bounds.Height / 2;
        Point[] handlePositions =
        [
            new(bounds.X, bounds.Y),
            new(cx, bounds.Y),
            new(bounds.X + bounds.Width, bounds.Y),
            new(bounds.X + bounds.Width, cy),
            new(bounds.X + bounds.Width, bounds.Y + bounds.Height),
            new(cx, bounds.Y + bounds.Height),
            new(bounds.X, bounds.Y + bounds.Height),
            new(bounds.X, cy),
        ];

        for (int i = 0; i < handlePositions.Length; i++)
        {
            if (Math.Abs(canvasPos.X - handlePositions[i].X) <= hs &&
                Math.Abs(canvasPos.Y - handlePositions[i].Y) <= hs)
                return i;
        }
        return -1;
    }

    private static readonly Cursor[] ResizeHandleCursors =
    [
        Cursors.SizeNWSE, // TL
        Cursors.SizeNS,   // T
        Cursors.SizeNESW, // TR
        Cursors.SizeWE,   // R
        Cursors.SizeNWSE, // BR
        Cursors.SizeNS,   // B
        Cursors.SizeNESW, // BL
        Cursors.SizeWE,   // L
    ];

    /// <summary>根据拖拽的手柄索引更新标注的 Start/End 坐标（像素空间）。</summary>
    private void ApplyResize(AnnotationItem item, int handle, Point imgPoint)
    {
        // 文本标注：通过缩放比例改变字号
        if (item.Tool == AnnotationTool.Text)
        {
            var origSize = AnnotationViewModel.MeasureTextSize(new AnnotationItem
            {
                Tool = AnnotationTool.Text,
                Text = item.Text,
                FontSize = _resizeStartFontSize,
                FontFamily = item.FontFamily,
                IsBold = item.IsBold,
                IsItalic = item.IsItalic
            });
            double ox = item.Offset.X, oy = item.Offset.Y;
            double sx = _resizeStartStart.X + ox;
            double sy = _resizeStartStart.Y + oy;

            // 根据拖拽方向计算缩放比例
            double scale = 1.0;
            switch (handle)
            {
                case 4: // BR — 最常用
                case 2: // TR
                case 6: // BL
                case 0: // TL
                    double newW = Math.Abs(imgPoint.X - sx);
                    double newH = Math.Abs(imgPoint.Y - sy);
                    double scaleW = origSize.Width > 1 ? newW / origSize.Width : 1;
                    double scaleH = origSize.Height > 1 ? newH / origSize.Height : 1;
                    scale = Math.Max(scaleW, scaleH);
                    break;
                case 3:
                case 7: // R, L — 水平
                    scale = origSize.Width > 1 ? Math.Abs(imgPoint.X - sx) / origSize.Width : 1;
                    break;
                case 1:
                case 5: // T, B — 垂直
                    scale = origSize.Height > 1 ? Math.Abs(imgPoint.Y - sy) / origSize.Height : 1;
                    break;
            }
            item.FontSize = Math.Clamp(_resizeStartFontSize * scale, 8, 200);

            // 同步面板显示
            if (ViewModel is not null)
                ViewModel.FontSize = item.FontSize;
            return;
        }

        // Pen 标注：对所有点做仿射缩放
        if (item.Tool == AnnotationTool.Pen && _resizeStartPenPoints is not null)
        {
            double oox = item.Offset.X, ooy = item.Offset.Y;
            // 计算原始包围盒（去掉 Offset）
            double oldMinX = double.MaxValue, oldMinY = double.MaxValue;
            double oldMaxX = double.MinValue, oldMaxY = double.MinValue;
            foreach (var pt in _resizeStartPenPoints)
            {
                oldMinX = Math.Min(oldMinX, pt.X);
                oldMinY = Math.Min(oldMinY, pt.Y);
                oldMaxX = Math.Max(oldMaxX, pt.X);
                oldMaxY = Math.Max(oldMaxY, pt.Y);
            }
            double oldW = Math.Max(1, oldMaxX - oldMinX);
            double oldH = Math.Max(1, oldMaxY - oldMinY);

            // 加上 Offset 得到屏幕空间的包围盒 LTRB
            double sl = oldMinX + oox, st = oldMinY + ooy;
            double sr = oldMaxX + oox, sb = oldMaxY + ooy;

            double nl = sl, nt = st, nr = sr, nb = sb;
            switch (handle)
            {
                case 0: nl = imgPoint.X; nt = imgPoint.Y; break;
                case 1: nt = imgPoint.Y; break;
                case 2: nr = imgPoint.X; nt = imgPoint.Y; break;
                case 3: nr = imgPoint.X; break;
                case 4: nr = imgPoint.X; nb = imgPoint.Y; break;
                case 5: nb = imgPoint.Y; break;
                case 6: nl = imgPoint.X; nb = imgPoint.Y; break;
                case 7: nl = imgPoint.X; break;
            }

            double newW = Math.Max(1, Math.Abs(nr - nl));
            double newH = Math.Max(1, Math.Abs(nb - nt));
            double newMinX = Math.Min(nl, nr) - oox;
            double newMinY = Math.Min(nt, nb) - ooy;
            double scaleX = newW / oldW;
            double scaleY = newH / oldH;

            item.PenPoints.Clear();
            foreach (var pt in _resizeStartPenPoints)
            {
                double nx = newMinX + (pt.X - oldMinX) * scaleX;
                double ny = newMinY + (pt.Y - oldMinY) * scaleY;
                item.PenPoints.Add(new Point(nx, ny));
            }
            item.Start = new Point(newMinX, newMinY);
            item.End = new Point(newMinX + newW, newMinY + newH);
            return;
        }

        // 非文本标注：修改 Start/End
        double oox2 = item.Offset.X, ooy2 = item.Offset.Y;
        double l = Math.Min(_resizeStartStart.X, _resizeStartEnd.X) + oox2;
        double t = Math.Min(_resizeStartStart.Y, _resizeStartEnd.Y) + ooy2;
        double r = Math.Max(_resizeStartStart.X, _resizeStartEnd.X) + oox2;
        double b = Math.Max(_resizeStartStart.Y, _resizeStartEnd.Y) + ooy2;

        switch (handle)
        {
            case 0: l = imgPoint.X; t = imgPoint.Y; break; // TL
            case 1: t = imgPoint.Y; break;                 // T
            case 2: r = imgPoint.X; t = imgPoint.Y; break; // TR
            case 3: r = imgPoint.X; break;                 // R
            case 4: r = imgPoint.X; b = imgPoint.Y; break; // BR
            case 5: b = imgPoint.Y; break;                 // B
            case 6: l = imgPoint.X; b = imgPoint.Y; break; // BL
            case 7: l = imgPoint.X; break;                 // L
        }

        item.Start = new Point(l - oox2, t - ooy2);
        item.End = new Point(r - oox2, b - ooy2);
    }
}
