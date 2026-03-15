// 编码：UTF-8 BOM
// 图片编辑器 ViewModel，管理画板绘制、图片处理操作和历史记录

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSnap.Models;
using PixSnap.Services;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
// 消除 WPF 与 WinForms 命名空间冲突
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using MessageBox = System.Windows.MessageBox;

namespace PixSnap.ViewModels;

/// <summary>图片编辑器 ViewModel</summary>
public partial class ImageEditorViewModel : ObservableObject
{
    // ===== 图片相关状态 =====

    /// <summary>原始截图（不含绘制内容）</summary>
    private SKBitmap? _originalBitmap;

    /// <summary>当前显示的合成图（原图 + 历史绘制层）</summary>
    [ObservableProperty]
    private BitmapSource? _displayImage;

    /// <summary>图像视觉缩放比例（仅影响显示，不修改像素）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    private double _zoomScale = 1.0;

    public string ZoomText => $"{(int)(_zoomScale * 100)}%";

    // ===== 工具状态 =====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDrawingTool))]
    private DrawToolType _currentTool = DrawToolType.Pen;

    public bool IsDrawingTool => CurrentTool != DrawToolType.Select;

    [ObservableProperty]
    private Color _strokeColor = Colors.Red;

    [ObservableProperty]
    private double _strokeWidth = 3.0;

    [ObservableProperty]
    private double _fontSize = 16.0;

    [ObservableProperty]
    private bool _isFilled = false;

    // ===== 图片处理状态 =====

    [ObservableProperty]
    private int _insetValue = 0;

    [ObservableProperty]
    private float _cornerRadius = 0f;

    // ===== 调色板颜色 =====
    public ObservableCollection<Color> PaletteColors { get; } = new()
    {
        Colors.Red, Colors.OrangeRed, Colors.Orange, Colors.Yellow,
        Colors.LimeGreen, Colors.Cyan, Colors.DodgerBlue, Colors.MediumPurple,
        Colors.HotPink, Colors.White, Colors.LightGray, Colors.Black
    };

    // ===== 撤销/重做历史 =====

    /// <summary>撤销栈：每个元素是绘制操作完成后的 SKBitmap 快照</summary>
    private readonly Stack<SKBitmap> _undoStack = new();
    /// <summary>重做栈</summary>
    private readonly Stack<SKBitmap> _redoStack = new();

    [ObservableProperty]
    private bool _canUndo = false;

    [ObservableProperty]
    private bool _canRedo = false;

    // ===== 当前正在绘制的临时元素（供 View 使用）=====

    /// <summary>当前绘制路径点（画笔工具使用）</summary>
    public List<System.Windows.Point> CurrentStrokePoints { get; } = new();

    /// <summary>通知 View 重绘画布的事件</summary>
    public event Action? RenderRequested;

    // ===== 初始化 =====

    /// <summary>加载截图数据</summary>
    public void LoadBitmap(BitmapSource source)
    {
        _originalBitmap?.Dispose();
        _originalBitmap = ImageProcessService.BitmapSourceToSkBitmap(source);

        // 清空历史
        ClearHistory();
        // 推入初始状态
        PushUndoState(_originalBitmap.Copy());

        RefreshDisplay();
        ZoomScale = 1.0;
    }

    // ===== 工具选择命令 =====

    [RelayCommand]
    private void SelectTool(DrawToolType tool) => CurrentTool = tool;

    [RelayCommand]
    private void SelectColor(Color color) => StrokeColor = color;

    // ===== 缩放命令 =====

    [RelayCommand]
    private void ZoomIn()
    {
        ZoomScale = Math.Min(ZoomScale + 0.25, 5.0);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        ZoomScale = Math.Max(ZoomScale - 0.25, 0.1);
    }

    [RelayCommand]
    private void ZoomReset() => ZoomScale = 1.0;

    // ===== 裁剪命令 =====

    [RelayCommand]
    private void ApplyInsetCrop()
    {
        if (_originalBitmap == null || InsetValue <= 0) return;

        try
        {
            var current = GetCurrentBitmap();
            var cropped = ImageProcessService.InsetCrop(current, InsetValue);
            current.Dispose();
            ReplaceCurrentBitmap(cropped);
            PushUndoState(cropped.Copy());
            RefreshDisplay();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"裁剪失败：{ex.Message}", "PixSnap", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ===== 圆角命令 =====

    [RelayCommand]
    private void ApplyRoundCorners()
    {
        if (_originalBitmap == null || CornerRadius <= 0) return;

        var current = GetCurrentBitmap();
        var rounded = ImageProcessService.ApplyRoundCorners(current, CornerRadius);
        current.Dispose();
        ReplaceCurrentBitmap(rounded);
        PushUndoState(rounded.Copy());
        RefreshDisplay();
    }

    // ===== 撤销/重做 =====

    [RelayCommand]
    private void Undo()
    {
        if (_undoStack.Count <= 1) return; // 保留初始帧

        var current = _undoStack.Pop();
        _redoStack.Push(current);
        ReplaceCurrentBitmap(_undoStack.Peek().Copy());
        RefreshDisplay();
        UpdateHistoryState();
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redoStack.Count == 0) return;

        var next = _redoStack.Pop();
        _undoStack.Push(next);
        ReplaceCurrentBitmap(next.Copy());
        RefreshDisplay();
        UpdateHistoryState();
    }

    // ===== 保存 =====

    [RelayCommand]
    private void Save(string? directory)
    {
        if (_originalBitmap == null) return;

        string saveDir = string.IsNullOrEmpty(directory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            : directory;

        string fileName = ImageProcessService.GenerateFileName(Models.ImageFormat.Png);
        string filePath = System.IO.Path.Combine(saveDir, fileName);

        var current = GetCurrentBitmap();
        ImageProcessService.SaveToFile(current, filePath, Models.ImageFormat.Png);

        HandyControl.Controls.Growl.Success($"已保存到：{filePath}");
    }

    /// <summary>保存为指定格式</summary>
    [RelayCommand]
    private void SaveAs()
    {
        if (_originalBitmap == null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|BMP 图片|*.bmp|WebP 图片|*.webp",
            FileName = ImageProcessService.GenerateFileName(Models.ImageFormat.Png),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };

        if (dialog.ShowDialog() == true)
        {
            var format = System.IO.Path.GetExtension(dialog.FileName).ToLower() switch
            {
                ".jpg" or ".jpeg" => Models.ImageFormat.Jpg,
                ".bmp" => Models.ImageFormat.Bmp,
                ".webp" => Models.ImageFormat.Webp,
                _ => Models.ImageFormat.Png
            };

            var current = GetCurrentBitmap();
            ImageProcessService.SaveToFile(current, dialog.FileName, format);
            HandyControl.Controls.Growl.Success($"已保存到：{dialog.FileName}");
        }
    }

    /// <summary>复制到剪贴板</summary>
    [RelayCommand]
    private void CopyToClipboard()
    {
        if (DisplayImage != null)
            ImageProcessService.CopyToClipboard(DisplayImage);
    }

    // ===== 画板绘制接口（由 View 调用）=====

    /// <summary>开始新的笔画</summary>
    public void BeginStroke(System.Windows.Point point)
    {
        CurrentStrokePoints.Clear();
        CurrentStrokePoints.Add(point);
    }

    /// <summary>持续绘制路径点</summary>
    public void ContinueStroke(System.Windows.Point point)
    {
        CurrentStrokePoints.Add(point);
        RenderRequested?.Invoke();
    }

    /// <summary>
    /// 完成笔画（提交到位图，清空临时路径，保存历史）
    /// 传入的 element 是已完成的绘制元素
    /// </summary>
    public void CommitStroke(DrawElement element)
    {
        if (_originalBitmap == null) return;

        var current = GetCurrentBitmap();
        using var canvas = new SKCanvas(current);

        DrawElementToCanvas(canvas, element);

        CurrentStrokePoints.Clear();
        ReplaceCurrentBitmap(current);
        PushUndoState(current.Copy());
        RefreshDisplay();
    }

    // ===== 私有辅助方法 =====

    /// <summary>当前工作位图（撤销栈顶）</summary>
    private SKBitmap GetCurrentBitmap()
    {
        return _undoStack.Count > 0 ? _undoStack.Peek().Copy() : _originalBitmap!.Copy();
    }

    /// <summary>替换当前工作位图（不入栈，仅更新栈顶）</summary>
    private void ReplaceCurrentBitmap(SKBitmap newBitmap)
    {
        if (_undoStack.Count > 0)
        {
            var old = _undoStack.Pop();
            old.Dispose();
        }
        _undoStack.Push(newBitmap);
        UpdateHistoryState();
    }

    /// <summary>将当前状态推入撤销栈</summary>
    private void PushUndoState(SKBitmap snapshot)
    {
        _undoStack.Push(snapshot);
        _redoStack.Clear(); // 新操作后清空重做栈
        UpdateHistoryState();
    }

    private void UpdateHistoryState()
    {
        CanUndo = _undoStack.Count > 1;
        CanRedo = _redoStack.Count > 0;
    }

    private void ClearHistory()
    {
        while (_undoStack.Count > 0) _undoStack.Pop().Dispose();
        while (_redoStack.Count > 0) _redoStack.Pop().Dispose();
        UpdateHistoryState();
    }

    /// <summary>刷新 DisplayImage 以更新 UI 绑定</summary>
    private void RefreshDisplay()
    {
        if (_undoStack.Count > 0)
        {
            DisplayImage = ImageProcessService.SkBitmapToBitmapSource(_undoStack.Peek());
        }
        RenderRequested?.Invoke();
    }

    /// <summary>将绘制元素渲染到 SKCanvas</summary>
    private void DrawElementToCanvas(SKCanvas canvas, DrawElement element)
    {
        using var paint = CreatePaint(element);

        switch (element)
        {
            case PathDrawElement path when path.Points.Count >= 2:
                {
                    using var skPath = new SKPath();
                    skPath.MoveTo((float)path.Points[0].X, (float)path.Points[0].Y);
                    for (int i = 1; i < path.Points.Count; i++)
                        skPath.LineTo((float)path.Points[i].X, (float)path.Points[i].Y);

                    if (element.ToolType == DrawToolType.Pen)
                    {
                        paint.Style = SKPaintStyle.Stroke;
                        canvas.DrawPath(skPath, paint);
                    }
                    else if (element.ToolType == DrawToolType.Line && path.Points.Count >= 2)
                    {
                        var first = path.Points.First();
                        var last = path.Points.Last();
                        canvas.DrawLine((float)first.X, (float)first.Y, (float)last.X, (float)last.Y, paint);
                    }
                    break;
                }

            case ShapeDrawElement shape:
                {
                    var r = new SKRect(
                        (float)shape.Bounds.Left, (float)shape.Bounds.Top,
                        (float)shape.Bounds.Right, (float)shape.Bounds.Bottom);

                    if (element.ToolType == DrawToolType.Rectangle)
                    {
                        if (element.IsFilled)
                        {
                            paint.Style = SKPaintStyle.Fill;
                            paint.Color = new SKColor(element.FillColor.R, element.FillColor.G, element.FillColor.B, element.FillColor.A);
                            canvas.DrawRect(r, paint);
                            paint.Style = SKPaintStyle.Stroke;
                            paint.Color = new SKColor(element.StrokeColor.R, element.StrokeColor.G, element.StrokeColor.B, element.StrokeColor.A);
                        }
                        canvas.DrawRect(r, paint);
                    }
                    else if (element.ToolType == DrawToolType.Ellipse)
                    {
                        if (element.IsFilled)
                        {
                            paint.Style = SKPaintStyle.Fill;
                            paint.Color = new SKColor(element.FillColor.R, element.FillColor.G, element.FillColor.B, element.FillColor.A);
                            canvas.DrawOval(r, paint);
                            paint.Style = SKPaintStyle.Stroke;
                            paint.Color = new SKColor(element.StrokeColor.R, element.StrokeColor.G, element.StrokeColor.B, element.StrokeColor.A);
                        }
                        canvas.DrawOval(r, paint);
                    }
                    break;
                }

            case ArrowDrawElement arrow:
                {
                    DrawArrow(canvas, paint,
                        (float)arrow.Start.X, (float)arrow.Start.Y,
                        (float)arrow.End.X, (float)arrow.End.Y);
                    break;
                }

            case TextDrawElement text when !string.IsNullOrEmpty(text.Text):
                {
                    using var textFont = new SKFont
                    {
                        Size = (float)text.FontSize
                    };
                    using var textPaint = new SKPaint
                    {
                        Color = new SKColor(text.TextColor.R, text.TextColor.G, text.TextColor.B, text.TextColor.A),
                        IsAntialias = true
                    };
                    canvas.DrawText(text.Text, (float)text.Position.X, (float)text.Position.Y, SKTextAlign.Left, textFont, textPaint);
                    break;
                }
        }
    }

    /// <summary>创建画笔（SKPaint）</summary>
    private SKPaint CreatePaint(DrawElement element)
    {
        return new SKPaint
        {
            Color = new SKColor(element.StrokeColor.R, element.StrokeColor.G, element.StrokeColor.B, element.StrokeColor.A),
            StrokeWidth = (float)element.StrokeWidth,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };
    }

    /// <summary>绘制带箭头的直线</summary>
    private static void DrawArrow(SKCanvas canvas, SKPaint paint, float x1, float y1, float x2, float y2)
    {
        canvas.DrawLine(x1, y1, x2, y2, paint);

        // 计算箭头头部
        float arrowLen = Math.Max(paint.StrokeWidth * 4, 12);
        float angle = MathF.Atan2(y2 - y1, x2 - x1);
        float arrowAngle = MathF.PI / 6; // 30 度

        float ax1 = x2 - arrowLen * MathF.Cos(angle - arrowAngle);
        float ay1 = y2 - arrowLen * MathF.Sin(angle - arrowAngle);
        float ax2 = x2 - arrowLen * MathF.Cos(angle + arrowAngle);
        float ay2 = y2 - arrowLen * MathF.Sin(angle + arrowAngle);

        using var arrowPath = new SKPath();
        arrowPath.MoveTo(ax1, ay1);
        arrowPath.LineTo(x2, y2);
        arrowPath.LineTo(ax2, ay2);

        using var fillPaint = paint.Clone();
        fillPaint.Style = SKPaintStyle.Fill;
        canvas.DrawPath(arrowPath, fillPaint);
    }
}
