using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSnap.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PixSnap.ViewModels;

/// <summary>
/// AI 擦除面板 ViewModel：管理画刷参数、笔画记录和推理进度。
/// 笔画坐标使用图片像素空间；推理由 InpaintService 异步完成。
/// </summary>
public partial class EraserViewModel : ObservableObject
{
    // ── 画刷大小（图片像素单位） ──────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrushSizeText))]
    private int _brushSize = 30;

    public string BrushSizeText => $"{BrushSize} px";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EdgeFeatherStrengthText))]
    private int _edgeFeatherStrength = 20;

    public string EdgeFeatherStrengthText => $"{EdgeFeatherStrength}";

    // ── 推理进度 ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _progressText = string.Empty;

    // ── 笔画列表（图片像素坐标 + 图片像素半径） ───────────────────────────────

    public List<(Point Center, double Radius)> Strokes { get; } = [];

    // ── 事件 ─────────────────────────────────────────────────────────────────

    /// <summary>推理完成，携带修复后的图像。</summary>
    public event Action<BitmapSource>? InpaintApplied;

    /// <summary>笔画被清除（用户手动清除或推理完成后自动清除），通知 View 清空视觉笔画。</summary>
    public event Action? StrokesCleared;

    private CancellationTokenSource? _cts;

    // ── 命令 ─────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ClearStrokes()
    {
        Strokes.Clear();
        StrokesCleared?.Invoke();
    }

    [RelayCommand]
    private void CancelInpaint()
    {
        _cts?.Cancel();
    }

    // ── 推理入口（由 ScreenshotPreviewViewModel 在鼠标抬起时调用） ─────────────

    public async Task RunInpaintAsync(BitmapSource originalImage)
    {
        if (IsProcessing || Strokes.Count == 0) return;

        Log.Information("开始 AI 擦除: {StrokeCount} 笔画, 画笔大小={BrushSize}px", Strokes.Count, BrushSize);

        IsProcessing = true;
        Progress     = 0;
        ProgressText = "正在准备...";
        _cts         = new CancellationTokenSource();

        try
        {
            var token    = _cts.Token;
            var progress = new Progress<(double Value, string Text)>(t =>
            {
                Progress     = t.Value;
                ProgressText = t.Text;
            });

            var result = await InpaintService.RunWithOptionsAsync(originalImage, Strokes, EdgeFeatherStrength, progress, token);

            if (result is not null && !token.IsCancellationRequested)
            {
                Log.Information("AI 擦除完成");
                // 推理成功：清除旧笔画，通知 View 和父 ViewModel
                Strokes.Clear();
                StrokesCleared?.Invoke();
                InpaintApplied?.Invoke(result);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("AI 擦除已取消");
            ProgressText = "已取消";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AI 擦除失败");
            ProgressText = $"AI 处理失败：{ex.Message}";
            await Task.Delay(2000);
        }
        finally
        {
            IsProcessing = false;
            _cts?.Dispose();
            _cts = null;
        }
    }
}
