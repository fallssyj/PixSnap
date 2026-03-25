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
public enum AnnotationTool { Arrow, Rectangle, Ellipse, Text, Pen }

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
    /// <summary>自由画笔的点列表。</summary>
    public List<Point> PenPoints { get; } = [];
}

/// <summary>
/// 标注绘制面板 ViewModel：管理标注工具选择、颜色和标注列表。
/// 标注坐标使用图片像素空间。
/// </summary>
public partial class AnnotationViewModel : ObservableObject
{
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

    public SolidColorBrush StrokeColorBrush => new(StrokeColor);

    public ObservableCollection<AnnotationItem> Annotations { get; } = [];

    /// <summary>当前正在绘制的临时标注（鼠标尚未松开）。</summary>
    [ObservableProperty]
    private AnnotationItem? _currentAnnotation;

    /// <summary>标注应用完成，携带结果图像。</summary>
    public event Action<BitmapSource>? AnnotationApplied;

    /// <summary>请求取消标注。</summary>
    public event Action? AnnotationCancelled;

    /// <summary>开始一个新标注（鼠标按下时调用）。</summary>
    public void BeginAnnotation(Point imagePoint)
    {
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

    /// <summary>更新当前标注（鼠标移动时调用）。</summary>
    public void UpdateAnnotation(Point imagePoint)
    {
        if (CurrentAnnotation is null) return;

        if (CurrentAnnotation.Tool == AnnotationTool.Pen)
        {
            CurrentAnnotation.PenPoints.Add(imagePoint);
        }
        CurrentAnnotation.End = imagePoint;
    }

    /// <summary>完成当前标注（鼠标松开时调用）。</summary>
    public void EndAnnotation()
    {
        if (CurrentAnnotation is null) return;

        // 如果是文本工具，设置占位文本
        if (CurrentAnnotation.Tool == AnnotationTool.Text)
        {
            CurrentAnnotation.Text = "文本";
        }

        Annotations.Add(CurrentAnnotation);
        CurrentAnnotation = null;
        IsAnnotating = false;
    }

    /// <summary>设置文本标注的内容。</summary>
    public void SetAnnotationText(AnnotationItem item, string text)
    {
        item.Text = text;
    }

    [RelayCommand]
    private void UndoAnnotation()
    {
        if (Annotations.Count > 0)
            Annotations.RemoveAt(Annotations.Count - 1);
    }

    [RelayCommand]
    private void ClearAnnotations()
    {
        Annotations.Clear();
    }

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

    /// <summary>应用所有标注到图像上。</summary>
    public async Task ApplyAnnotationsAsync(BitmapSource originalImage)
    {
        if (Annotations.Count == 0) return;

        Log.Information("应用标注: {Count} 项", Annotations.Count);
        var result = await Task.Run(() => RenderAnnotations(originalImage));
        if (result is not null)
        {
            AnnotationApplied?.Invoke(result);
            Annotations.Clear();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Annotations.Clear();
        AnnotationCancelled?.Invoke();
    }

    private BitmapSource RenderAnnotations(BitmapSource originalImage)
    {
        using var bitmap = SkiaInteropHelper.BitmapSourceToSKBitmap(originalImage);
        using var canvas = new SKCanvas(bitmap);

        foreach (var annotation in Annotations)
        {
            DrawAnnotation(canvas, annotation);
        }

        var result = SkiaInteropHelper.SKBitmapToBitmapSource(bitmap);
        result.Freeze();
        return result;
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

        switch (item.Tool)
        {
            case AnnotationTool.Arrow:
                DrawArrow(canvas, paint, item.Start, item.End);
                break;

            case AnnotationTool.Rectangle:
                var rect = new SKRect(
                    (float)Math.Min(item.Start.X, item.End.X),
                    (float)Math.Min(item.Start.Y, item.End.Y),
                    (float)Math.Max(item.Start.X, item.End.X),
                    (float)Math.Max(item.Start.Y, item.End.Y));
                canvas.DrawRect(rect, paint);
                break;

            case AnnotationTool.Ellipse:
                var ex = (float)(item.Start.X + item.End.X) / 2;
                var ey = (float)(item.Start.Y + item.End.Y) / 2;
                var rx = Math.Abs((float)(item.End.X - item.Start.X)) / 2;
                var ry = Math.Abs((float)(item.End.Y - item.Start.Y)) / 2;
                canvas.DrawOval(ex, ey, rx, ry, paint);
                break;

            case AnnotationTool.Text:
                using (var font = new SKFont { Size = (float)item.FontSize })
                using (var textPaint = new SKPaint
                {
                    Color = color,
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill
                })
                {
                    canvas.DrawText(item.Text, (float)item.Start.X, (float)item.Start.Y + (float)item.FontSize, SKTextAlign.Left, font, textPaint);
                }
                break;

            case AnnotationTool.Pen:
                if (item.PenPoints.Count >= 2)
                {
                    using var path = new SKPath();
                    path.MoveTo((float)item.PenPoints[0].X, (float)item.PenPoints[0].Y);
                    for (int i = 1; i < item.PenPoints.Count; i++)
                        path.LineTo((float)item.PenPoints[i].X, (float)item.PenPoints[i].Y);
                    canvas.DrawPath(path, paint);
                }
                break;
        }
    }

    private static void DrawArrow(SKCanvas canvas, SKPaint paint, Point start, Point end)
    {
        // 画线
        canvas.DrawLine((float)start.X, (float)start.Y, (float)end.X, (float)end.Y, paint);

        // 画箭头
        double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        double arrowLength = Math.Max(12, paint.StrokeWidth * 5);
        double arrowAngle = Math.PI / 6;

        var p1 = new SKPoint(
            (float)(end.X - arrowLength * Math.Cos(angle - arrowAngle)),
            (float)(end.Y - arrowLength * Math.Sin(angle - arrowAngle)));
        var p2 = new SKPoint(
            (float)(end.X - arrowLength * Math.Cos(angle + arrowAngle)),
            (float)(end.Y - arrowLength * Math.Sin(angle + arrowAngle)));

        using var fillPaint = new SKPaint
        {
            Color = paint.Color,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using var path = new SKPath();
        path.MoveTo((float)end.X, (float)end.Y);
        path.LineTo(p1);
        path.LineTo(p2);
        path.Close();
        canvas.DrawPath(path, fillPaint);
    }
}
