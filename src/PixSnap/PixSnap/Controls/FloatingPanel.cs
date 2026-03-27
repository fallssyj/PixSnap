using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PixSnap.Controls;

/// <summary>可拖拽的浮动面板，内置拖拽逻辑，无需外部事件处理。</summary>
public class FloatingPanel : ContentControl
{
    private readonly TranslateTransform _translate = new();
    private bool _isDragging;
    private Point _dragStart;
    private Point _transformStart;

    public FloatingPanel()
    {
        RenderTransform = _translate;
    }

    /// <summary>将面板拖拽偏移重置为 (0,0)。</summary>
    public void ResetPosition()
    {
        _translate.X = 0;
        _translate.Y = 0;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _isDragging = true;
        _dragStart = e.GetPosition(Window.GetWindow(this));
        _transformStart = new Point(_translate.X, _translate.Y);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition(Window.GetWindow(this));
        var delta = pos - _dragStart;
        _translate.X = _transformStart.X + delta.X;
        _translate.Y = _transformStart.Y + delta.Y;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        ReleaseMouseCapture();
    }
}
