using CommunityToolkit.Mvvm.Messaging;
using PixSnap.Models;
using PixSnap.Services;
using PixSnap.ViewModels;
using PixSnap.Views;
using System;
using System.Text;
using System.Windows;

namespace PixSnap;

public partial class App : Application, IRecipient<ScreenshotCapturedMessage>
{
    private MainWindow? _mainWindow;
    private IScreenCaptureService? _screenCaptureService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _screenCaptureService = new ScreenCaptureService();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                BuildExceptionMessage(exception),
                "PixSnap 启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        WeakReferenceMessenger.Default.Register(this);

        _mainWindow = new MainWindow
        {
            DataContext = new MainViewModel(_screenCaptureService)
        };

        MainWindow = _mainWindow;
        _mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);

        if (_screenCaptureService is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnExit(e);
    }

    public void Receive(ScreenshotCapturedMessage message)
    {
        var previewViewModel = new ScreenshotPreviewViewModel();
        previewViewModel.Receive(message);

        var previewWindow = new ScreenshotPreviewWindow
        {
            DataContext = previewViewModel,
            Owner = _mainWindow,
            Topmost = true
        };

        previewWindow.Show();
        previewWindow.Activate();
    }

    private static string BuildExceptionMessage(Exception exception)
    {
        var builder = new StringBuilder();
        builder.AppendLine("应用启动失败");

        var current = exception;
        var depth = 0;
        while (current is not null)
        {
            builder.AppendLine($"[{depth}] {current.GetType().FullName}");
            builder.AppendLine(current.Message);
            builder.AppendLine($"HRESULT: 0x{current.HResult:X8}");

            if (!string.IsNullOrWhiteSpace(current.StackTrace))
            {
                builder.AppendLine("StackTrace:");
                builder.AppendLine(current.StackTrace);
            }

            current = current.InnerException;
            depth++;
            if (current is not null)
            {
                builder.AppendLine("InnerException:");
            }
        }

        return builder.ToString().TrimEnd();
    }
}