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
public enum AnnotationTool { Pointer, Arrow, Rectangle, Ellipse, Text, Pen, Blur }

/// <summary>单条标注元素。</summary>
public sealed class AnnotationItem
{
    public AnnotationTool Tool { get; init; }
    public Point Start { get; set; }
    public Point End { get; set; }
    public Color StrokeColor { get; set; } = Colors.Red;
    public double StrokeWidth { get; set; } = 3;
    public string Text { get; set; } = string.Empty;
    public double FontSize { get; set; } = 20;
    public string FontFamily { get; set; } = "Microsoft YaHei";
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public bool IsUnderline { get; set; }
    public bool IsStrikethrough { get; set; }
    public double CornerRadius { get; set; }
    public double BlurRadius { get; set; } = 10;
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

internal sealed class ResizeAnnotationAction(AnnotationItem item, Point oldStart, Point oldEnd, Point newStart, Point newEnd) : IAnnotationAction
{
    public void Undo(ObservableCollection<AnnotationItem> annotations) { item.Start = oldStart; item.End = oldEnd; }
    public void Redo(ObservableCollection<AnnotationItem> annotations) { item.Start = newStart; item.End = newEnd; }
}

internal sealed class EditColorAction(AnnotationItem item, Color oldColor, Color newColor) : IAnnotationAction
{
    public void Undo(ObservableCollection<AnnotationItem> annotations) { item.StrokeColor = oldColor; }
    public void Redo(ObservableCollection<AnnotationItem> annotations) { item.StrokeColor = newColor; }
}

internal sealed class EditStrokeWidthAction(AnnotationItem item, double oldWidth, double newWidth) : IAnnotationAction
{
    public void Undo(ObservableCollection<AnnotationItem> annotations) { item.StrokeWidth = oldWidth; }
    public void Redo(ObservableCollection<AnnotationItem> annotations) { item.StrokeWidth = newWidth; }
}

internal sealed class EditCornerRadiusAction(AnnotationItem item, double oldRadius, double newRadius) : IAnnotationAction
{
    public void Undo(ObservableCollection<AnnotationItem> annotations) { item.CornerRadius = oldRadius; }
    public void Redo(ObservableCollection<AnnotationItem> annotations) { item.CornerRadius = newRadius; }
}

internal sealed class EditBlurRadiusAction(AnnotationItem item, double oldRadius, double newRadius) : IAnnotationAction
{
    public void Undo(ObservableCollection<AnnotationItem> annotations) { item.BlurRadius = oldRadius; }
    public void Redo(ObservableCollection<AnnotationItem> annotations) { item.BlurRadius = newRadius; }
}

internal sealed class EditTextStyleAction(
    AnnotationItem item,
    string oldFamily, double oldSize, bool oldBold, bool oldItalic, bool oldUnderline, bool oldStrikethrough,
    string newFamily, double newSize, bool newBold, bool newItalic, bool newUnderline, bool newStrikethrough) : IAnnotationAction
{
    public void Undo(ObservableCollection<AnnotationItem> annotations)
    {
        item.FontFamily = oldFamily; item.FontSize = oldSize;
        item.IsBold = oldBold; item.IsItalic = oldItalic;
        item.IsUnderline = oldUnderline; item.IsStrikethrough = oldStrikethrough;
    }
    public void Redo(ObservableCollection<AnnotationItem> annotations)
    {
        item.FontFamily = newFamily; item.FontSize = newSize;
        item.IsBold = newBold; item.IsItalic = newItalic;
        item.IsUnderline = newUnderline; item.IsStrikethrough = newStrikethrough;
    }
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
    [NotifyPropertyChangedFor(nameof(IsTextSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsRectangleSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsBlurSettingsVisible))]
    private AnnotationTool _selectedTool = AnnotationTool.Arrow;

    partial void OnSelectedToolChanged(AnnotationTool value)
    {
        var item = SelectedAnnotation;
        CommitTextStyleEdit(item);
        CommitColorEdit(item);
        CommitStrokeWidthEdit(item);
        CommitCornerRadiusEdit(item);
        CommitBlurRadiusEdit(item);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrokeColorBrush))]
    private Color _strokeColor = Colors.Red;

    [ObservableProperty]
    private double _strokeWidth = 3;

    [ObservableProperty]
    private double _cornerRadius;

    [ObservableProperty]
    private double _blurRadius = 10;

    [ObservableProperty]
    private double _fontSize = 20;

    [ObservableProperty]
    private string _fontFamily = "Microsoft YaHei";

    [ObservableProperty]
    private bool _isBold;

    [ObservableProperty]
    private bool _isItalic;

    [ObservableProperty]
    private bool _isUnderline;

    [ObservableProperty]
    private bool _isStrikethrough;

    public string[] FontFamilies { get; } = ["Microsoft YaHei", "SimSun", "SimHei", "KaiTi", "Arial", "Times New Roman", "Consolas"];

    [ObservableProperty]
    private bool _isAnnotating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsRectangleSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsBlurSettingsVisible))]
    private AnnotationItem? _selectedAnnotation;

    /// <summary>文字设置面板是否可见：选中文本工具 或 选中了一个文本标注。</summary>
    public bool IsTextSettingsVisible =>
        SelectedTool == AnnotationTool.Text ||
        (SelectedAnnotation is not null && SelectedAnnotation.Tool == AnnotationTool.Text);

    /// <summary>矩形圆角设置是否可见：选中矩形工具 或 选中了一个矩形标注。</summary>
    public bool IsRectangleSettingsVisible =>
        SelectedTool == AnnotationTool.Rectangle ||
        (SelectedAnnotation is not null && SelectedAnnotation.Tool == AnnotationTool.Rectangle);

    /// <summary>模糊设置是否可见：选中模糊工具 或 选中了一个模糊标注。</summary>
    public bool IsBlurSettingsVisible =>
        SelectedTool == AnnotationTool.Blur ||
        (SelectedAnnotation is not null && SelectedAnnotation.Tool == AnnotationTool.Blur);

    private bool _syncingProperties;

    // ── 选中标注时同步属性到面板 ──────────────────────────

    partial void OnSelectedAnnotationChanged(AnnotationItem? oldValue, AnnotationItem? newValue)
    {
        // 先提交旧选中标注的编辑
        if (oldValue is not null)
        {
            CommitTextStyleEdit(oldValue);
            CommitColorEdit(oldValue);
            CommitStrokeWidthEdit(oldValue);
            CommitCornerRadiusEdit(oldValue);
            CommitBlurRadiusEdit(oldValue);
        }

        SyncPanelFromAnnotation(newValue);
        OnPropertyChanged(nameof(IsTextSettingsVisible));
        OnPropertyChanged(nameof(IsRectangleSettingsVisible));
        OnPropertyChanged(nameof(IsBlurSettingsVisible));
    }

    /// <summary>将标注属性同步到面板（不触发 Apply 回写）。</summary>
    private void SyncPanelFromAnnotation(AnnotationItem? item)
    {
        _syncingProperties = true;
        if (item is not null)
        {
            StrokeColor = item.StrokeColor;
            StrokeWidth = item.StrokeWidth;
            if (item.Tool == AnnotationTool.Rectangle)
                CornerRadius = item.CornerRadius;
            if (item.Tool == AnnotationTool.Blur)
                BlurRadius = item.BlurRadius;
            if (item.Tool == AnnotationTool.Text)
            {
                FontFamily = item.FontFamily;
                FontSize = item.FontSize;
                IsBold = item.IsBold;
                IsItalic = item.IsItalic;
                IsUnderline = item.IsUnderline;
                IsStrikethrough = item.IsStrikethrough;
            }
        }
        _syncingProperties = false;
    }

    // ── 保存编辑前的文本样式用于撤销 ──────────────────────

    private string _editOldFamily = string.Empty;
    private double _editOldSize;
    private bool _editOldBold;
    private bool _editOldItalic;
    private bool _editOldUnderline;
    private bool _editOldStrikethrough;
    private bool _hasTextStyleSnapshot;

    private void SnapshotTextStyleIfNeeded()
    {
        if (_hasTextStyleSnapshot || SelectedAnnotation is not { Tool: AnnotationTool.Text }) return;
        _editOldFamily = SelectedAnnotation.FontFamily;
        _editOldSize = SelectedAnnotation.FontSize;
        _editOldBold = SelectedAnnotation.IsBold;
        _editOldItalic = SelectedAnnotation.IsItalic;
        _editOldUnderline = SelectedAnnotation.IsUnderline;
        _editOldStrikethrough = SelectedAnnotation.IsStrikethrough;
        _hasTextStyleSnapshot = true;
    }

    /// <summary>面板属性发生变化后应用到选中的文本标注。</summary>
    private void ApplyTextPropertyToSelected()
    {
        if (_syncingProperties || SelectedAnnotation is not { Tool: AnnotationTool.Text } item) return;
        SnapshotTextStyleIfNeeded();
        item.FontFamily = FontFamily;
        item.FontSize = FontSize;
        item.IsBold = IsBold;
        item.IsItalic = IsItalic;
        item.IsUnderline = IsUnderline;
        item.IsStrikethrough = IsStrikethrough;
        RequestRedraw?.Invoke();
    }

    /// <summary>提交文本样式编辑到撤销栈（在取消选中 / 切换工具时调用）。</summary>
    public void CommitTextStyleEdit(AnnotationItem? target = null)
    {
        target ??= SelectedAnnotation;
        if (!_hasTextStyleSnapshot || target is not { Tool: AnnotationTool.Text } item) { _hasTextStyleSnapshot = false; return; }
        if (item.FontFamily == _editOldFamily && item.FontSize == _editOldSize &&
            item.IsBold == _editOldBold && item.IsItalic == _editOldItalic &&
            item.IsUnderline == _editOldUnderline && item.IsStrikethrough == _editOldStrikethrough)
        {
            _hasTextStyleSnapshot = false;
            return;
        }
        var action = new EditTextStyleAction(item,
            _editOldFamily, _editOldSize, _editOldBold, _editOldItalic, _editOldUnderline, _editOldStrikethrough,
            item.FontFamily, item.FontSize, item.IsBold, item.IsItalic, item.IsUnderline, item.IsStrikethrough);
        _undoStack.Push(action);
        _redoStack.Clear();
        NotifyUndoRedoChanged();
        _hasTextStyleSnapshot = false;
    }

    // ── 颜色编辑快照 / 提交 ──────────────────────────────

    private Color _editOldColor;
    private bool _hasColorSnapshot;

    private void SnapshotColorIfNeeded()
    {
        if (_hasColorSnapshot || SelectedAnnotation is null) return;
        _editOldColor = SelectedAnnotation.StrokeColor;
        _hasColorSnapshot = true;
    }

    private void ApplyColorToSelected()
    {
        if (_syncingProperties || SelectedAnnotation is not { } item) return;
        SnapshotColorIfNeeded();
        item.StrokeColor = StrokeColor;
        RequestRedraw?.Invoke();
    }

    public void CommitColorEdit(AnnotationItem? target = null)
    {
        target ??= SelectedAnnotation;
        if (!_hasColorSnapshot || target is not { } item) { _hasColorSnapshot = false; return; }
        if (item.StrokeColor == _editOldColor) { _hasColorSnapshot = false; return; }
        var action = new EditColorAction(item, _editOldColor, item.StrokeColor);
        _undoStack.Push(action);
        _redoStack.Clear();
        NotifyUndoRedoChanged();
        _hasColorSnapshot = false;
    }

    // ── 线宽编辑快照 / 提交 ──────────────────────────────

    private double _editOldStrokeWidth;
    private bool _hasStrokeWidthSnapshot;

    private void SnapshotStrokeWidthIfNeeded()
    {
        if (_hasStrokeWidthSnapshot || SelectedAnnotation is null) return;
        _editOldStrokeWidth = SelectedAnnotation.StrokeWidth;
        _hasStrokeWidthSnapshot = true;
    }

    private void ApplyStrokeWidthToSelected()
    {
        if (_syncingProperties || SelectedAnnotation is not { } item) return;
        SnapshotStrokeWidthIfNeeded();
        item.StrokeWidth = StrokeWidth;
        RequestRedraw?.Invoke();
    }

    public void CommitStrokeWidthEdit(AnnotationItem? target = null)
    {
        target ??= SelectedAnnotation;
        if (!_hasStrokeWidthSnapshot || target is not { } item) { _hasStrokeWidthSnapshot = false; return; }
        if (item.StrokeWidth == _editOldStrokeWidth) { _hasStrokeWidthSnapshot = false; return; }
        var action = new EditStrokeWidthAction(item, _editOldStrokeWidth, item.StrokeWidth);
        _undoStack.Push(action);
        _redoStack.Clear();
        NotifyUndoRedoChanged();
        _hasStrokeWidthSnapshot = false;
    }

    // ── 圆角编辑快照 / 提交 ──────────────────────────────

    private double _editOldCornerRadius;
    private bool _hasCornerRadiusSnapshot;

    private void SnapshotCornerRadiusIfNeeded()
    {
        if (_hasCornerRadiusSnapshot || SelectedAnnotation is not { Tool: AnnotationTool.Rectangle }) return;
        _editOldCornerRadius = SelectedAnnotation.CornerRadius;
        _hasCornerRadiusSnapshot = true;
    }

    private void ApplyCornerRadiusToSelected()
    {
        if (_syncingProperties || SelectedAnnotation is not { Tool: AnnotationTool.Rectangle } item) return;
        SnapshotCornerRadiusIfNeeded();
        item.CornerRadius = CornerRadius;
        RequestRedraw?.Invoke();
    }

    public void CommitCornerRadiusEdit(AnnotationItem? target = null)
    {
        target ??= SelectedAnnotation;
        if (!_hasCornerRadiusSnapshot || target is not { Tool: AnnotationTool.Rectangle } item) { _hasCornerRadiusSnapshot = false; return; }
        if (item.CornerRadius == _editOldCornerRadius) { _hasCornerRadiusSnapshot = false; return; }
        var action = new EditCornerRadiusAction(item, _editOldCornerRadius, item.CornerRadius);
        _undoStack.Push(action);
        _redoStack.Clear();
        NotifyUndoRedoChanged();
        _hasCornerRadiusSnapshot = false;
    }

    // ── 模糊半径编辑快照 / 提交 ──────────────────────────

    private double _editOldBlurRadius;
    private bool _hasBlurRadiusSnapshot;

    private void SnapshotBlurRadiusIfNeeded()
    {
        if (_hasBlurRadiusSnapshot || SelectedAnnotation is not { Tool: AnnotationTool.Blur }) return;
        _editOldBlurRadius = SelectedAnnotation.BlurRadius;
        _hasBlurRadiusSnapshot = true;
    }

    private void ApplyBlurRadiusToSelected()
    {
        if (_syncingProperties || SelectedAnnotation is not { Tool: AnnotationTool.Blur } item) return;
        SnapshotBlurRadiusIfNeeded();
        item.BlurRadius = BlurRadius;
        RequestRedraw?.Invoke();
    }

    public void CommitBlurRadiusEdit(AnnotationItem? target = null)
    {
        target ??= SelectedAnnotation;
        if (!_hasBlurRadiusSnapshot || target is not { Tool: AnnotationTool.Blur } item) { _hasBlurRadiusSnapshot = false; return; }
        if (item.BlurRadius == _editOldBlurRadius) { _hasBlurRadiusSnapshot = false; return; }
        var action = new EditBlurRadiusAction(item, _editOldBlurRadius, item.BlurRadius);
        _undoStack.Push(action);
        _redoStack.Clear();
        NotifyUndoRedoChanged();
        _hasBlurRadiusSnapshot = false;
    }

    partial void OnStrokeColorChanged(Color value) => ApplyColorToSelected();
    partial void OnStrokeWidthChanged(double value) => ApplyStrokeWidthToSelected();
    partial void OnCornerRadiusChanged(double value) => ApplyCornerRadiusToSelected();
    partial void OnBlurRadiusChanged(double value) => ApplyBlurRadiusToSelected();
    partial void OnFontFamilyChanged(string value) => ApplyTextPropertyToSelected();
    partial void OnFontSizeChanged(double value) => ApplyTextPropertyToSelected();
    partial void OnIsBoldChanged(bool value) => ApplyTextPropertyToSelected();
    partial void OnIsItalicChanged(bool value) => ApplyTextPropertyToSelected();
    partial void OnIsUnderlineChanged(bool value) => ApplyTextPropertyToSelected();
    partial void OnIsStrikethroughChanged(bool value) => ApplyTextPropertyToSelected();

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
            CornerRadius = CornerRadius,
            BlurRadius = BlurRadius,
            FontSize = FontSize,
            FontFamily = FontFamily,
            IsBold = IsBold,
            IsItalic = IsItalic,
            IsUnderline = IsUnderline,
            IsStrikethrough = IsStrikethrough
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

    /// <summary>缩放完成后提交到撤销栈。</summary>
    public void CommitResize(AnnotationItem item, Point oldStart, Point oldEnd)
    {
        if (item.Start == oldStart && item.End == oldEnd) return;
        var action = new ResizeAnnotationAction(item, oldStart, oldEnd, item.Start, item.End);
        _undoStack.Push(action);
        _redoStack.Clear();
        NotifyUndoRedoChanged();
    }

    /// <summary>文本标注缩放字号后提交到撤销栈。</summary>
    public void CommitTextResize(AnnotationItem item, double oldFontSize)
    {
        if (Math.Abs(item.FontSize - oldFontSize) < 0.1) return;
        var action = new EditTextStyleAction(item,
            item.FontFamily, oldFontSize, item.IsBold, item.IsItalic, item.IsUnderline, item.IsStrikethrough,
            item.FontFamily, item.FontSize, item.IsBold, item.IsItalic, item.IsUnderline, item.IsStrikethrough);
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
            case AnnotationTool.Blur:
                var bounds = new Rect(new Point(Math.Min(sx, ex), Math.Min(sy, ey)),
                                     new Point(Math.Max(sx, ex), Math.Max(sy, ey)));
                bounds.Inflate(tol, tol);
                return bounds.Contains(pt);
            case AnnotationTool.Ellipse:
                double ecx = (sx + ex) / 2, ecy = (sy + ey) / 2;
                double erx = Math.Abs(ex - sx) / 2 + tol, ery = Math.Abs(ey - sy) / 2 + tol;
                if (erx < 1 || ery < 1) return false;
                double edx = pt.X - ecx, edy = pt.Y - ecy;
                return (edx * edx) / (erx * erx) + (edy * edy) / (ery * ery) <= 1.0;
            case AnnotationTool.Text:
                var textSize = MeasureTextSize(item);
                var textBounds = new Rect(sx, sy, textSize.Width, textSize.Height);
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

    /// <summary>使用 WPF FormattedText 精确测量文本标注的像素尺寸。</summary>
    internal static Size MeasureTextSize(AnnotationItem item)
    {
        if (string.IsNullOrEmpty(item.Text))
            return new Size(item.FontSize * 2, item.FontSize * 1.4);

        var typeface = new Typeface(
            new FontFamily(item.FontFamily),
            item.IsItalic ? FontStyles.Italic : FontStyles.Normal,
            item.IsBold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);

        var ft = new FormattedText(
            item.Text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, item.FontSize,
            System.Windows.Media.Brushes.Black,
            VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip);

        return new Size(Math.Max(ft.WidthIncludingTrailingWhitespace, item.FontSize),
                        Math.Max(ft.Height, item.FontSize));
    }

    // ── 命令 ───────────────────────────────────────────────

    [RelayCommand]
    private void UndoAnnotation()
    {
        if (_undoStack.Count == 0) return;
        var action = _undoStack.Pop();
        action.Undo(Annotations);
        _redoStack.Push(action);

        // 选中如果被撤销则清空，否则同步面板属性
        if (SelectedAnnotation is not null && !Annotations.Contains(SelectedAnnotation))
            SelectedAnnotation = null;
        else if (SelectedAnnotation is not null)
            SyncPanelFromAnnotation(SelectedAnnotation);

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

        if (SelectedAnnotation is not null)
            SyncPanelFromAnnotation(SelectedAnnotation);

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
    private void SelectBlur() => SelectedTool = AnnotationTool.Blur;

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
        {
            if (annotation.Tool == AnnotationTool.Blur)
                ApplyBlurRegion(canvas, bitmap, annotation);
            else
                DrawAnnotation(canvas, annotation);
        }

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
                float cr = (float)item.CornerRadius;
                if (cr > 0)
                    canvas.DrawRoundRect(rect, cr, cr, paint);
                else
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
                var skStyle = SKFontStyle.Normal;
                if (item.IsBold && item.IsItalic) skStyle = SKFontStyle.BoldItalic;
                else if (item.IsBold) skStyle = SKFontStyle.Bold;
                else if (item.IsItalic) skStyle = SKFontStyle.Italic;
                using (var typeface = SKTypeface.FromFamilyName(item.FontFamily, skStyle) ?? SKTypeface.Default)
                using (var font = new SKFont(typeface, (float)item.FontSize))
                using (var textPaint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill })
                {
                    float tx = (float)start.X;
                    float ty = (float)start.Y + (float)item.FontSize;
                    canvas.DrawText(item.Text, tx, ty, SKTextAlign.Left, font, textPaint);

                    // 下划线 / 删除线
                    float textWidth = font.MeasureText(item.Text, textPaint);
                    float lineThickness = Math.Max(1f, (float)item.FontSize / 14f);
                    using var linePaint = new SKPaint { Color = color, StrokeWidth = lineThickness, IsAntialias = true, Style = SKPaintStyle.Stroke };
                    if (item.IsUnderline)
                    {
                        float uy = ty + lineThickness * 2;
                        canvas.DrawLine(tx, uy, tx + textWidth, uy, linePaint);
                    }
                    if (item.IsStrikethrough)
                    {
                        float sy2 = ty - (float)item.FontSize * 0.35f;
                        canvas.DrawLine(tx, sy2, tx + textWidth, sy2, linePaint);
                    }
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

    private static void ApplyBlurRegion(SKCanvas canvas, SKBitmap bitmap, AnnotationItem item)
    {
        float ox = (float)item.Offset.X, oy = (float)item.Offset.Y;
        float x1 = (float)Math.Min(item.Start.X, item.End.X) + ox;
        float y1 = (float)Math.Min(item.Start.Y, item.End.Y) + oy;
        float x2 = (float)Math.Max(item.Start.X, item.End.X) + ox;
        float y2 = (float)Math.Max(item.Start.Y, item.End.Y) + oy;

        // 限制到位图边界
        x1 = Math.Max(0, x1); y1 = Math.Max(0, y1);
        x2 = Math.Min(bitmap.Width, x2); y2 = Math.Min(bitmap.Height, y2);
        if (x2 - x1 < 1 || y2 - y1 < 1) return;

        var blurRect = new SKRect(x1, y1, x2, y2);
        float sigma = (float)item.BlurRadius;

        using var blurFilter = SKImageFilter.CreateBlur(sigma, sigma);
        using var blurPaint = new SKPaint { ImageFilter = blurFilter };

        canvas.Save();
        canvas.ClipRect(blurRect);
        canvas.SaveLayer(blurPaint);
        canvas.DrawBitmap(bitmap, 0, 0);
        canvas.Restore();
        canvas.Restore();
    }

    /// <summary>计算箭头七边形顶点（WPF 预览与 SkiaSharp 渲染共用）。</summary>
    internal static Point[] CalculateArrowPoints(Point start, Point end, double strokeWidth)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lineLen = Math.Sqrt(dx * dx + dy * dy);
        if (lineLen < 1) return [];

        double angle = Math.Atan2(dy, dx);
        double headHalfW = strokeWidth * 1.5;
        double headLen = headHalfW * 1.6;
        headLen = Math.Min(headLen, lineLen * 0.45);
        headHalfW = headLen / 1.6;
        double shaftHalfW = strokeWidth / 2.0;

        double nx = -Math.Sin(angle), ny = Math.Cos(angle);
        double ax = Math.Cos(angle), ay = Math.Sin(angle);
        double jx = end.X - ax * headLen, jy = end.Y - ay * headLen;

        return
        [
            new(start.X + nx * shaftHalfW, start.Y + ny * shaftHalfW),
            new(jx + nx * shaftHalfW, jy + ny * shaftHalfW),
            new(jx + nx * headHalfW, jy + ny * headHalfW),
            new(end.X, end.Y),
            new(jx - nx * headHalfW, jy - ny * headHalfW),
            new(jx - nx * shaftHalfW, jy - ny * shaftHalfW),
            new(start.X - nx * shaftHalfW, start.Y - ny * shaftHalfW),
        ];
    }

    private static void DrawArrow(SKCanvas canvas, SKPaint paint, Point start, Point end)
    {
        var pts = CalculateArrowPoints(start, end, paint.StrokeWidth);
        if (pts.Length == 0) return;

        using var fillPaint = new SKPaint { Color = paint.Color, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var path = new SKPath();
        path.MoveTo((float)pts[0].X, (float)pts[0].Y);
        for (int i = 1; i < pts.Length; i++)
            path.LineTo((float)pts[i].X, (float)pts[i].Y);
        path.Close();
        canvas.DrawPath(path, fillPaint);
    }
}
