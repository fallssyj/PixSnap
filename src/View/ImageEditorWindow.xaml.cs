// 编码：UTF-8 BOM
// 图片编辑器窗口后台代码：处理鼠标绘制交互

using PixSnap.Models;
using PixSnap.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
// 消除 WPF 与 WinForms 命名空间冲突
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Rectangle = System.Windows.Shapes.Rectangle;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace PixSnap.View;

/// <summary>图片编辑器窗口（后台代码处理 Canvas 绘制交互）</summary>
public partial class ImageEditorWindow : HandyControl.Controls.Window
{
    private readonly ImageEditorViewModel _vm;

    // ===== 绘制状态 =====
    private bool _isDrawing = false;
    private System.Windows.Point _drawStartPoint;
    private System.Windows.Point _drawCurrentPoint;

    /// <summary>实时预览图形元素（正在绘制中，未提交）</summary>
    private Shape? _previewShape;

    /// <summary>文字输入框（文字工具使用）</summary>
    private TextBox? _textInput;

    public ImageEditorWindow()
    {
        InitializeComponent();
        _vm = new ImageEditorViewModel();
        DataContext = _vm;

        // 监听 ViewModel 请求重绘
        _vm.RenderRequested += OnRenderRequested;
    }

    /// <summary>外部传入截图数据</summary>
    public void LoadBitmap(BitmapSource bitmap)
    {
        _vm.LoadBitmap(bitmap);
        UpdateImageSizeStatus();
    }

    // ===== 工具按钮点击（左侧工具栏）=====

    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string toolName
            && Enum.TryParse<DrawToolType>(toolName, out var tool))
        {
            _vm.SelectToolCommand.Execute(tool);
            UpdateActiveToolButton(btn);
        }
    }

    private Button? _activeToolButton;

    private void UpdateActiveToolButton(Button target)
    {
        // 重置所有工具按钮样式
        if (_activeToolButton != null)
        {
            _activeToolButton.Background = Brushes.Transparent;
        }
        _activeToolButton = target;
        target.Background = (Brush)FindResource("AccentBrush");
    }

    // ===== Canvas 鼠标绘制事件 =====

    private void DrawingCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.CurrentTool == DrawToolType.Select) return;

        _isDrawing = true;
        _drawStartPoint = e.GetPosition(DrawingCanvas);
        _drawCurrentPoint = _drawStartPoint;

        DrawingCanvas.CaptureMouse();

        if (_vm.CurrentTool == DrawToolType.Text)
        {
            BeginTextInput(_drawStartPoint);
            return;
        }

        _vm.BeginStroke(_drawStartPoint);
        CreatePreviewShape();
    }

    private void DrawingCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        // 更新状态栏坐标
        var pos = e.GetPosition(DrawingCanvas);
        TbCoordinate.Text = $"坐标: {(int)pos.X}, {(int)pos.Y}";

        if (!_isDrawing) return;

        _drawCurrentPoint = pos;
        _vm.ContinueStroke(_drawCurrentPoint);

        UpdatePreviewShape();
    }

    private void DrawingCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDrawing) return;
        _isDrawing = false;
        DrawingCanvas.ReleaseMouseCapture();

        _drawCurrentPoint = e.GetPosition(DrawingCanvas);

        // 构建最终绘制元素并提交到 ViewModel
        var element = BuildDrawElement();
        if (element != null)
        {
            _vm.CommitStroke(element);
        }

        // 清除预览形状
        ClearPreviewShape();
    }

    private void DrawingCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 右键取消当前绘制
        if (_isDrawing)
        {
            _isDrawing = false;
            DrawingCanvas.ReleaseMouseCapture();
            ClearPreviewShape();
        }
    }

    // ===== 预览形状管理 =====

    private void CreatePreviewShape()
    {
        ClearPreviewShape();

        var strokeBrush = new SolidColorBrush(_vm.StrokeColor);
        double strokeWidth = _vm.StrokeWidth;

        _previewShape = _vm.CurrentTool switch
        {
            DrawToolType.Pen or DrawToolType.Eraser =>
                CreatePolyline(strokeBrush, strokeWidth),
            DrawToolType.Line =>
                new Line { Stroke = strokeBrush, StrokeThickness = strokeWidth, IsHitTestVisible = false },
            DrawToolType.Rectangle =>
                new Rectangle { Stroke = strokeBrush, StrokeThickness = strokeWidth, Fill = Brushes.Transparent, IsHitTestVisible = false },
            DrawToolType.Ellipse =>
                new Ellipse { Stroke = strokeBrush, StrokeThickness = strokeWidth, Fill = Brushes.Transparent, IsHitTestVisible = false },
            DrawToolType.Arrow =>
                null, // 箭头通过 Polyline 模拟
            _ => null
        };

        if (_previewShape != null)
            DrawingCanvas.Children.Add(_previewShape);
    }

    private Polyline CreatePolyline(Brush stroke, double width)
    {
        return new Polyline
        {
            Stroke = stroke,
            StrokeThickness = width,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
    }

    private void UpdatePreviewShape()
    {
        if (_previewShape == null) return;

        var start = _drawStartPoint;
        var cur = _drawCurrentPoint;

        switch (_vm.CurrentTool)
        {
            case DrawToolType.Pen or DrawToolType.Eraser:
                if (_previewShape is Polyline pl)
                {
                    pl.Points.Add(cur);
                }
                break;

            case DrawToolType.Line:
                if (_previewShape is Line line)
                {
                    line.X1 = start.X; line.Y1 = start.Y;
                    line.X2 = cur.X; line.Y2 = cur.Y;
                }
                break;

            case DrawToolType.Rectangle:
                if (_previewShape is Rectangle rect)
                {
                    var r = NormalizeRect(start, cur);
                    Canvas.SetLeft(rect, r.Left);
                    Canvas.SetTop(rect, r.Top);
                    rect.Width = r.Width;
                    rect.Height = r.Height;
                    if (_vm.IsFilled)
                        rect.Fill = new SolidColorBrush(_vm.StrokeColor) { Opacity = 0.3 };
                }
                break;

            case DrawToolType.Ellipse:
                if (_previewShape is Ellipse ell)
                {
                    var r = NormalizeRect(start, cur);
                    Canvas.SetLeft(ell, r.Left);
                    Canvas.SetTop(ell, r.Top);
                    ell.Width = r.Width;
                    ell.Height = r.Height;
                    if (_vm.IsFilled)
                        ell.Fill = new SolidColorBrush(_vm.StrokeColor) { Opacity = 0.3 };
                }
                break;
        }
    }

    private void ClearPreviewShape()
    {
        if (_previewShape != null)
        {
            DrawingCanvas.Children.Remove(_previewShape);
            _previewShape = null;
        }
    }

    // ===== 文字输入 =====

    private void BeginTextInput(System.Windows.Point position)
    {
        // 清理旧的输入框
        FinishTextInput(cancel: true);

        _textInput = new TextBox
        {
            Background = new SolidColorBrush(Color.FromArgb(180, 20, 20, 30)),
            Foreground = new SolidColorBrush(_vm.StrokeColor),
            BorderBrush = new SolidColorBrush(_vm.StrokeColor),
            BorderThickness = new Thickness(1),
            FontSize = _vm.FontSize,
            CaretBrush = new SolidColorBrush(_vm.StrokeColor),
            MinWidth = 80,
            AcceptsReturn = false,
            Padding = new Thickness(4, 2, 4, 2)
        };

        Canvas.SetLeft(_textInput, position.X);
        Canvas.SetTop(_textInput, position.Y);
        DrawingCanvas.Children.Add(_textInput);

        _textInput.KeyDown += TextInput_KeyDown;
        _textInput.LostFocus += (_, _) => FinishTextInput();

        _textInput.Focus();
        _isDrawing = false; // 文字输入时不需要绘制追踪
    }

    private void TextInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            FinishTextInput();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            FinishTextInput(cancel: true);
            e.Handled = true;
        }
    }

    private void FinishTextInput(bool cancel = false)
    {
        if (_textInput == null) return;

        if (!cancel && !string.IsNullOrWhiteSpace(_textInput.Text))
        {
            double x = Canvas.GetLeft(_textInput);
            double y = Canvas.GetTop(_textInput) + _vm.FontSize; // 加上字体高度偏移

            var element = new TextDrawElement
            {
                ToolType = DrawToolType.Text,
                Text = _textInput.Text,
                Position = new System.Windows.Point(x, y),
                FontSize = _vm.FontSize,
                TextColor = _vm.StrokeColor,
                StrokeColor = _vm.StrokeColor,
                StrokeWidth = _vm.StrokeWidth
            };
            _vm.CommitStroke(element);
        }

        DrawingCanvas.Children.Remove(_textInput);
        _textInput = null;
    }

    // ===== 颜色预选框点击 =====

    private void CurrentColorPreview_Click(object sender, MouseButtonEventArgs e)
    {
        // 使用系统颜色选择器
        // WPF 没有内置颜色对话框，简单弹出提示
        // 实际颜色通过调色板按钮选择
    }

    // ===== 滚轮缩放 =====

    private void CanvasScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
        {
            if (e.Delta > 0)
                _vm.ZoomInCommand.Execute(null);
            else
                _vm.ZoomOutCommand.Execute(null);

            e.Handled = true;
        }
    }

    // ===== 工具快捷键 =====

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_textInput != null) return; // 文字输入时忽略工具切换

        switch (e.Key)
        {
            case Key.V: ActivateTool(DrawToolType.Select, BtnSelect); break;
            case Key.B: ActivateTool(DrawToolType.Pen, BtnPen); break;
            case Key.L: ActivateTool(DrawToolType.Line, BtnLine); break;
            case Key.R: ActivateTool(DrawToolType.Rectangle, BtnRect); break;
            case Key.E: ActivateTool(DrawToolType.Ellipse, BtnEllipse); break;
            case Key.A: ActivateTool(DrawToolType.Arrow, BtnArrow); break;
            case Key.T: ActivateTool(DrawToolType.Text, BtnText); break;
            case Key.X: ActivateTool(DrawToolType.Eraser, BtnEraser); break;
        }
    }

    private void ActivateTool(DrawToolType tool, Button btn)
    {
        _vm.SelectToolCommand.Execute(tool);
        UpdateActiveToolButton(btn);
    }

    // ===== 辅助方法 =====

    private void OnRenderRequested()
    {
        // DrawingCanvas 的实时预览图形已通过 UpdatePreviewShape 实时更新
        // 提交后 DisplayImage 绑定会自动更新 BaseImage
    }

    private void UpdateImageSizeStatus()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_vm.DisplayImage != null)
            {
                TbImageSize.Text = $"大小: {_vm.DisplayImage.PixelWidth} × {_vm.DisplayImage.PixelHeight}";
                // 更新 Canvas 尺寸以匹配图片
                DrawingCanvas.Width = _vm.DisplayImage.PixelWidth;
                DrawingCanvas.Height = _vm.DisplayImage.PixelHeight;
            }
        });
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(_vm.DisplayImage) && _vm.DisplayImage != null)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    TbImageSize.Text = $"大小: {_vm.DisplayImage.PixelWidth} × {_vm.DisplayImage.PixelHeight}";
                    DrawingCanvas.Width = _vm.DisplayImage.PixelWidth;
                    DrawingCanvas.Height = _vm.DisplayImage.PixelHeight;
                });
            }
        };
    }

    /// <summary>构建绘制完成的元素</summary>
    private DrawElement? BuildDrawElement()
    {
        var strokeColor = _vm.StrokeColor;
        var strokeWidth = _vm.StrokeWidth;

        switch (_vm.CurrentTool)
        {
            case DrawToolType.Pen:
            case DrawToolType.Eraser:
                {
                    var path = new PathDrawElement
                    {
                        ToolType = _vm.CurrentTool,
                        StrokeColor = strokeColor,
                        StrokeWidth = _vm.CurrentTool == DrawToolType.Eraser ? strokeWidth * 3 : strokeWidth,
                        FillColor = _vm.CurrentTool == DrawToolType.Eraser
                            ? Colors.Transparent : strokeColor // 橡皮擦用白色
                    };
                    path.Points.AddRange(_vm.CurrentStrokePoints);
                    if (path.Points.Count < 2) return null;
                    return path;
                }

            case DrawToolType.Line:
                {
                    var path = new PathDrawElement
                    {
                        ToolType = DrawToolType.Line,
                        StrokeColor = strokeColor,
                        StrokeWidth = strokeWidth
                    };
                    path.Points.Add(_drawStartPoint);
                    path.Points.Add(_drawCurrentPoint);
                    return path;
                }

            case DrawToolType.Rectangle:
                {
                    var r = NormalizeRect(_drawStartPoint, _drawCurrentPoint);
                    if (r.Width < 2 || r.Height < 2) return null;
                    return new ShapeDrawElement
                    {
                        ToolType = DrawToolType.Rectangle,
                        Bounds = r,
                        StrokeColor = strokeColor,
                        StrokeWidth = strokeWidth,
                        IsFilled = _vm.IsFilled,
                        FillColor = Color.FromArgb(80,
                            strokeColor.R, strokeColor.G, strokeColor.B)
                    };
                }

            case DrawToolType.Ellipse:
                {
                    var r = NormalizeRect(_drawStartPoint, _drawCurrentPoint);
                    if (r.Width < 2 || r.Height < 2) return null;
                    return new ShapeDrawElement
                    {
                        ToolType = DrawToolType.Ellipse,
                        Bounds = r,
                        StrokeColor = strokeColor,
                        StrokeWidth = strokeWidth,
                        IsFilled = _vm.IsFilled,
                        FillColor = Color.FromArgb(80,
                            strokeColor.R, strokeColor.G, strokeColor.B)
                    };
                }

            case DrawToolType.Arrow:
                {
                    return new ArrowDrawElement
                    {
                        ToolType = DrawToolType.Arrow,
                        Start = _drawStartPoint,
                        End = _drawCurrentPoint,
                        StrokeColor = strokeColor,
                        StrokeWidth = strokeWidth
                    };
                }

            default:
                return null;
        }
    }

    private static Rect NormalizeRect(System.Windows.Point p1, System.Windows.Point p2)
    {
        return new Rect(
            Math.Min(p1.X, p2.X),
            Math.Min(p1.Y, p2.Y),
            Math.Abs(p2.X - p1.X),
            Math.Abs(p2.Y - p1.Y));
    }
}
