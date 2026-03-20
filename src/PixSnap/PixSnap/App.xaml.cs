using CommunityToolkit.Mvvm.Messaging;
using PixSnap.Models;
using PixSnap.Services;
using PixSnap.ViewModels;
using PixSnap.Views;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace PixSnap;

public partial class App : System.Windows.Application, IRecipient<ScreenshotCapturedMessage>
{
    private MainWindow? _mainWindow;
    private IScreenCaptureService? _screenCaptureService;
    private Forms.NotifyIcon? _trayIcon;

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
        _mainWindow.Hide();

        InitializeTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);

        if (_screenCaptureService is IDisposable disposable)
        {
            disposable.Dispose();
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
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
            Topmost = false
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

    private void InitializeTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "PixSnap",
            Visible = true,
            Icon = LoadTrayIcon()
        };

        _trayIcon.DoubleClick += (_, _) => StartCaptureFromTray();
    }

    private void StartCaptureFromTray()
    {
        if (_mainWindow?.DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (viewModel.StartCaptureCommand.CanExecute(null))
        {
            viewModel.StartCaptureCommand.Execute(null);
        }
    }

    private Icon? LoadTrayIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icons", "app.ico");
            if (File.Exists(iconPath))
            {
                return new Icon(iconPath);
            }

            var processIconPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(processIconPath) && File.Exists(processIconPath))
            {
                return Icon.ExtractAssociatedIcon(processIconPath);
            }

            return SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }
}