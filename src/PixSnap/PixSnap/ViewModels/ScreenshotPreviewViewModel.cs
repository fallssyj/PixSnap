using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PixSnap.Models;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace PixSnap.ViewModels;

public partial class ScreenshotPreviewViewModel : ObservableRecipient, IRecipient<ScreenshotCapturedMessage>
{
    public const double MinZoomFactor = 0.1;
    public const double MaxZoomFactor = 8.0;

    [ObservableProperty]
    private BitmapSource? _screenshotImage;

    [ObservableProperty]
    private bool _isActualSize;

    [ObservableProperty]
    private double _zoomFactor = 1.0;

    [ObservableProperty]
    private double _fitZoomFactor = 1.0;

    [ObservableProperty]
    private string _captureTime = string.Empty;

    [ObservableProperty]
    private string _imageSize = string.Empty;

    [ObservableProperty]
    private string _fileSize = string.Empty;

    [ObservableProperty]
    private string _captureMode = string.Empty;

    [ObservableProperty]
    private bool _isMaximized;

    public string PreviewScaleModeText => IsActualSize ? "缩放以适应" : "缩放以原始";
    public string ZoomDisplayText => $"缩放 {(IsActualSize ? ZoomFactor : FitZoomFactor):P0}";
    public string ZoomCompactText => $"{(IsActualSize ? ZoomFactor : FitZoomFactor):P0}";
    public double ZoomSliderValue
    {
        get => (IsActualSize ? ZoomFactor : FitZoomFactor) * 100.0;
        set => SetManualZoomFactor(value / 100.0);
    }

    public ScreenshotPreviewViewModel()
    {
        IsActive = true;
    }

    partial void OnIsActualSizeChanged(bool value)
    {
        OnPropertyChanged(nameof(PreviewScaleModeText));
        OnPropertyChanged(nameof(ZoomDisplayText));
        OnPropertyChanged(nameof(ZoomCompactText));
        OnPropertyChanged(nameof(ZoomSliderValue));
    }

    partial void OnZoomFactorChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomDisplayText));
        OnPropertyChanged(nameof(ZoomCompactText));
        OnPropertyChanged(nameof(ZoomSliderValue));
    }

    partial void OnFitZoomFactorChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomDisplayText));
        OnPropertyChanged(nameof(ZoomCompactText));
        OnPropertyChanged(nameof(ZoomSliderValue));
    }

    public void SetManualZoomFactor(double zoomFactor)
    {
        ZoomFactor = Math.Clamp(zoomFactor, MinZoomFactor, MaxZoomFactor);
        IsActualSize = true;
    }

    public void SwitchToFitMode()
    {
        ZoomFactor = FitZoomFactor;
        IsActualSize = false;
    }

    [RelayCommand]
    private void SaveToFile()
    {
        if (ScreenshotImage is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 文件|*.png",
            FileName = $"PixSnap_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ScreenshotImage));

        using var stream = File.Create(dialog.FileName);
        encoder.Save(stream);
    }

    [RelayCommand]
    private void CopyToClipboard()
    {
        if (ScreenshotImage is not null)
        {
            Clipboard.SetImage(ScreenshotImage);
        }
    }

    [RelayCommand]
    private void TogglePreviewScale()
    {
        if (IsActualSize)
        {
            SwitchToFitMode();
            return;
        }

        SetManualZoomFactor(1.0);
    }

    [RelayCommand]
    private void ZoomIn()
    {
        var baseZoom = IsActualSize ? ZoomFactor : FitZoomFactor;
        SetManualZoomFactor(baseZoom * 1.1);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        var baseZoom = IsActualSize ? ZoomFactor : FitZoomFactor;
        SetManualZoomFactor(baseZoom / 1.1);
    }

    [RelayCommand]
    private void FitToWindow()
    {
        SwitchToFitMode();
    }

    [RelayCommand]
    private void Close(Window? window)
    {
        if (window is null)
        {
            return;
        }
        window?.Close();
    }

    [RelayCommand]
    private void Minimize(Window? window)
    {
        if (window is null)
        {
            return;
        }

        window.WindowState = WindowState.Minimized;
        IsMaximized = false;
    }

    [RelayCommand]
    private void Maximize(Window? window)
    {
        if (window is null)
        {
            return;
        }

        var nextState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

        window.WindowState = nextState;
        IsMaximized = nextState == WindowState.Maximized;
    }

    [RelayCommand]
    private void Recapture(Window? window)
    {
        window?.Close();
        Application.Current.MainWindow?.Show();
        Application.Current.MainWindow?.Activate();
    }

    protected override void OnActivated()
    {
        Messenger.RegisterAll(this);
    }

    protected override void OnDeactivated()
    {
        Messenger.UnregisterAll(this);
    }

    public void Receive(ScreenshotCapturedMessage message)
    {
        ScreenshotImage = message.Screenshot;
        IsActualSize = false;
        ZoomFactor = 1.0;
        CaptureMode = message.CaptureMode;
        CaptureTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        ImageSize = message.Screenshot is null
            ? string.Empty
            : $"{message.Screenshot.PixelWidth} x {message.Screenshot.PixelHeight}";
        FileSize = message.Screenshot is null
            ? string.Empty
            : FormatFileSize(GetEncodedPngSize(message.Screenshot));
    }

    private static long GetEncodedPngSize(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.Length;
    }

    private static string FormatFileSize(long byteCount)
    {
        const double kilo = 1024d;
        const double mega = kilo * 1024d;

        if (byteCount >= mega)
        {
            return $"{byteCount / mega:0.0} MB";
        }

        if (byteCount >= kilo)
        {
            return $"{byteCount / kilo:0.0} KB";
        }

        return $"{byteCount} B";
    }
}