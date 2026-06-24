using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSnap.Services;
using Serilog;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixSnap.ViewModels;

/// <summary>标注工具类型。</summary>
public enum AnnotationTool { Pointer, Arrow, Rectangle, Ellipse, Text, Pen, Blur, Number }

/// <summary>单条标注元素。</summary>
public sealed class AnnotationItem
{
    public AnnotationTool Tool { get; init; }
    public Point Start { get; set; }
    public Point End { get; set; }
    public Color StrokeColor { get; set; } = Colors.Red;
    public double StrokeWidth { get; set; } = 10;
    public string Text { get; set; } = string.Empty;
    public double FontSize { get; set; } = 20;
    public string FontFamily { get; set; } = "Microsoft YaHei";
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public bool IsUnderline { get; set; }
    public bool IsStrikethrough { get; set; }
    public double CornerRadius { get; set; }
    public double BlurRadius { get; set; } = 10;
    public bool UseMosaic { get; set; }
    public bool HasTextBackground { get; set; }
    public Color TextBackgroundColor { get; set; } = Color.FromArgb(180, 0, 0, 0);
    public bool HasFill { get; set; }
    public double FillOpacity { get; set; } = 40;
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

internal sealed class EditTextContentAction(AnnotationItem item, string oldText, string newText) : IAnnotationAction
{
    public void Undo(ObservableCollection<AnnotationItem> annotations) { item.Text = oldText; }
    public void Redo(ObservableCollection<AnnotationItem> annotations) { item.Text = newText; }
}

internal sealed class ResizePenAction(AnnotationItem item, List<Point> oldPoints, List<Point> newPoints, Point oldStart, Point oldEnd, Point newStart, Point newEnd) : IAnnotationAction
{
    public void Undo(ObservableCollection<AnnotationItem> annotations)
    {
        item.PenPoints.Clear(); item.PenPoints.AddRange(oldPoints);
        item.Start = oldStart; item.End = oldEnd;
    }
    public void Redo(ObservableCollection<AnnotationItem> annotations)
    {
        item.PenPoints.Clear(); item.PenPoints.AddRange(newPoints);
        item.Start = newStart; item.End = newEnd;
    }
}

internal sealed class EditBlurMosaicAction(AnnotationItem item, bool oldValue, bool newValue) : IAnnotationAction
{
    public void Undo(ObservableCollection<AnnotationItem> annotations) { item.UseMosaic = oldValue; }
    public void Redo(ObservableCollection<AnnotationItem> annotations) { item.UseMosaic = newValue; }
}

internal sealed class EditFillAction(AnnotationItem item, bool oldHasFill, double oldFillOpacity, bool newHasFill, double newFillOpacity) : IAnnotationAction
{
    public void Undo(ObservableCollection<AnnotationItem> annotations)
    {
        item.HasFill = oldHasFill;
        item.FillOpacity = oldFillOpacity;
    }

    public void Redo(ObservableCollection<AnnotationItem> annotations)
    {
        item.HasFill = newHasFill;
        item.FillOpacity = newFillOpacity;
    }
}

internal sealed class ReorderAnnotationAction(AnnotationItem item, int oldIndex, int newIndex) : IAnnotationAction
{
    public void Undo(ObservableCollection<AnnotationItem> annotations)
    {
        annotations.Remove(item);
        annotations.Insert(Math.Clamp(oldIndex, 0, annotations.Count), item);
    }

    public void Redo(ObservableCollection<AnnotationItem> annotations)
    {
        annotations.Remove(item);
        annotations.Insert(Math.Clamp(newIndex, 0, annotations.Count), item);
    }
}

internal sealed class EditTextBackgroundAction(
    AnnotationItem item,
    bool oldEnabled, Color oldColor,
    bool newEnabled, Color newColor) : IAnnotationAction
{
    public void Undo(ObservableCollection<AnnotationItem> annotations)
    {
        item.HasTextBackground = oldEnabled;
        item.TextBackgroundColor = oldColor;
    }

    public void Redo(ObservableCollection<AnnotationItem> annotations)
    {
        item.HasTextBackground = newEnabled;
        item.TextBackgroundColor = newColor;
    }
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
    private const double NumberBadgeSize = 28;
    internal const double NumberBadgeDiameter = NumberBadgeSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsRectangleSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsBlurSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsShapeSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsColorSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsStrokeSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsPropertyPanelVisible))]
    [NotifyPropertyChangedFor(nameof(IsFillSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsContextBarVisible))]
    [NotifyPropertyChangedFor(nameof(IsToolOptionsBarVisible))]
    private AnnotationTool _selectedTool = AnnotationTool.Arrow;

    partial void OnSelectedToolChanged(AnnotationTool value)
    {
        var item = SelectedAnnotation;
        CommitTextStyleEdit(item);
        CommitColorEdit(item);
        CommitStrokeWidthEdit(item);
        CommitCornerRadiusEdit(item);
        CommitBlurRadiusEdit(item);
        CommitBlurMosaicEdit(item);
        CommitTextBackgroundEdit(item);
        CommitFillEdit(item);
        OnPropertyChanged(nameof(IsShapeSettingsVisible));
        OnPropertyChanged(nameof(IsColorSettingsVisible));
        OnPropertyChanged(nameof(IsStrokeSettingsVisible));
        OnPropertyChanged(nameof(IsTextSettingsVisible));
        OnPropertyChanged(nameof(IsRectangleSettingsVisible));
        OnPropertyChanged(nameof(IsBlurSettingsVisible));
        OnPropertyChanged(nameof(IsFillSettingsVisible));
        OnPropertyChanged(nameof(IsPropertyPanelVisible));
        OnPropertyChanged(nameof(IsContextBarVisible));
        OnPropertyChanged(nameof(IsToolOptionsBarVisible));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrokeColorBrush))]
    private Color _strokeColor = Colors.Red;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrokeColorBrush))]
    private double _strokeOpacity = 100;

    [ObservableProperty]
    private double _strokeWidth = 10;

    [ObservableProperty]
    private double _cornerRadius;

    [ObservableProperty]
    private double _blurRadius = 10;

    [ObservableProperty]
    private bool _useMosaic;

    [ObservableProperty]
    private bool _hasTextBackground;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextBackgroundColorBrush))]
    private Color _textBackgroundColor = Color.FromArgb(180, 0, 0, 0);

    [ObservableProperty]
    private double _fontSize = 20;

    [ObservableProperty]
    private string _fontFamily = "Microsoft YaHei";

    [ObservableProperty]
    private bool _hasFill;

    [ObservableProperty]
    private double _fillOpacity = 40;

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
    [NotifyPropertyChangedFor(nameof(IsShapeSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsColorSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(HasSelectedAnnotation))]
    [NotifyPropertyChangedFor(nameof(IsPropertyPanelVisible))]
    [NotifyPropertyChangedFor(nameof(IsContextBarVisible))]
    [NotifyPropertyChangedFor(nameof(IsToolOptionsBarVisible))]
    private AnnotationItem? _selectedAnnotation;

    [ObservableProperty]
    private bool _isColorPickerOpen;

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

    /// <summary>矩形/椭圆填充设置是否可见。</summary>
    public bool IsFillSettingsVisible =>
        SelectedTool is AnnotationTool.Rectangle or AnnotationTool.Ellipse ||
        (SelectedAnnotation?.Tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse);

    /// <summary>线宽/圆角等形状属性是否可见。</summary>
    public bool IsShapeSettingsVisible =>
        !IsTextSettingsVisible && !IsBlurSettingsVisible &&
        SelectedTool != AnnotationTool.Number &&
        SelectedAnnotation?.Tool != AnnotationTool.Number;

    /// <summary>颜色设置是否可见（模糊工具不使用描边色）。</summary>
    public bool IsColorSettingsVisible => !IsBlurSettingsVisible;

    /// <summary>线宽/透明度等描边设置是否在底部条显示。</summary>
    public bool IsStrokeSettingsVisible => IsShapeSettingsVisible || IsColorSettingsVisible;

    /// <summary>是否选中了可编辑的标注对象。</summary>
    public bool HasSelectedAnnotation => SelectedAnnotation is not null;

    /// <summary>属性区是否有内容可展示。</summary>
    public bool IsPropertyPanelVisible =>
        IsColorSettingsVisible || IsShapeSettingsVisible || IsBlurSettingsVisible ||
        IsTextSettingsVisible || IsFillSettingsVisible;

    /// <summary>底部上下文选项条是否可见（选中对象 / 文字排版）。</summary>
    public bool IsContextBarVisible =>
        HasSelectedAnnotation || IsTextSettingsVisible;

    /// <summary>第二行工具专属选项（填充 / 圆角 / 模糊）。</summary>
    public bool IsToolOptionsBarVisible =>
        IsFillSettingsVisible || IsRectangleSettingsVisible || IsBlurSettingsVisible;

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
            CommitBlurMosaicEdit(oldValue);
            CommitTextBackgroundEdit(oldValue);
            CommitFillEdit(oldValue);
        }

        SyncPanelFromAnnotation(newValue);
        OnPropertyChanged(nameof(IsTextSettingsVisible));
        OnPropertyChanged(nameof(IsRectangleSettingsVisible));
        OnPropertyChanged(nameof(IsBlurSettingsVisible));
        OnPropertyChanged(nameof(IsFillSettingsVisible));
        OnPropertyChanged(nameof(IsShapeSettingsVisible));
        OnPropertyChanged(nameof(IsColorSettingsVisible));
        OnPropertyChanged(nameof(IsStrokeSettingsVisible));
        OnPropertyChanged(nameof(HasSelectedAnnotation));
        OnPropertyChanged(nameof(IsPropertyPanelVisible));
        OnPropertyChanged(nameof(IsContextBarVisible));
        OnPropertyChanged(nameof(IsToolOptionsBarVisible));
        OnPropertyChanged(nameof(CanBringForward));
        OnPropertyChanged(nameof(CanSendBackward));
        BringForwardCommand.NotifyCanExecuteChanged();
        SendBackwardCommand.NotifyCanExecuteChanged();
        DuplicateSelectedAnnotationCommand.NotifyCanExecuteChanged();
    }

    private void SyncPanelFromAnnotation(AnnotationItem? item)
    {
        _syncingProperties = true;
        if (item is not null)
        {
            StrokeColor = item.StrokeColor;
            StrokeWidth = item.StrokeWidth;
            if (item.Tool == AnnotationTool.Rectangle || item.Tool == AnnotationTool.Ellipse)
            {
                HasFill = item.HasFill;
                FillOpacity = item.FillOpacity;
            }
            if (item.Tool == AnnotationTool.Rectangle)
                CornerRadius = item.CornerRadius;
            if (item.Tool == AnnotationTool.Blur)
            {
                BlurRadius = item.BlurRadius;
                UseMosaic = item.UseMosaic;
            }
            if (item.Tool == AnnotationTool.Text)
            {
                FontFamily = item.FontFamily;
                FontSize = item.FontSize;
                IsBold = item.IsBold;
                IsItalic = item.IsItalic;
                IsUnderline = item.IsUnderline;
                IsStrikethrough = item.IsStrikethrough;
                HasTextBackground = item.HasTextBackground;
                TextBackgroundColor = item.TextBackgroundColor;
            }

            StrokeOpacity = item.StrokeColor.A / 255.0 * 100.0;
        }
        _syncingProperties = false;
    }

    private Color ApplyStrokeOpacity(Color rgb) =>
        Color.FromArgb(
            (byte)Math.Round(StrokeOpacity / 100.0 * 255),
            rgb.R, rgb.G, rgb.B);

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

    // ── 马赛克模式编辑快照 / 提交 ────────────────────────

    private bool _editOldUseMosaic;
    private bool _hasBlurMosaicSnapshot;

    private void SnapshotBlurMosaicIfNeeded()
    {
        if (_hasBlurMosaicSnapshot || SelectedAnnotation is not { Tool: AnnotationTool.Blur }) return;
        _editOldUseMosaic = SelectedAnnotation.UseMosaic;
        _hasBlurMosaicSnapshot = true;
    }

    private void ApplyBlurMosaicToSelected()
    {
        if (_syncingProperties || SelectedAnnotation is not { Tool: AnnotationTool.Blur } item) return;
        SnapshotBlurMosaicIfNeeded();
        item.UseMosaic = UseMosaic;
        RequestRedraw?.Invoke();
    }

    public void CommitBlurMosaicEdit(AnnotationItem? target = null)
    {
        target ??= SelectedAnnotation;
        if (!_hasBlurMosaicSnapshot || target is not { Tool: AnnotationTool.Blur } item) { _hasBlurMosaicSnapshot = false; return; }
        if (item.UseMosaic == _editOldUseMosaic) { _hasBlurMosaicSnapshot = false; return; }
        var action = new EditBlurMosaicAction(item, _editOldUseMosaic, item.UseMosaic);
        _undoStack.Push(action);
        _redoStack.Clear();
        NotifyUndoRedoChanged();
        _hasBlurMosaicSnapshot = false;
    }

    // ── 文字背景编辑快照 / 提交 ──────────────────────────

    private bool _editOldHasTextBackground;
    private Color _editOldTextBackgroundColor;
    private bool _hasTextBackgroundSnapshot;

    private void SnapshotTextBackgroundIfNeeded()
    {
        if (_hasTextBackgroundSnapshot || SelectedAnnotation is not { Tool: AnnotationTool.Text }) return;
        _editOldHasTextBackground = SelectedAnnotation.HasTextBackground;
        _editOldTextBackgroundColor = SelectedAnnotation.TextBackgroundColor;
        _hasTextBackgroundSnapshot = true;
    }

    private void ApplyTextBackgroundToSelected()
    {
        if (_syncingProperties || SelectedAnnotation is not { Tool: AnnotationTool.Text } item) return;
        SnapshotTextBackgroundIfNeeded();
        item.HasTextBackground = HasTextBackground;
        item.TextBackgroundColor = TextBackgroundColor;
        RequestRedraw?.Invoke();
    }

    public void CommitTextBackgroundEdit(AnnotationItem? target = null)
    {
        target ??= SelectedAnnotation;
        if (!_hasTextBackgroundSnapshot || target is not { Tool: AnnotationTool.Text } item) { _hasTextBackgroundSnapshot = false; return; }
        if (item.HasTextBackground == _editOldHasTextBackground && item.TextBackgroundColor == _editOldTextBackgroundColor)
        {
            _hasTextBackgroundSnapshot = false;
            return;
        }
        var action = new EditTextBackgroundAction(item,
            _editOldHasTextBackground, _editOldTextBackgroundColor,
            item.HasTextBackground, item.TextBackgroundColor);
        _undoStack.Push(action);
        _redoStack.Clear();
        NotifyUndoRedoChanged();
        _hasTextBackgroundSnapshot = false;
    }

    // ── 填充编辑快照 / 提交 ──────────────────────────────

    private bool _editOldHasFill;
    private double _editOldFillOpacity;
    private bool _hasFillSnapshot;

    private void SnapshotFillIfNeeded()
    {
        if (_hasFillSnapshot || SelectedAnnotation is not { Tool: AnnotationTool.Rectangle or AnnotationTool.Ellipse }) return;
        _editOldHasFill = SelectedAnnotation.HasFill;
        _editOldFillOpacity = SelectedAnnotation.FillOpacity;
        _hasFillSnapshot = true;
    }

    private void ApplyFillToSelected()
    {
        if (_syncingProperties || SelectedAnnotation is not { Tool: AnnotationTool.Rectangle or AnnotationTool.Ellipse } item) return;
        SnapshotFillIfNeeded();
        item.HasFill = HasFill;
        item.FillOpacity = FillOpacity;
        RequestRedraw?.Invoke();
    }

    public void CommitFillEdit(AnnotationItem? target = null)
    {
        target ??= SelectedAnnotation;
        if (!_hasFillSnapshot || target is not { Tool: AnnotationTool.Rectangle or AnnotationTool.Ellipse } item)
        {
            _hasFillSnapshot = false;
            return;
        }

        if (item.HasFill == _editOldHasFill && Math.Abs(item.FillOpacity - _editOldFillOpacity) < 0.01)
        {
            _hasFillSnapshot = false;
            return;
        }

        var action = new EditFillAction(item, _editOldHasFill, _editOldFillOpacity, item.HasFill, item.FillOpacity);
        _undoStack.Push(action);
        _redoStack.Clear();
        NotifyUndoRedoChanged();
        _hasFillSnapshot = false;
    }

    partial void OnStrokeColorChanged(Color value)
    {
        if (!_syncingProperties)
            StrokeOpacity = value.A / 255.0 * 100.0;
        ApplyColorToSelected();
        OnPropertyChanged(nameof(CustomColorRed));
        OnPropertyChanged(nameof(CustomColorGreen));
        OnPropertyChanged(nameof(CustomColorBlue));
        OnPropertyChanged(nameof(CustomColorAlpha));
    }

    partial void OnStrokeOpacityChanged(double value)
    {
        if (_syncingProperties) return;
        StrokeColor = Color.FromArgb(
            (byte)Math.Round(value / 100.0 * 255),
            StrokeColor.R, StrokeColor.G, StrokeColor.B);
    }

    public int CustomColorRed
    {
        get => StrokeColor.R;
        set => StrokeColor = Color.FromArgb(StrokeColor.A, (byte)Math.Clamp(value, 0, 255), StrokeColor.G, StrokeColor.B);
    }

    public int CustomColorGreen
    {
        get => StrokeColor.G;
        set => StrokeColor = Color.FromArgb(StrokeColor.A, StrokeColor.R, (byte)Math.Clamp(value, 0, 255), StrokeColor.B);
    }

    public int CustomColorBlue
    {
        get => StrokeColor.B;
        set => StrokeColor = Color.FromArgb(StrokeColor.A, StrokeColor.R, StrokeColor.G, (byte)Math.Clamp(value, 0, 255));
    }

    public int CustomColorAlpha
    {
        get => StrokeColor.A;
        set
        {
            StrokeOpacity = Math.Clamp(value, 0, 255) / 255.0 * 100.0;
        }
    }

    partial void OnStrokeWidthChanged(double value) => ApplyStrokeWidthToSelected();
    partial void OnCornerRadiusChanged(double value) => ApplyCornerRadiusToSelected();
    partial void OnBlurRadiusChanged(double value) => ApplyBlurRadiusToSelected();
    partial void OnUseMosaicChanged(bool value) => ApplyBlurMosaicToSelected();
    partial void OnHasFillChanged(bool value) => ApplyFillToSelected();
    partial void OnFillOpacityChanged(double value) => ApplyFillToSelected();
    partial void OnHasTextBackgroundChanged(bool value) => ApplyTextBackgroundToSelected();
    partial void OnTextBackgroundColorChanged(Color value) => ApplyTextBackgroundToSelected();
    partial void OnFontFamilyChanged(string value) => ApplyTextPropertyToSelected();
    partial void OnFontSizeChanged(double value) => ApplyTextPropertyToSelected();
    partial void OnIsBoldChanged(bool value) => ApplyTextPropertyToSelected();
    partial void OnIsItalicChanged(bool value) => ApplyTextPropertyToSelected();
    partial void OnIsUnderlineChanged(bool value) => ApplyTextPropertyToSelected();
    partial void OnIsStrikethroughChanged(bool value) => ApplyTextPropertyToSelected();

    public SolidColorBrush StrokeColorBrush => new(StrokeColor);
    public SolidColorBrush TextBackgroundColorBrush => new(TextBackgroundColor);

    public ObservableCollection<AnnotationItem> Annotations { get; } = [];

    [ObservableProperty]
    private AnnotationItem? _currentAnnotation;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>是否存在尚未应用到图片的标注。</summary>
    public bool HasPendingAnnotations => Annotations.Count > 0;

    public bool CanBringForward => SelectedAnnotation is not null &&
        Annotations.IndexOf(SelectedAnnotation) >= 0 &&
        Annotations.IndexOf(SelectedAnnotation) < Annotations.Count - 1;

    public bool CanSendBackward => SelectedAnnotation is not null &&
        Annotations.IndexOf(SelectedAnnotation) > 0;

    /// <summary>根据快捷键切换工具（单键，无修饰符）。</summary>
    public bool TrySelectToolFromKey(Key key)
    {
        var tool = key switch
        {
            Key.V => AnnotationTool.Pointer,
            Key.A => AnnotationTool.Arrow,
            Key.R => AnnotationTool.Rectangle,
            Key.E => AnnotationTool.Ellipse,
            Key.T => AnnotationTool.Text,
            Key.P => AnnotationTool.Pen,
            Key.M => AnnotationTool.Blur,
            _ => (AnnotationTool?)null
        };

        if (tool is null)
            return false;

        SelectedTool = tool.Value;
        return true;
    }

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
        OnPropertyChanged(nameof(HasPendingAnnotations));
        UndoAnnotationCommand.NotifyCanExecuteChanged();
        RedoAnnotationCommand.NotifyCanExecuteChanged();
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
            IsStrikethrough = IsStrikethrough,
            HasTextBackground = HasTextBackground,
            TextBackgroundColor = TextBackgroundColor,
            UseMosaic = UseMosaic,
            HasFill = HasFill,
            FillOpacity = FillOpacity
        };
        if (SelectedTool == AnnotationTool.Pen)
            CurrentAnnotation.PenPoints.Add(imagePoint);
        IsAnnotating = true;
    }

    public void UpdateAnnotation(Point imagePoint)
    {
        if (CurrentAnnotation is null) return;
        if (CurrentAnnotation.Tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse &&
            (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)))
        {
            imagePoint = ConstrainSquarePoint(CurrentAnnotation.Start, imagePoint);
        }

        if (CurrentAnnotation.Tool == AnnotationTool.Pen)
        {
            var last = CurrentAnnotation.PenPoints[^1];
            if (!AnnotationPenHelper.ShouldAddPoint(last, imagePoint))
                return;
            CurrentAnnotation.PenPoints.Add(imagePoint);
        }
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

        if (CurrentAnnotation.Tool == AnnotationTool.Pen)
        {
            if (CurrentAnnotation.PenPoints.Count < 2)
            {
                CurrentAnnotation = null;
                IsAnnotating = false;
                return;
            }

            var simplified = AnnotationPenHelper.Simplify(CurrentAnnotation.PenPoints);
            CurrentAnnotation.PenPoints.Clear();
            CurrentAnnotation.PenPoints.AddRange(simplified);
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

    private static Point ConstrainSquarePoint(Point start, Point end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var size = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (size < 1) size = 1;
        return new Point(
            start.X + Math.Sign(dx == 0 ? 1 : dx) * size,
            start.Y + Math.Sign(dy == 0 ? 1 : dy) * size);
    }

    private static AnnotationItem CloneAnnotation(AnnotationItem source)
    {
        var clone = new AnnotationItem
        {
            Tool = source.Tool,
            Start = source.Start,
            End = source.End,
            StrokeColor = source.StrokeColor,
            StrokeWidth = source.StrokeWidth,
            Text = source.Text,
            FontSize = source.FontSize,
            FontFamily = source.FontFamily,
            IsBold = source.IsBold,
            IsItalic = source.IsItalic,
            IsUnderline = source.IsUnderline,
            IsStrikethrough = source.IsStrikethrough,
            CornerRadius = source.CornerRadius,
            BlurRadius = source.BlurRadius,
            UseMosaic = source.UseMosaic,
            HasTextBackground = source.HasTextBackground,
            TextBackgroundColor = source.TextBackgroundColor,
            HasFill = source.HasFill,
            FillOpacity = source.FillOpacity,
            Offset = source.Offset
        };
        clone.PenPoints.AddRange(source.PenPoints);
        return clone;
    }

    private void ReorderSelected(int newIndex)
    {
        if (SelectedAnnotation is not { } item) return;
        var oldIndex = Annotations.IndexOf(item);
        if (oldIndex < 0 || oldIndex == newIndex) return;

        Annotations.RemoveAt(oldIndex);
        newIndex = Math.Clamp(newIndex, 0, Annotations.Count);
        Annotations.Insert(newIndex, item);

        var action = new ReorderAnnotationAction(item, oldIndex, newIndex);
        _undoStack.Push(action);
        _redoStack.Clear();
        NotifyUndoRedoChanged();
        RequestRedraw?.Invoke();
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

    // ── 双击编辑已有文本 ──────────────────────────────────

    private AnnotationItem? _editingTextItem;
    private string _editingTextOldText = string.Empty;

    /// <summary>开始编辑已有文本标注的内容（双击触发）。</summary>
    public AnnotationItem? BeginEditExistingText(AnnotationItem item)
    {
        if (item.Tool != AnnotationTool.Text) return null;
        _editingTextItem = item;
        _editingTextOldText = item.Text;
        SelectedAnnotation = null;
        return item;
    }

    /// <summary>完成已有文本标注的编辑，提交到撤销栈。</summary>
    public void CommitEditExistingText(string newText)
    {
        if (_editingTextItem is null) return;
        var item = _editingTextItem;
        _editingTextItem = null;

        if (string.IsNullOrWhiteSpace(newText))
        {
            // 文本清空 → 删除标注
            var index = Annotations.IndexOf(item);
            if (index >= 0)
            {
                var action = new DeleteAnnotationAction(item, index);
                _undoStack.Push(action);
                _redoStack.Clear();
                Annotations.Remove(item);
                NotifyUndoRedoChanged();
            }
            RequestRedraw?.Invoke();
            return;
        }

        if (newText != _editingTextOldText)
        {
            item.Text = newText;
            var action = new EditTextContentAction(item, _editingTextOldText, newText);
            _undoStack.Push(action);
            _redoStack.Clear();
            NotifyUndoRedoChanged();
        }
        RequestRedraw?.Invoke();
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

    /// <summary>Pen 缩放完成后提交到撤销栈。</summary>
    public void CommitPenResize(AnnotationItem item, List<Point> oldPoints, Point oldStart, Point oldEnd)
    {
        var action = new ResizePenAction(item, oldPoints, [.. item.PenPoints], oldStart, oldEnd, item.Start, item.End);
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
            case AnnotationTool.Number:
                return GetNumberBounds(item).Contains(pt);
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

    internal static Rect GetNumberBounds(AnnotationItem item)
    {
        var size = Math.Max(Math.Abs(item.End.X - item.Start.X), Math.Abs(item.End.Y - item.Start.Y));
        if (size < 1) size = NumberBadgeSize;
        return new Rect(item.Start.X + item.Offset.X, item.Start.Y + item.Offset.Y, size, size);
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

        var dpi = Application.Current?.MainWindow is { } window
            ? VisualTreeHelper.GetDpi(window).PixelsPerDip
            : 1.0;
        var ft = new FormattedText(
            item.Text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, item.FontSize,
            System.Windows.Media.Brushes.Black,
            dpi);

        return new Size(Math.Max(ft.WidthIncludingTrailingWhitespace, item.FontSize),
                        Math.Max(ft.Height, item.FontSize));
    }

    // ── 命令 ───────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanUndo))]
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
        else
            ResetPendingEditSnapshots();

        NotifyUndoRedoChanged();
        RequestRedraw?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
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
        ResetPendingEditSnapshots();
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

    [RelayCommand(CanExecute = nameof(CanBringForward))]
    private void BringForward()
    {
        if (SelectedAnnotation is null) return;
        var index = Annotations.IndexOf(SelectedAnnotation);
        if (index < 0 || index >= Annotations.Count - 1) return;
        ReorderSelected(index + 1);
        OnPropertyChanged(nameof(CanBringForward));
        OnPropertyChanged(nameof(CanSendBackward));
        BringForwardCommand.NotifyCanExecuteChanged();
        SendBackwardCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSendBackward))]
    private void SendBackward()
    {
        if (SelectedAnnotation is null) return;
        var index = Annotations.IndexOf(SelectedAnnotation);
        if (index <= 0) return;
        ReorderSelected(index - 1);
        OnPropertyChanged(nameof(CanBringForward));
        OnPropertyChanged(nameof(CanSendBackward));
        BringForwardCommand.NotifyCanExecuteChanged();
        SendBackwardCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedAnnotation))]
    private void DuplicateSelectedAnnotation()
    {
        if (SelectedAnnotation is null) return;
        var clone = CloneAnnotation(SelectedAnnotation);
        clone.Offset = SelectedAnnotation.Offset + new Vector(12, 12);

        var action = new AddAnnotationAction(clone);
        _undoStack.Push(action);
        _redoStack.Clear();
        Annotations.Add(clone);
        SelectedAnnotation = clone;
        NotifyUndoRedoChanged();
        RequestRedraw?.Invoke();
    }

    [RelayCommand]
    private void PickCustomColor() => IsColorPickerOpen = !IsColorPickerOpen;

    [RelayCommand]
    private void SetTextBackgroundDark()
    {
        HasTextBackground = true;
        TextBackgroundColor = Color.FromArgb(180, 0, 0, 0);
    }

    [RelayCommand]
    private void SetTextBackgroundLight()
    {
        HasTextBackground = true;
        TextBackgroundColor = Color.FromArgb(210, 255, 255, 255);
    }

    [RelayCommand]
    private void CloseColorPicker() => IsColorPickerOpen = false;

    [RelayCommand]
    private void SetColorRed() { IsColorPickerOpen = false; StrokeColor = ApplyStrokeOpacity(Colors.Red); }
    [RelayCommand]
    private void SetColorBlue() { IsColorPickerOpen = false; StrokeColor = ApplyStrokeOpacity(Color.FromRgb(0x33, 0x99, 0xFF)); }
    [RelayCommand]
    private void SetColorGreen() { IsColorPickerOpen = false; StrokeColor = ApplyStrokeOpacity(Color.FromRgb(0x22, 0xCC, 0x55)); }
    [RelayCommand]
    private void SetColorYellow() { IsColorPickerOpen = false; StrokeColor = ApplyStrokeOpacity(Color.FromRgb(0xFF, 0xCC, 0x00)); }
    [RelayCommand]
    private void SetColorWhite() { IsColorPickerOpen = false; StrokeColor = ApplyStrokeOpacity(Colors.White); }
    [RelayCommand]
    private void SetColorBlack() { IsColorPickerOpen = false; StrokeColor = ApplyStrokeOpacity(Colors.Black); }
    [RelayCommand]
    private void SetColorOrange() { IsColorPickerOpen = false; StrokeColor = ApplyStrokeOpacity(Color.FromRgb(0xFF, 0x88, 0x00)); }
    [RelayCommand]
    private void SetColorPurple() { IsColorPickerOpen = false; StrokeColor = ApplyStrokeOpacity(Color.FromRgb(0xAA, 0x44, 0xFF)); }
    [RelayCommand]
    private void SetColorPink() { IsColorPickerOpen = false; StrokeColor = ApplyStrokeOpacity(Color.FromRgb(0xFF, 0x66, 0xB2)); }
    [RelayCommand]
    private void SetColorGray() { IsColorPickerOpen = false; StrokeColor = ApplyStrokeOpacity(Color.FromRgb(0x88, 0x88, 0x88)); }

    // ── 应用标注到图像 ─────────────────────────────────────

    public async Task ApplyAnnotationsAsync(BitmapSource originalImage)
    {
        if (Annotations.Count == 0) return;

        Log.Information("应用标注: {Count} 项", Annotations.Count);
        var frozen = ImageIOService.CreateFrozenSnapshot(originalImage);
        var export = await Task.Run(() => RenderAnnotationsToPixels(frozen)).ConfigureAwait(true);
        if (export is null)
            return;

        var result = SkiaInteropHelper.CreateFrozenBitmapFromBgra(
            export.Value.Pixels, export.Value.Width, export.Value.Height,
            export.Value.DpiX, export.Value.DpiY);
        AnnotationApplied?.Invoke(result);
        Annotations.Clear();
        _undoStack.Clear();
        _redoStack.Clear();
        SelectedAnnotation = null;
        ResetPendingEditSnapshots();
        NotifyUndoRedoChanged();
    }

    [RelayCommand]
    private void Cancel()
    {
        Annotations.Clear();
        _undoStack.Clear();
        _redoStack.Clear();
        SelectedAnnotation = null;
        ResetPendingEditSnapshots();
        NotifyUndoRedoChanged();
        AnnotationCancelled?.Invoke();
    }

    private (byte[] Pixels, int Width, int Height, double DpiX, double DpiY)? RenderAnnotationsToPixels(BitmapSource originalImage)
    {
        using var bitmap = SkiaInteropHelper.BitmapSourceToSKBitmap(originalImage);
        using var canvas = new SKCanvas(bitmap);

        foreach (var annotation in Annotations)
        {
            if (annotation.Tool == AnnotationTool.Blur)
            {
                if (annotation.UseMosaic)
                    ApplyMosaicRegion(canvas, bitmap, annotation);
                else
                    ApplyBlurRegion(canvas, bitmap, annotation);
            }
            else
                DrawAnnotation(canvas, annotation);
        }

        return (SkiaInteropHelper.CopyPixels(bitmap), bitmap.Width, bitmap.Height, originalImage.DpiX, originalImage.DpiY);
    }

    private void ResetPendingEditSnapshots()
    {
        _hasColorSnapshot = false;
        _hasStrokeWidthSnapshot = false;
        _hasFillSnapshot = false;
        _hasTextStyleSnapshot = false;
        _hasCornerRadiusSnapshot = false;
        _hasBlurRadiusSnapshot = false;
        _hasBlurMosaicSnapshot = false;
        _hasTextBackgroundSnapshot = false;
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
                if (item.HasFill)
                {
                    using var fillPaint = CreateFillPaint(item, color);
                    if (cr > 0)
                        canvas.DrawRoundRect(rect, cr, cr, fillPaint);
                    else
                        canvas.DrawRect(rect, fillPaint);
                }
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
                if (item.HasFill)
                {
                    using var fillPaint = CreateFillPaint(item, color);
                    canvas.DrawOval(ecx, ecy, rx, ry, fillPaint);
                }
                canvas.DrawOval(ecx, ecy, rx, ry, paint);
                break;

            case AnnotationTool.Number:
                DrawNumberBadge(canvas, item, color, start);
                break;

            case AnnotationTool.Text:
                DrawTextWithBackground(canvas, item, color, start);
                break;

            case AnnotationTool.Pen:
                if (item.PenPoints.Count >= 2)
                {
                    using var path = AnnotationPenHelper.BuildSmoothSkiaPath(item.PenPoints, ox, oy);
                    canvas.DrawPath(path, paint);
                }
                break;
        }
    }

    private static SKPaint CreateFillPaint(AnnotationItem item, SKColor strokeColor)
    {
        var alpha = (byte)Math.Clamp(Math.Round(item.FillOpacity / 100.0 * 255), 0, 255);
        return new SKPaint
        {
            Color = new SKColor(strokeColor.Red, strokeColor.Green, strokeColor.Blue, alpha),
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
    }

    private static void DrawNumberBadge(SKCanvas canvas, AnnotationItem item, SKColor color, Point start)
    {
        float size = (float)Math.Max(Math.Abs(item.End.X - item.Start.X), Math.Abs(item.End.Y - item.Start.Y));
        if (size < 1) size = (float)NumberBadgeSize;
        float cx = (float)start.X + size / 2f;
        float cy = (float)start.Y + size / 2f;

        using var circlePaint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawCircle(cx, cy, size / 2f, circlePaint);

        using var typeface = SKTypeface.FromFamilyName(item.FontFamily, SKFontStyle.Bold) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, (float)item.FontSize);
        var textColor = color.Red + color.Green + color.Blue > 380
            ? new SKColor(0, 0, 0, color.Alpha)
            : new SKColor(255, 255, 255, color.Alpha);
        using var textPaint = new SKPaint { Color = textColor, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawText(item.Text, cx, cy + font.Size * 0.35f, SKTextAlign.Center, font, textPaint);
    }

    private static void DrawTextWithBackground(SKCanvas canvas, AnnotationItem item, SKColor color, Point start)
    {
        var skStyle = SKFontStyle.Normal;
        if (item.IsBold && item.IsItalic) skStyle = SKFontStyle.BoldItalic;
        else if (item.IsBold) skStyle = SKFontStyle.Bold;
        else if (item.IsItalic) skStyle = SKFontStyle.Italic;

        using var typeface = SKTypeface.FromFamilyName(item.FontFamily, skStyle) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, (float)item.FontSize);
        using var textPaint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };

        float tx = (float)start.X;
        float ty = (float)start.Y + (float)item.FontSize;
        float textWidth = font.MeasureText(item.Text, textPaint);

        if (item.HasTextBackground && item.TextBackgroundColor.A > 0)
        {
            var bg = item.TextBackgroundColor;
            var textSize = MeasureTextSize(item);
            float padding = (float)item.FontSize * 0.15f;
            using var bgPaint = new SKPaint
            {
                Color = new SKColor(bg.R, bg.G, bg.B, bg.A),
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            canvas.DrawRoundRect(
                new SKRect(tx - padding, (float)start.Y - padding,
                    tx + textWidth + padding, (float)start.Y + (float)textSize.Height + padding),
                4, 4, bgPaint);
        }

        canvas.DrawText(item.Text, tx, ty, SKTextAlign.Left, font, textPaint);

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

    private static void ApplyMosaicRegion(SKCanvas canvas, SKBitmap bitmap, AnnotationItem item)
    {
        float ox = (float)item.Offset.X, oy = (float)item.Offset.Y;
        float x1 = (float)Math.Min(item.Start.X, item.End.X) + ox;
        float y1 = (float)Math.Min(item.Start.Y, item.End.Y) + oy;
        float x2 = (float)Math.Max(item.Start.X, item.End.X) + ox;
        float y2 = (float)Math.Max(item.Start.Y, item.End.Y) + oy;

        x1 = Math.Max(0, x1); y1 = Math.Max(0, y1);
        x2 = Math.Min(bitmap.Width, x2); y2 = Math.Min(bitmap.Height, y2);
        if (x2 - x1 < 1 || y2 - y1 < 1) return;

        int px = (int)x1, py = (int)y1;
        int pw = Math.Max(1, (int)(x2 - x1));
        int ph = Math.Max(1, (int)(y2 - y1));

        using var region = new SKBitmap(pw, ph, bitmap.ColorType, bitmap.AlphaType);
        if (!bitmap.ExtractSubset(region, new SKRectI(px, py, px + pw, py + ph)))
            return;

        int blockSize = Math.Max(4, (int)Math.Round(item.BlurRadius));
        int smallW = Math.Max(1, pw / blockSize);
        int smallH = Math.Max(1, ph / blockSize);

        using var small = region.Resize(new SKImageInfo(smallW, smallH), new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        if (small is null) return;

        using var mosaic = small.Resize(new SKImageInfo(pw, ph), new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        if (mosaic is null) return;

        canvas.DrawBitmap(mosaic, px, py);
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

    /// <summary>直线实心楔形箭头：尾端尖点，箭身线性渐宽，末端三角箭头（WPF 预览与 Skia 渲染共用）。</summary>
    internal static Point[] CalculateArrowPoints(Point start, Point end, double strokeWidth)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lineLen = Math.Sqrt(dx * dx + dy * dy);
        if (lineLen < 1)
            return [];

        double angle = Math.Atan2(dy, dx);
        double nx = -Math.Sin(angle);
        double ny = Math.Cos(angle);
        double ax = Math.Cos(angle);
        double ay = Math.Sin(angle);

        double headHalfW = Math.Max(strokeWidth * 0.85, 5);
        double headLen = Math.Max(strokeWidth * 1.45, headHalfW * 1.55);
        headLen = Math.Min(headLen, lineLen * 0.5);

        double neckHalfW = Math.Max(strokeWidth * 0.42, 2.5);
        double jx = end.X - ax * headLen;
        double jy = end.Y - ay * headLen;

        return
        [
            new(start.X, start.Y),
            new(jx + nx * neckHalfW, jy + ny * neckHalfW),
            new(jx + nx * headHalfW, jy + ny * headHalfW),
            new(end.X, end.Y),
            new(jx - nx * headHalfW, jy - ny * headHalfW),
            new(jx - nx * neckHalfW, jy - ny * neckHalfW),
        ];
    }

    private static void DrawArrow(SKCanvas canvas, SKPaint paint, Point start, Point end)
    {
        var pts = CalculateArrowPoints(start, end, paint.StrokeWidth);
        if (pts.Length == 0)
            return;

        using var fillPaint = new SKPaint { Color = paint.Color, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var path = new SKPath();
        path.MoveTo((float)pts[0].X, (float)pts[0].Y);
        for (int i = 1; i < pts.Length; i++)
            path.LineTo((float)pts[i].X, (float)pts[i].Y);
        path.Close();
        canvas.DrawPath(path, fillPaint);
    }
}
