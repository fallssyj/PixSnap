using CommunityToolkit.Mvvm.Messaging;
using Hardcodet.Wpf.TaskbarNotification;
using MicaWPF.Core.Events;
using MicaWPF.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using PixSnap.Models;
using PixSnap.Services;
using PixSnap.ViewModels;
using PixSnap.Views;
using Serilog;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
    private ISubscription? _themeSubscription;

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
                "PixSnap 已在运行中，请查看系统托盘。",
                "PixSnap",
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
                "PixSnap 启动失败",
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

        // 订阅 MicaWPF 主题切换，强制刷新桥接字典使颜色跟随
        _themeSubscription = MicaWPFServiceUtility.ThemeService.ThemeChanged.Subscribe(OnMicaThemeChanged);

        // 应用持久化的主题偏好
        ApplyTheme(SettingsService.ReadTheme());
    }

    private void OnMicaThemeChanged(MicaWPF.Core.Enums.WindowsTheme _)
    {
        Dispatcher.Invoke(() =>
        {
            var dicts = Resources.MergedDictionaries;
            for (var i = 0; i < dicts.Count; i++)
            {
                if (dicts[i].Source?.OriginalString?.Contains("Theme.xaml", StringComparison.Ordinal) == true)
                {
                    var source = dicts[i].Source;
                    dicts.RemoveAt(i);
                    dicts.Insert(i, new ResourceDictionary { Source = source });
                    break;
                }
            }
        });
    }

    /// <summary>
    /// 将主题索引映射为 MicaWPF WindowsTheme 并应用。
    /// 0 = Auto, 1 = Dark, 2 = Light。
    /// </summary>
    private static void ApplyTheme(int themeIndex)
    {
        var theme = themeIndex switch
        {
            1 => MicaWPF.Core.Enums.WindowsTheme.Dark,
            2 => MicaWPF.Core.Enums.WindowsTheme.Light,
            _ => MicaWPF.Core.Enums.WindowsTheme.Auto,
        };
        MicaWPFServiceUtility.ThemeService.ChangeTheme(theme);
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

        _themeSubscription?.Dispose();
        _themeSubscription = null;

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
            // 保存到历史记录（后台异步，不阻塞预览窗口）
            _ = ScreenshotHistoryService.SaveAsync(message.Screenshot);

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
                "预览窗口打开失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "未处理的 UI 线程异常");
        MessageBoxWindow.Show(
            BuildExceptionMessage(e.Exception),
            "应用异常",
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
                "应用异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "未观察的后台任务异常");
        MessageBoxWindow.Show(
            BuildExceptionMessage(e.Exception),
            "后台任务异常",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.SetObserved();
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
        _taskbarIcon.DataContext = new TrayViewModel(StartCaptureFromTray, StartDelayCaptureFromTray, CaptureLastRegionFromTray, ShowSettings, ShowAbout);
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
        {
            if (!hotkeyService.Register(modifiers, key, StartCaptureFromTray))
            {
                MessageBoxWindow.Show(
                    string.Format("全局快捷键 {0} 注册失败，可能已被其他程序占用。\n请在设置中更换快捷键。",
                        FormatHotkey(modifiers, key)),
                    "快捷键注册失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private static string FormatHotkey(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private void ShowSettings() => ShowSettingsWindow();

    /// <summary>打开设置窗口并订阅快捷键变更事件，供外部调用（如 ScreenshotPreviewViewModel）。</summary>
    public void ShowSettingsWindow()
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
            {
                if (!hotkeyService.Register(modifiers, key, StartCaptureFromTray))
                {
                    MessageBoxWindow.Show(
                        string.Format("全局快捷键 {0} 注册失败，可能已被其他程序占用。\n请更换其他快捷键。",
                            FormatHotkey(modifiers, key)),
                        "快捷键注册失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
        };
        win.ViewModel.ThemeChanged += ApplyTheme;
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
        foreach (Window w in Windows)
        {
            if (w is AboutWindow existing)
            {
                existing.Activate();
                return;
            }
        }

        var window = new AboutWindow();
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

    private async void StartDelayCaptureFromTray(int seconds)
    {
        Log.Information("延时截图: {Seconds} 秒", seconds);
        var countdown = new Views.CountdownOverlay(seconds);
        countdown.Show();
        await Task.Delay(seconds * 1000);
        countdown.Close();
        StartCaptureFromTray();
    }

    private async void CaptureLastRegionFromTray()
    {
        if (_mainWindow?.DataContext is MainViewModel viewModel)
        {
            await viewModel.CaptureLastRegionAsync();
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