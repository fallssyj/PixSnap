using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSnap.Services;
using Serilog;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixSnap.ViewModels;

/// <summary>标注工具类型。</summary>
public enum AnnotationTool { Pointer, Arrow, Rectangle, Ellipse, Text, Pen }

/// <summary>单条标注元素。</summary>
public sealed class AnnotationItem
{
    public AnnotationTool Tool { get; init; }
    public Point Start { get; set; }
    public Point End { get; set; }
    public Color StrokeColor { get; init; } = Colors.Red;
    public double StrokeWidth { get; init; } = 3;
    public string Text { get; set; } = string.Empty;
    public double FontSize { get; init; } = 20;
    public List<Point> PenPoints { get; } = [];
    public Vector Offset { get; set; }
}

// ── 撤销 / 重做 操作定义 ────────────────────────────────────────

internal interface IAnnotationAction
{
    void Undo(ObservableCollection<AnnotationItem> annotations);
    void Redo(ObservableCollection<AnnotationItem> annotations);
}

internal sealed class AddAnnotationAction(AnnotationItem item) : IAnnotationAction
{
    public void Undo(ObservableCollection<AnnotationItem> annotations) => annotations.Remove(item);
    public void Redo(ObservableCollection<AnnotationItem> annotations) => annotations.Add(item);
}

internal sealed class DeleteAnnotationAction(AnnotationItem item, int index) : IAnnotationAction
{
    public void Undo(ObservableCollection<AnnotationItem> annotations) => annotations.Insert(Math.Min(index, annotations.Count), item);
    public void Redo(ObservableCollection<AnnotationItem> annotations) => annotations.Remove(item);
}

internal sealed class MoveAnnotationAction(AnnotationItem item, Vector oldOffset, Vector newOffset) : IAnnotationAction
{
    public void Undo(ObservableCollection<AnnotationItem> annotations) { item.Offset = oldOffset; }
    public void Redo(ObservableCollection<AnnotationItem> annotations) { item.Offset = newOffset; }
}

/// <summary>
/// 标注绘制面板 ViewModel：管理标注工具选择、颜色、标注列表及撤销/重做。
/// 标注坐标使用图片像素空间。
/// </summary>
public partial class AnnotationViewModel : ObservableObject
{
    private readonly Stack<IAnnotationAction> _undoStack = new();
    private readonly Stack<IAnnotationAction> _redoStack = new();

    [ObservableProperty]
    private AnnotationTool _selectedTool = AnnotationTool.Arrow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrokeColorBrush))]
    private Color _strokeColor = Colors.Red;

    [ObservableProperty]
    private double _strokeWidth = 3;

    [ObservableProperty]
    private double _fontSize = 20;

    [ObservableProperty]
    private bool _isAnnotating;

    [ObservableProperty]
    private AnnotationItem? _selectedAnnotation;

    public SolidColorBrush StrokeColorBrush => new(StrokeColor);

    public ObservableCollection<AnnotationItem> Annotations { get; } = [];

    [ObservableProperty]
    private AnnotationItem? _currentAnnotation;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public event Action<BitmapSource>? AnnotationApplied;
    public event Action? AnnotationCancelled;
    /// <summary>请求 View 层刷新标注 overlay。</summary>
    public event Action? RequestRedraw;

    // ── 操作栈管理 ─────────────────────────────────────────

    private void PushAction(IAnnotationAction action)
    {
        action.Redo(Annotations);
        _undoStack.Push(action);
        _redoStack.Clear();
        NotifyUndoRedoChanged();
    }

    private void NotifyUndoRedoChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    // ── 标注生命周期 ───────────────────────────────────────

    public void BeginAnnotation(Point imagePoint)
    {
        SelectedAnnotation = null;
        CurrentAnnotation = new AnnotationItem
        {
            Tool = SelectedTool,
            Start = imagePoint,
            End = imagePoint,
            StrokeColor = StrokeColor,
            StrokeWidth = StrokeWidth,
            FontSize = FontSize
        };
        IsAnnotating = true;
    }

    public void UpdateAnnotation(Point imagePoint)
    {
        if (CurrentAnnotation is null) return;
        if (CurrentAnnotation.Tool == AnnotationTool.Pen)
            CurrentAnnotation.PenPoints.Add(imagePoint);
        CurrentAnnotation.End = imagePoint;
    }

    public void EndAnnotation()
    {
        if (CurrentAnnotation is null) return;

        if (CurrentAnnotation.Tool == AnnotationTool.Text)
        {
            IsAnnotating = false;
            return;
        }

        var item = CurrentAnnotation;
        CurrentAnnotation = null;
        IsAnnotating = false;

        // 通过 Action 栈添加
        var action = new AddAnnotationAction(item);
        _undoStack.Push(action);
        _redoStack.Clear();
        Annotations.Add(item);
        NotifyUndoRedoChanged();
    }

    public void CommitTextAnnotation(string text)
    {
        if (CurrentAnnotation is null || CurrentAnnotation.Tool != AnnotationTool.Text) return;

        if (string.IsNullOrWhiteSpace(text))
        {
            CurrentAnnotation = null;
            return;
        }
        var item = CurrentAnnotation;
        item.Text = text;
        CurrentAnnotation = null;

        var action = new AddAnnotationAction(item);
        _undoStack.Push(action);
        _redoStack.Clear();
        Annotations.Add(item);
        NotifyUndoRedoChanged();
    }

    /// <summary>拖拽完成后提交移动操作到撤销栈。</summary>
    public void CommitMove(AnnotationItem item, Vector previousOffset)
    {
        if (item.Offset == previousOffset) return;
        var action = new MoveAnnotationAction(item, previousOffset, item.Offset);
        _undoStack.Push(action);
        _redoStack.Clear();
        NotifyUndoRedoChanged();
    }

    // ── 碰撞检测 ───────────────────────────────────────────

    public AnnotationItem? HitTest(Point imagePoint, double tolerancePx = 8)
    {
        for (int i = Annotations.Count - 1; i >= 0; i--)
        {
            if (HitTestItem(Annotations[i], imagePoint, tolerancePx))
                return Annotations[i];
        }
        return null;
    }

    private static bool HitTestItem(AnnotationItem item, Point pt, double tol)
    {
        var ox = item.Offset.X;
        var oy = item.Offset.Y;
        double sx = item.Start.X + ox, sy = item.Start.Y + oy;
        double ex = item.End.X + ox, ey = item.End.Y + oy;

        switch (item.Tool)
        {
            case AnnotationTool.Arrow:
                return DistanceToSegment(pt, new Point(sx, sy), new Point(ex, ey)) <= tol + item.StrokeWidth;
            case AnnotationTool.Rectangle:
            case AnnotationTool.Ellipse:
                var bounds = new Rect(new Point(Math.Min(sx, ex), Math.Min(sy, ey)),
                                     new Point(Math.Max(sx, ex), Math.Max(sy, ey)));
                bounds.Inflate(tol, tol);
                return bounds.Contains(pt);
            case AnnotationTool.Text:
                var textBounds = new Rect(sx, sy,
                    Math.Max(Math.Abs(ex - sx), item.FontSize * Math.Max(1, item.Text.Length) * 0.6),
                    item.FontSize * 1.4);
                textBounds.Inflate(tol, tol);
                return textBounds.Contains(pt);
            case AnnotationTool.Pen:
                foreach (var p in item.PenPoints)
                    if ((pt - new Point(p.X + ox, p.Y + oy)).Length <= tol + item.StrokeWidth) return true;
                return false;
            default:
                return false;
        }
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        var ap = p - a;
        double t = (ab.X * ap.X + ab.Y * ap.Y) / Math.Max(1e-9, ab.X * ab.X + ab.Y * ab.Y);
        t = Math.Clamp(t, 0, 1);
        var closest = new Point(a.X + ab.X * t, a.Y + ab.Y * t);
        return (p - closest).Length;
    }

    // ── 命令 ───────────────────────────────────────────────

    [RelayCommand]
    private void UndoAnnotation()
    {
        if (_undoStack.Count == 0) return;
        var action = _undoStack.Pop();
        action.Undo(Annotations);
        _redoStack.Push(action);

        // 选中如果被撤销则清空
        if (SelectedAnnotation is not null && !Annotations.Contains(SelectedAnnotation))
            SelectedAnnotation = null;

        NotifyUndoRedoChanged();
        RequestRedraw?.Invoke();
    }

    [RelayCommand]
    private void RedoAnnotation()
    {
        if (_redoStack.Count == 0) return;
        var action = _redoStack.Pop();
        action.Redo(Annotations);
        _undoStack.Push(action);
        NotifyUndoRedoChanged();
        RequestRedraw?.Invoke();
    }

    [RelayCommand]
    private void DeleteSelectedAnnotation()
    {
        if (SelectedAnnotation is null) return;
        var item = SelectedAnnotation;
        var index = Annotations.IndexOf(item);
        if (index < 0) return;

        SelectedAnnotation = null;
        var action = new DeleteAnnotationAction(item, index);
        _undoStack.Push(action);
        _redoStack.Clear();
        Annotations.Remove(item);
        NotifyUndoRedoChanged();
        RequestRedraw?.Invoke();
    }

    [RelayCommand]
    private void ClearAnnotations()
    {
        Annotations.Clear();
        _undoStack.Clear();
        _redoStack.Clear();
        SelectedAnnotation = null;
        NotifyUndoRedoChanged();
    }

    [RelayCommand]
    private void SelectPointer() => SelectedTool = AnnotationTool.Pointer;
    [RelayCommand]
    private void SelectArrow() => SelectedTool = AnnotationTool.Arrow;
    [RelayCommand]
    private void SelectRectangle() => SelectedTool = AnnotationTool.Rectangle;
    [RelayCommand]
    private void SelectEllipse() => SelectedTool = AnnotationTool.Ellipse;
    [RelayCommand]
    private void SelectText() => SelectedTool = AnnotationTool.Text;
    [RelayCommand]
    private void SelectPen() => SelectedTool = AnnotationTool.Pen;

    [RelayCommand]
    private void SetColorRed() => StrokeColor = Colors.Red;
    [RelayCommand]
    private void SetColorBlue() => StrokeColor = Color.FromRgb(0x33, 0x99, 0xFF);
    [RelayCommand]
    private void SetColorGreen() => StrokeColor = Color.FromRgb(0x22, 0xCC, 0x55);
    [RelayCommand]
    private void SetColorYellow() => StrokeColor = Color.FromRgb(0xFF, 0xCC, 0x00);
    [RelayCommand]
    private void SetColorWhite() => StrokeColor = Colors.White;
    [RelayCommand]
    private void SetColorBlack() => StrokeColor = Colors.Black;

    // ── 应用标注到图像 ─────────────────────────────────────

    public async Task ApplyAnnotationsAsync(BitmapSource originalImage)
    {
        if (Annotations.Count == 0) return;

        Log.Information("应用标注: {Count} 项", Annotations.Count);
        var result = await Task.Run(() => RenderAnnotations(originalImage));
        if (result is not null)
        {
            AnnotationApplied?.Invoke(result);
            Annotations.Clear();
            _undoStack.Clear();
            _redoStack.Clear();
            SelectedAnnotation = null;
            NotifyUndoRedoChanged();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Annotations.Clear();
        _undoStack.Clear();
        _redoStack.Clear();
        SelectedAnnotation = null;
        NotifyUndoRedoChanged();
        AnnotationCancelled?.Invoke();
    }

    private BitmapSource RenderAnnotations(BitmapSource originalImage)
    {
        using var bitmap = SkiaInteropHelper.BitmapSourceToSKBitmap(originalImage);
        using var canvas = new SKCanvas(bitmap);

        foreach (var annotation in Annotations)
            DrawAnnotation(canvas, annotation);

        // 保留原图 DPI，避免 Stretch="None" 显示尺寸变化
        int w = bitmap.Width, h = bitmap.Height;
        var wb = new System.Windows.Media.Imaging.WriteableBitmap(
            w, h, originalImage.DpiX, originalImage.DpiY,
            System.Windows.Media.PixelFormats.Bgra32, null);
        wb.Lock();
        try
        {
            unsafe
            {
                Buffer.MemoryCopy(
                    (void*)bitmap.GetPixels(), (void*)wb.BackBuffer,
                    (long)h * w * 4, (long)h * w * 4);
            }
            wb.AddDirtyRect(new System.Windows.Int32Rect(0, 0, w, h));
        }
        finally { wb.Unlock(); }
        wb.Freeze();
        return wb;
    }

    private static void DrawAnnotation(SKCanvas canvas, AnnotationItem item)
    {
        var color = new SKColor(item.StrokeColor.R, item.StrokeColor.G, item.StrokeColor.B, item.StrokeColor.A);
        using var paint = new SKPaint
        {
            Color = color,
            StrokeWidth = (float)item.StrokeWidth,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        float ox = (float)item.Offset.X, oy = (float)item.Offset.Y;
        var start = new Point(item.Start.X + ox, item.Start.Y + oy);
        var end = new Point(item.End.X + ox, item.End.Y + oy);

        switch (item.Tool)
        {
            case AnnotationTool.Arrow:
                DrawArrow(canvas, paint, start, end);
                break;

            case AnnotationTool.Rectangle:
                var rect = new SKRect(
                    (float)Math.Min(start.X, end.X), (float)Math.Min(start.Y, end.Y),
                    (float)Math.Max(start.X, end.X), (float)Math.Max(start.Y, end.Y));
                canvas.DrawRect(rect, paint);
                break;

            case AnnotationTool.Ellipse:
                var ecx = (float)(start.X + end.X) / 2;
                var ecy = (float)(start.Y + end.Y) / 2;
                var rx = Math.Abs((float)(end.X - start.X)) / 2;
                var ry = Math.Abs((float)(end.Y - start.Y)) / 2;
                canvas.DrawOval(ecx, ecy, rx, ry, paint);
                break;

            case AnnotationTool.Text:
                using (var typeface = SKTypeface.FromFamilyName("Microsoft YaHei") ?? SKTypeface.Default)
                using (var font = new SKFont(typeface, (float)item.FontSize))
                using (var textPaint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill })
                {
                    canvas.DrawText(item.Text, (float)start.X, (float)start.Y + (float)item.FontSize, SKTextAlign.Left, font, textPaint);
                }
                break;

            case AnnotationTool.Pen:
                if (item.PenPoints.Count >= 2)
                {
                    using var path = new SKPath();
                    path.MoveTo((float)(item.PenPoints[0].X + ox), (float)(item.PenPoints[0].Y + oy));
                    for (int i = 1; i < item.PenPoints.Count; i++)
                        path.LineTo((float)(item.PenPoints[i].X + ox), (float)(item.PenPoints[i].Y + oy));
                    canvas.DrawPath(path, paint);
                }
                break;
        }
    }

    private static void DrawArrow(SKCanvas canvas, SKPaint paint, Point start, Point end)
    {
        canvas.DrawLine((float)start.X, (float)start.Y, (float)end.X, (float)end.Y, paint);

        double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        double arrowLength = Math.Max(12, paint.StrokeWidth * 5);
        double arrowAngle = Math.PI / 6;

        var p1 = new SKPoint(
            (float)(end.X - arrowLength * Math.Cos(angle - arrowAngle)),
            (float)(end.Y - arrowLength * Math.Sin(angle - arrowAngle)));
        var p2 = new SKPoint(
            (float)(end.X - arrowLength * Math.Cos(angle + arrowAngle)),
            (float)(end.Y - arrowLength * Math.Sin(angle + arrowAngle)));

        using var fillPaint = new SKPaint { Color = paint.Color, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var path = new SKPath();
        path.MoveTo((float)end.X, (float)end.Y);
        path.LineTo(p1);
        path.LineTo(p2);
        path.Close();
        canvas.DrawPath(path, fillPaint);
    }
}
