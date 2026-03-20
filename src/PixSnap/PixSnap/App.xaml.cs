using CommunityToolkit.Mvvm.Messaging;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using PixSnap.Models;
using PixSnap.Services;
using PixSnap.ViewModels;
using PixSnap.Views;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace PixSnap;

public partial class App : System.Windows.Application, IRecipient<ScreenshotCapturedMessage>
{
    private MainWindow? _mainWindow;
    private IScreenCaptureService? _screenCaptureService;
    private TaskbarIcon? _taskbarIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _screenCaptureService = new ScreenCaptureService();
        }
        catch (Exception exception)
        {
            MessageBoxWindow.Show(
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

        _taskbarIcon?.Dispose();
        _taskbarIcon = null;

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
        _taskbarIcon = (TaskbarIcon)FindResource("TrayIcon");
        _taskbarIcon.DataContext = new TrayViewModel(StartCaptureFromTray, ShowAbout);
        _taskbarIcon.Icon = LoadTrayIcon();
        _taskbarIcon.TrayMouseDoubleClick += (_, _) => StartCaptureFromTray();
        _taskbarIcon.TrayContextMenuOpen += OnTrayContextMenuOpen;
    }

    // Win32 获取光标屏幕像素坐标
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>
    /// 修复 Hardcodet 用 Win32 像素坐标设置偏移导致的弹出位置偏差。
    /// PlacementMode.AbsolutePoint 接受 WPF DIP，需除以 DPI 缩放平转换。
    /// </summary>
    private void OnTrayContextMenuOpen(object sender, RoutedEventArgs e)
    {
        if (_taskbarIcon?.ContextMenu is not { } menu) return;

        GetCursorPos(out var pt);
        double dpi = GetDpiForSystem() / 96.0;

        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = pt.X / dpi;
        menu.VerticalOffset = pt.Y / dpi;
    }

    private void ShowAbout()
    {
        var window = new AboutWindow();
        // _mainWindow 启动后始终隐藏，未曾显示时不能作为 Owner
        if (_mainWindow?.IsVisible == true)
            window.Owner = _mainWindow;
        window.ShowDialog();
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