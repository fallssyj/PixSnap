using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PixSnap.Models;
using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

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
    private string _captureMode = string.Empty;

    public string PreviewScaleModeText => IsActualSize ? "切换为适应窗口" : "切换为 100% 原始大小";
    public string ZoomDisplayText => $"缩放 {(IsActualSize ? ZoomFactor : FitZoomFactor):P0}";

    public ScreenshotPreviewViewModel()
    {
        IsActive = true;
    }

    partial void OnIsActualSizeChanged(bool value)
    {
        OnPropertyChanged(nameof(PreviewScaleModeText));
        OnPropertyChanged(nameof(ZoomDisplayText));
    }

    partial void OnZoomFactorChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomDisplayText));
    }

    partial void OnFitZoomFactorChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomDisplayText));
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
            ZoomFactor = 1.0;
            IsActualSize = false;
            return;
        }

        ZoomFactor = 1.0;
        IsActualSize = true;
    }

    [RelayCommand]
    private void Close(Window? window)
    {
        window?.Close();
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
    }
}