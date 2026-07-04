using CommunityToolkit.Mvvm.Messaging;
using Hardcodet.Wpf.TaskbarNotification;
using iNKORE.UI.WPF.Modern;
using Microsoft.Extensions.DependencyInjection;
using PixSnap.Models;
using PixSnap.Services;
using PixSnap.Controls;
using PixSnap.ViewModels;
using PixSnap.Views;
using Serilog;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace PixSnap;

public partial class App : System.Windows.Application, IRecipient<ScreenshotCapturedMessage>
{
    private const string InstanceMutexName = "Local\\PixSnap_SingleInstance";

    private static Mutex? _instanceMutex;
    private static bool _ownsInstanceMutex;

    static App()
    {
        PreloadUiAutomationAssemblies();
    }

    private static void PreloadUiAutomationAssemblies()
    {
        try
        {
            Assembly.Load(new AssemblyName("UIAutomationProvider"));
            Assembly.Load(new AssemblyName("UIAutomationTypes"));
        }
        catch
        {
            // Non-fatal: core capture/edit features do not depend on UI Automation.
        }
    }

    private MainWindow? _mainWindow;
    private TaskbarIcon? _taskbarIcon;
    private TrayViewModel? _trayViewModel;

    /// <summary>全局 DI 容器，在 OnStartup 中构建。</summary>
    public IServiceProvider Services { get; private set; } = null!;

    /// <summary>获取当前 App 实例的便捷属性。</summary>
    public static new App Current => (App)System.Windows.Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureDataDirectories();

        // 初始化 Serilog 文件日志：%LocalAppData%\PixSnap\logs\yyyy-MM-dd\pixsnap.log
        LogFileService.ConfigureLogger();

        Log.Information("PixSnap 启动");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 尝试获取互斥体所有权；获取失败说明已有实例在运行
        if (!TryBecomeSingleInstance())
        {
            AppMessageBox.Show(
                "PixSnap 已在运行中，请查看系统托盘。",
                "PixSnap",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        if (!EnsureDisclaimerAccepted())
            return;

        AppPaths.MigrateLegacySettings();

        // 清理过期的录屏临时文件（7天前），异步执行避免阻塞启动
        _ = Task.Run(CleanupOldRecordingFiles);
        _ = Task.Run(() => LogFileService.DeleteExpiredFiles(SettingsService.ReadLogRetentionDays()));

        try
        {
            Services = ConfigureServices();
        }
        catch (Exception exception)
        {
            AppMessageBox.Show(
                ExceptionMessageFormatter.Format("应用启动失败", exception),
                "PixSnap 启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        WeakReferenceMessenger.Default.Register(this);

        Services.GetRequiredService<NavigationMessageHandler>().Register();

        _mainWindow = new MainWindow
        {
            DataContext = Services.GetRequiredService<MainViewModel>()
        };

        MainWindow = _mainWindow;
        _mainWindow.Hide();

        // 应用持久化的主题偏好（Loaded 后确保 iNKORE 资源已就绪）
        AiGpuSettings.LoadFromSettings();
        OcrSettings.LoadFromSettings();
        AiFeatureSettings.LoadFromSettings();
        DirectMlDeviceEnumerator.WarmCache();
        _ = Task.Run(async () =>
        {
            await DirectMlDeviceEnumerator.EnsureEnumeratedAsync().ConfigureAwait(false);
        });
        Dispatcher.BeginInvoke(
            () =>
            {
                ThemeHelper.ApplyTheme(SettingsService.ReadTheme());
                WindowBackdropHelper.ApplyBackdrop(SettingsService.ReadWindowBackdrop());
            },
            DispatcherPriority.Loaded);

        //创建托盘图标
        InitializeTrayIcon();

        // 检测系统版本是否支持 Windows Graphics Capture API
        // CreateFreeThreaded + IsCursorCaptureEnabled 需要 Windows 10 2004 (Build 19041)
        if (Environment.OSVersion.Version.Build < 19041)
        {
            AppMessageBox.Show(
                "PixSnap 需要 Windows 10 版本 2004（Build 19041）或更高版本。\n\n" +
                $"当前系统版本：{Environment.OSVersion.Version}",
                "系统版本不支持",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        //注册快捷键
        InitializeHotkey();

        ScheduleStartupUpdateCheck();
    }

    private static bool EnsureDisclaimerAccepted()
    {
        if (SettingsService.ReadDisclaimerAccepted())
            return true;

        Log.Information("首次启动，显示许可与免责声明");

        var dialog = new DisclaimerWindow(requireAcceptance: true);
        dialog.ShowDialog();

        if (dialog.AcceptanceResult == true)
        {
            SettingsService.WriteDisclaimerAccepted(true);
            Log.Information("用户已同意许可与免责声明");
            return true;
        }

        Log.Information("用户拒绝许可与免责声明，退出应用");
        ReleaseInstanceMutex();
        Current.Shutdown(0);
        return false;
    }

    private void ScheduleStartupUpdateCheck()
    {
        if (!SettingsService.ReadAutoCheckUpdateOnStartup())
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await UpdateCheckService.CheckAsync(
                    SettingsService.ReadUpdateSource()).ConfigureAwait(false);

                if (!result.HasUpdate)
                    return;

                await Dispatcher.InvokeAsync(
                    () => UpdateCheckService.PromptUpdate(result),
                    DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "启动时检查更新失败");
            }
        });
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Services
        services.AddSingleton<IScreenCaptureService, ScreenCaptureService>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ScreenshotPreviewWindowService>();
        services.AddSingleton<NavigationMessageHandler>();
        services.AddSingleton<TrayMenuService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<ScreenshotPreviewViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<TrayViewModel>();

        return services.BuildServiceProvider();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        WeakReferenceMessenger.Default.UnregisterAll(this);

        if (Services is not null)
        {
            Services.GetRequiredService<NavigationMessageHandler>().Unregister();

            // 释放 DI 容器（同时 Dispose 所有 Singleton）
            if (Services is IDisposable disposable)
                disposable.Dispose();
        }

        _taskbarIcon?.Dispose();
        _taskbarIcon = null;

        OcrService.Shutdown();

        Log.Information("PixSnap 退出");
        Log.CloseAndFlush();

        ReleaseInstanceMutex();

        base.OnExit(e);
    }

    private static bool TryBecomeSingleInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        if (createdNew)
        {
            _ownsInstanceMutex = true;
            return true;
        }

        try
        {
            if (_instanceMutex.WaitOne(TimeSpan.Zero, exitContext: false))
            {
                _ownsInstanceMutex = true;
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            // 上一实例异常退出未释放互斥体；当前线程已接管所有权。
            _ownsInstanceMutex = true;
            return true;
        }

        _instanceMutex.Dispose();
        _instanceMutex = null;
        return false;
    }

    private static void ReleaseInstanceMutex()
    {
        if (!_ownsInstanceMutex || _instanceMutex is null)
            return;

        try
        {
            _instanceMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 所有权计数异常时忽略，避免阻断退出。
        }

        _instanceMutex.Dispose();
        _instanceMutex = null;
        _ownsInstanceMutex = false;
    }

    public void Receive(ScreenshotCapturedMessage message)
    {
        try
        {
            var notificationVm = new NotificationViewModel(message.Screenshot, message.CaptureMode);
            notificationVm.OpenRequested += OpenPreviewWindow;

            var notification = new NotificationWindow { DataContext = notificationVm };
            notification.Closed += (_, _) => notificationVm.OpenRequested -= OpenPreviewWindow;
            notification.Show();
        }
        catch (Exception exception)
        {
            AppMessageBox.Show(
                ExceptionMessageFormatter.Format("预览窗口打开失败", exception),
                "预览窗口打开失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenPreviewWindow(System.Windows.Media.Imaging.BitmapSource screenshot, string captureMode)
    {
        try
        {
            var message = new ScreenshotCapturedMessage(screenshot, captureMode);
            Services.GetRequiredService<ScreenshotPreviewWindowService>().Open(message);
        }
        catch (Exception exception)
        {
            AppMessageBox.Show(
                ExceptionMessageFormatter.Format("预览窗口打开失败", exception),
                "预览窗口打开失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (IsBenignUiAutomationException(e.Exception))
        {
            Log.Warning(e.Exception, "UI Automation 组件加载失败（已忽略，不影响截图/录屏功能）");
            e.Handled = true;
            return;
        }

        Log.Error(e.Exception, "未处理的 UI 线程异常");
        AppMessageBox.Show(
            ExceptionMessageFormatter.Format("未处理的 UI 线程异常", e.Exception),
            "应用异常",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>
    /// WPF 在焦点切换时会触发 AutomationPeer，若 UIAutomationProvider 未能加载则抛出此异常。
    /// 该问题通常由 .NET Desktop Runtime 不完整或更新中断引起，与 PixSnap 核心功能无关。
    /// </summary>
    private static bool IsBenignUiAutomationException(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is FileNotFoundException fileNotFound &&
                fileNotFound.FileName?.Contains("UIAutomationProvider", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }

            if (current is TypeInitializationException typeInit &&
                typeInit.TypeName?.Contains("AutomationPeer", StringComparison.Ordinal) == true &&
                IsBenignUiAutomationException(typeInit.InnerException))
            {
                return true;
            }
        }

        return false;
    }

    private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log.Fatal(exception, "未处理的 CLR 异常 (IsTerminating={IsTerminating})", e.IsTerminating);
        }
        else
        {
            Log.Fatal("未处理的非托管异常: {ExObj} (IsTerminating={IsTerminating})", e.ExceptionObject, e.IsTerminating);
        }
        Log.CloseAndFlush();

        if (e.ExceptionObject is Exception ex)
        {
            AppMessageBox.Show(
                ExceptionMessageFormatter.Format("未处理的 CLR 异常", ex),
                "应用异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "未观察的后台任务异常");
        AppMessageBox.Show(
            ExceptionMessageFormatter.Format("未观察的后台任务异常", e.Exception),
            "后台任务异常",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.SetObserved();
    }

    private static void CleanupOldRecordingFiles()
    {
        try
        {
            var dir = SettingsService.ReadRecordingTempDirectory();
            if (!Directory.Exists(dir)) return;

            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var file in Directory.EnumerateFiles(dir, "recording_*.mp4"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                        Log.Information("已清理过期录屏文件: {File}", file);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "清理录屏文件失败: {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "清理过期录屏文件时出错");
        }
    }

    private void InitializeTrayIcon()
    {
        _taskbarIcon = (TaskbarIcon)FindResource("TrayIcon");
        _trayViewModel = Services.GetRequiredService<TrayViewModel>();
        _taskbarIcon.DataContext = _trayViewModel;
        _taskbarIcon.Icon = LoadTrayIcon();
        _taskbarIcon.TrayMouseDoubleClick += OnTrayMouseDoubleClick;
        _taskbarIcon.TrayRightMouseUp += OnTrayRightMouseUp;
    }

    private void OnTrayRightMouseUp(object sender, RoutedEventArgs e)
    {
        if (_trayViewModel is null)
            return;

        Services.GetRequiredService<TrayMenuService>().Show(
            new TrayContextMenuView { DataContext = _trayViewModel });
        e.Handled = true;
    }

    private void OnTrayMouseDoubleClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Dispatcher.BeginInvoke(ExecuteTrayDoubleClickAction, DispatcherPriority.Background);
    }

    private void ExecuteTrayDoubleClickAction()
    {
        var navigation = Services.GetRequiredService<INavigationService>();
        if (SettingsService.ReadTrayDoubleClickAction() == TrayDoubleClickAction.Capture)
            navigation.StartCapture();
        else
            navigation.OpenScreenshotPreview();
    }

    private void InitializeHotkey()
    {
        var hotkeyService = Services.GetRequiredService<GlobalHotkeyService>();
        var navigation = Services.GetRequiredService<INavigationService>();
        var (modifiers, key) = SettingsService.ReadHotkey();
        if (key != Key.None)
        {
            if (!hotkeyService.Register(modifiers, key, navigation.StartCapture))
            {
                AppMessageBox.Show(
                    string.Format("全局快捷键 {0} 注册失败，可能已被其他程序占用。\n请在设置中更换快捷键。",
                        HotkeyDisplayFormatter.FormatCompact(modifiers, key)),
                    "快捷键注册失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private Icon? LoadTrayIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppPaths.InstallDirectory, "Assets", "icons", "app.ico");
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