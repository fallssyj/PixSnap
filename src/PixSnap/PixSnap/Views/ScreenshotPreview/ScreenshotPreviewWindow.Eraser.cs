using PixSnap.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PixSnap.Views;

public partial class ScreenshotPreviewWindow
{
    // ══════════════════════════════════════════════════════════════════════════
    // AI 擦除画布交互
    // ══════════════════════════════════════════════════════════════════════════

    #region 擦除字段

    private bool _isEraserDrawing;
    private Point _lastEraserCanvasPoint;
    private bool _hasLastEraserCanvasPoint;
    private Polyline? _currentEraserStrokeVisual;

    #endregion

    private void UpdateEraserCanvasState()
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;
        var active = vm.IsEraserMode;

        EraserCanvas.IsHitTestVisible = active;

        if (active)
            UpdateEraserCanvasSize(vm);
        else
        {
            // 退出时隐藏光标指示器、清除视觉笔画
            EraserCursorIndicator.Visibility = Visibility.Collapsed;
            ClearEraserVisualStrokes();
        }
    }

    private void UpdateEraserCanvasSize(ScreenshotPreviewViewModel vm)
    {
        if (vm.ScreenshotImage is not null)
        {
            EraserCanvas.Width = vm.ScreenshotImage.Width;
            EraserCanvas.Height = vm.ScreenshotImage.Height;
        }
    }

    private void UpdateEraserCursorIndicator(Point canvasPos)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;
        // BrushSize 直接作为屏幕显示半径使用
        double displayR = vm.EraserPanel.BrushSize;
        double diameter = displayR * 2;

        EraserCursorIndicator.Width = diameter;
        EraserCursorIndicator.Height = diameter;
        Canvas.SetLeft(EraserCursorIndicator, canvasPos.X - displayR);
        Canvas.SetTop(EraserCursorIndicator, canvasPos.Y - displayR);
        EraserCursorIndicator.Visibility = Visibility.Visible;
    }

    private void PaintEraserStroke(Point canvasPos)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;
        if (!TryGetImagePixelPoint(canvasPos, out var imgPt)) return;

        // EraserCanvas 在缩放变换 Grid 内，与图片同处 DIP 空间
        // BrushSize 即 DIP 半径，乘 DPI 缩放转为图片像素半径
        double imgPixelRadius = vm.EraserPanel.BrushSize * GetAnnotationDpiScale();

        vm.EraserPanel.Strokes.Add((imgPt, imgPixelRadius));
    }

    private void BeginEraserStrokeVisual(Point startPoint)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;

        _currentEraserStrokeVisual = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromArgb(56, 255, 80, 80)),
            StrokeThickness = Math.Max(2.0, vm.EraserPanel.BrushSize * 2.0),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };
        _currentEraserStrokeVisual.Points.Add(startPoint);
        EraserCanvas.Children.Add(_currentEraserStrokeVisual);
    }

    private void AddEraserStrokeVisualPoint(Point point)
    {
        _currentEraserStrokeVisual?.Points.Add(point);
    }

    private void PaintInterpolatedEraserStroke(Point from, Point to)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm) return;

        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        // 算法采样用较小步长，保证遮罩连续
        double step = Math.Max(1.0, vm.EraserPanel.BrushSize * EraserSampleStepRatio);
        int segments = Math.Max(1, (int)Math.Ceiling(distance / step));

        for (int i = 1; i <= segments; i++)
        {
            double t = i / (double)segments;
            var p = new Point(from.X + dx * t, from.Y + dy * t);
            PaintEraserStroke(p);
            AddEraserStrokeVisualPoint(p);
        }
    }

    private void ClearEraserVisualStrokes()
    {
        // 移除所有视觉笔画，保留光标指示器
        for (int i = EraserCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (EraserCanvas.Children[i] != EraserCursorIndicator)
                EraserCanvas.Children.RemoveAt(i);
        }

        _currentEraserStrokeVisual = null;
        _hasLastEraserCanvasPoint = false;
        _isEraserDrawing = false;
    }

    private void OnEraserStrokesCleared()
    {
        Dispatcher.InvokeAsync(ClearEraserVisualStrokes);
    }

    private void OnEraserInpaintApplied(BitmapSource _)
    {
        // ViewModel 已更新 ScreenshotImage；FitZoomFactor 会由 PropertyChanged 触发刷新
        // 此处确保视觉笔画已清空（StrokesCleared 会先于此触发，双重保险）
        Dispatcher.InvokeAsync(ClearEraserVisualStrokes);
    }

    // ── EraserCanvas 鼠标事件 ────────────────────────────────────────────────

    private void EraserCanvas_MouseEnter(object sender, MouseEventArgs e)
    {
        UpdateEraserCursorIndicator(e.GetPosition(EraserCanvas));
    }

    private void EraserCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        EraserCursorIndicator.Visibility = Visibility.Collapsed;
        if (_isEraserDrawing)
        {
            _isEraserDrawing = false;
            EraserCanvas.ReleaseMouseCapture();
        }
        _hasLastEraserCanvasPoint = false;
        _currentEraserStrokeVisual = null;
    }

    private void EraserCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ScreenshotPreviewViewModel vm || !vm.IsEraserMode) return;
        var pos = e.GetPosition(EraserCanvas);
        _isEraserDrawing = true;
        EraserCanvas.CaptureMouse();
        BeginEraserStrokeVisual(pos);
        PaintEraserStroke(pos);
        _lastEraserCanvasPoint = pos;
        _hasLastEraserCanvasPoint = true;
        e.Handled = true;
    }

    private void EraserCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(EraserCanvas);
        UpdateEraserCursorIndicator(pos);

        if (_isEraserDrawing && e.LeftButton == MouseButtonState.Pressed)
        {
            if (_hasLastEraserCanvasPoint)
                PaintInterpolatedEraserStroke(_lastEraserCanvasPoint, pos);
            else
                PaintEraserStroke(pos);

            _lastEraserCanvasPoint = pos;
            _hasLastEraserCanvasPoint = true;
        }
    }

    private void EraserCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isEraserDrawing) return;
        _isEraserDrawing = false;
        _hasLastEraserCanvasPoint = false;
        _currentEraserStrokeVisual = null;
        EraserCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }
}
