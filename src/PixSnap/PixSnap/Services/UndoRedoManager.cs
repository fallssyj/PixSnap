using System.Windows.Media.Imaging;

namespace PixSnap.Services;

/// <summary>
/// 基于双栈的撤销/重做管理器，支持大图自动限额。
/// 实现 IDisposable 以便在窗口关闭时显式释放大图引用。
/// </summary>
public sealed class UndoRedoManager : IDisposable
{
    private const long LargeImagePixelThreshold = 16_000_000;
    private const int NormalUndoLimit = 10;
    private const int LargeImageUndoLimit = 3;
    private const int AbsoluteMaxUndoLimit = 30;

    private readonly Stack<BitmapSource> _undoStack = [];
    private readonly Stack<BitmapSource> _redoStack = [];

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>将当前图像压入撤销栈，清空重做栈。</summary>
    public void PushUndo(BitmapSource current, BitmapSource incoming)
    {
        _undoStack.Push(current);
        TrimUndoStack(Math.Min(GetUndoLimit(incoming), AbsoluteMaxUndoLimit));
        _redoStack.Clear();
    }

    /// <summary>弹出上一张图像（撤销），将当前图像压入重做栈。</summary>
    public BitmapSource Undo(BitmapSource current)
    {
        _redoStack.Push(current);
        return _undoStack.Pop();
    }

    /// <summary>弹出下一张图像（重做），将当前图像压入撤销栈。</summary>
    public BitmapSource Redo(BitmapSource current)
    {
        _undoStack.Push(current);
        return _redoStack.Pop();
    }

    /// <summary>清空所有历史记录。</summary>
    public void Reset()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }

    public void Dispose()
    {
        Reset();
    }

    private static int GetUndoLimit(BitmapSource image)
    {
        long pixels = (long)image.PixelWidth * image.PixelHeight;
        return pixels >= LargeImagePixelThreshold ? LargeImageUndoLimit : NormalUndoLimit;
    }

    private void TrimUndoStack(int limit)
    {
        if (_undoStack.Count <= limit) return;

        var kept = _undoStack.Take(limit).Reverse().ToArray();
        _undoStack.Clear();
        foreach (var item in kept)
            _undoStack.Push(item);
    }
}
