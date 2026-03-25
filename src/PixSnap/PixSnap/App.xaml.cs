using CommunityToolkit.Mvvm.Messaging;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.DependencyInjection;
using PixSnap.Models;
using PixSnap.Resources;
using PixSnap.Services;
using PixSnap.ViewModels;
using PixSnap.Views;
using Serilog;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace PixSnap;

public partial class App : System.Windows.Application, IRecipient<ScreenshotCapturedMessage>
{
    // 全局互斥体，防止同一用户会话下多次启动
    private static readonly Mutex _instanceMutex = new(initiallyOwned: true, name: "Local\\PixSnap_SingleInstance");

    private MainWindow? _mainWindow;
    private TaskbarIcon? _taskbarIcon;

    /// <summary>全局 DI 容器，在 OnStartup 中构建。</summary>
    public IServiceProvider Services { get; private set; } = null!;

    /// <summary>获取当前 App 实例的便捷属性。</summary>
    public static new App Current => (App)System.Windows.Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 初始化 Serilog 文件日志：写入程序目录下的 logs/ 文件夹
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(AppContext.BaseDirectory, "logs", "pixsnap-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("PixSnap 启动");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 尝试获取互斥体所有权；获取失败说明已有实例在运行
        if (!_instanceMutex.WaitOne(TimeSpan.Zero, exitContext: false))
        {
            MessageBoxWindow.Show(
                S.App_AlreadyRunning,
                S.AppName,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        try
        {
            Services = ConfigureServices();
        }
        catch (Exception exception)
        {
            MessageBoxWindow.Show(
                BuildExceptionMessage(exception),
                S.App_StartupFailed,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        WeakReferenceMessenger.Default.Register(this);

        _mainWindow = new MainWindow
        {
            DataContext = Services.GetRequiredService<MainViewModel>()
        };

        MainWindow = _mainWindow;
        _mainWindow.Hide();

        InitializeTrayIcon();
        InitializeHotkey();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Services
        services.AddSingleton<IScreenCaptureService, ScreenCaptureService>();
        services.AddSingleton<GlobalHotkeyService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<ScreenshotPreviewViewModel>();

        return services.BuildServiceProvider();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        WeakReferenceMessenger.Default.UnregisterAll(this);

        _taskbarIcon?.Dispose();
        _taskbarIcon = null;

        OnnxSessionFactory.DisposeAll();

        // 释放 DI 容器（同时 Dispose 所有 Singleton）
        if (Services is IDisposable disposable)
            disposable.Dispose();

        Log.Information("PixSnap 退出");
        Log.CloseAndFlush();

        // 释放互斥体，允许下一个实例启动
        _instanceMutex.ReleaseMutex();
        _instanceMutex.Dispose();

        base.OnExit(e);
    }

    public void Receive(ScreenshotCapturedMessage message)
    {
        try
        {
            var previewViewModel = Services.GetRequiredService<ScreenshotPreviewViewModel>();
            previewViewModel.Receive(message);

            var previewWindow = new ScreenshotPreviewWindow
            {
                DataContext = previewViewModel,
                Topmost = false
            };

            previewWindow.Show();
            previewWindow.Activate();
        }
        catch (Exception exception)
        {
            MessageBoxWindow.Show(
                BuildExceptionMessage(exception),
                S.App_PreviewOpenFailed,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "未处理的 UI 线程异常");
        MessageBoxWindow.Show(
            BuildExceptionMessage(e.Exception),
            S.App_Exception,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log.Fatal(exception, "未处理的 CLR 异常");
            MessageBoxWindow.Show(
                BuildExceptionMessage(exception),
                S.App_Exception,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "未观察的后台任务异常");
        MessageBoxWindow.Show(
            BuildExceptionMessage(e.Exception),
            S.App_BackgroundTaskException,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.SetObserved();
    }

    private static string BuildExceptionMessage(Exception exception)
    {
        var builder = new StringBuilder();
        builder.AppendLine(S.App_StartupFailedDetail);

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
        _taskbarIcon.DataContext = new TrayViewModel(StartCaptureFromTray, ShowSettings, ShowAbout);
        _taskbarIcon.Icon = LoadTrayIcon();
        _taskbarIcon.TrayMouseDoubleClick += OnTrayMouseDoubleClick;
        _taskbarIcon.TrayContextMenuOpen += OnTrayContextMenuOpen;
    }

    private void OnTrayMouseDoubleClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Dispatcher.BeginInvoke(ShowScreenshotPreviewFromTray, DispatcherPriority.Background);
    }

    private void ShowScreenshotPreviewFromTray()
    {
        foreach (Window window in Windows)
        {
            if (window is ScreenshotPreviewWindow previewWindow)
            {
                if (!previewWindow.IsVisible)
                    previewWindow.Show();

                if (previewWindow.WindowState == WindowState.Minimized)
                    previewWindow.WindowState = WindowState.Normal;

                previewWindow.Activate();
                return;
            }
        }

        var previewViewModel = Services.GetRequiredService<ScreenshotPreviewViewModel>();
        var newPreviewWindow = new ScreenshotPreviewWindow
        {
            DataContext = previewViewModel,
            Topmost = false
        };

        newPreviewWindow.Show();
        newPreviewWindow.Activate();
    }

    private void InitializeHotkey()
    {
        var hotkeyService = Services.GetRequiredService<GlobalHotkeyService>();
        var (modifiers, key) = SettingsService.ReadHotkey();
        if (key != Key.None)
            hotkeyService.Register(modifiers, key, StartCaptureFromTray);
    }

    private void ShowSettings()
    {
        // 若设置窗口已开启则激活并返回，避免重复打开
        foreach (Window w in Windows)
        {
            if (w is SettingsWindow existing)
            {
                existing.Activate();
                return;
            }
        }

        var win = new SettingsWindow();
        var hotkeyService = Services.GetRequiredService<GlobalHotkeyService>();
        win.ViewModel.HotkeyChanged += (modifiers, key) =>
        {
            // 用户保存设置后重新注册快捷键
            hotkeyService.Unregister();
            if (key != Key.None)
                hotkeyService.Register(modifiers, key, StartCaptureFromTray);
        };
        win.ShowDialog();
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
        catch (Exception ex)
        {
            Log.Warning(ex, "LoadTrayIcon 失败");
            return SystemIcons.Application;
        }
    }
}