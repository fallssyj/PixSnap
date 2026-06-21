using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PixSnap.Controls;

/// <summary>可拖拽的浮动工具面板：仅标题栏可拖动，带统一视觉样式。</summary>
public class FloatingPanel : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(FloatingPanel), new PropertyMetadata(string.Empty));

    private readonly TranslateTransform _translate = new();
    private bool _isDragging;
    private Point _dragStart;
    private Point _transformStart;

    static FloatingPanel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(FloatingPanel),
            new FrameworkPropertyMetadata(typeof(FloatingPanel)));
    }

    public FloatingPanel()
    {
        RenderTransform = _translate;
        Loaded += (_, _) => ResetPositionIfVisible();
        IsVisibleChanged += (_, _) => ResetPositionIfVisible();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>将面板拖拽偏移重置为锚点 (0,0)。</summary>
    public void ResetPosition()
    {
        _translate.X = 0;
        _translate.Y = 0;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            !IsDescendantOfDragHandle(source))
        {
            base.OnMouseLeftButtonDown(e);
            return;
        }

        _isDragging = true;
        _dragStart = e.GetPosition(Window.GetWindow(this));
        _transformStart = new Point(_translate.X, _translate.Y);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_isDragging)
        {
            base.OnMouseMove(e);
            return;
        }

        var pos = e.GetPosition(Window.GetWindow(this));
        var delta = pos - _dragStart;
        _translate.X = _transformStart.X + delta.X;
        _translate.Y = _transformStart.Y + delta.Y;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            base.OnMouseLeftButtonUp(e);
            return;
        }

        _isDragging = false;
        ReleaseMouseCapture();
    }

    private void ResetPositionIfVisible()
    {
        if (IsVisible)
            ResetPosition();
    }

    private bool IsDescendantOfDragHandle(DependencyObject source)
    {
        while (source is not null and not FloatingPanel)
        {
            if (source is FrameworkElement { Name: "PART_DragHandle" })
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
