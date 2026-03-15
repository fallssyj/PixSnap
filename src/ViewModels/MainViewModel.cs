// 编码：UTF-8 BOM
// 主窗口（托盘）ViewModel，管理系统托盘、全局快捷键和设置

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSnap.Models;
using PixSnap.Services;

namespace PixSnap.ViewModels;

/// <summary>主程序 ViewModel，负责托盘管理和全局协调</summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly HotkeyService _hotkeyService;
    private bool _disposed;

    [ObservableProperty]
    private AppSettings _settings = new();

    [ObservableProperty]
    private string _statusText = "就绪";

    /// <summary>呼出截图操作事件（由 App 层监听）</summary>
    public event Action? ShowCaptureRequested;

    /// <summary>显示图片处理窗口（携带截图数据）</summary>
    public event Action<System.Windows.Media.Imaging.BitmapSource>? ShowEditorRequested;

    /// <summary>显示主窗口</summary>
    public event Action? ShowMainWindowRequested;

    public MainViewModel()
    {
        _hotkeyService = new HotkeyService();
    }

    /// <summary>初始化热键服务（需要在 Window 加载后调用）</summary>
    public void InitializeHotkey(IntPtr hwnd)
    {
        _hotkeyService.Initialize(hwnd);
        _hotkeyService.CaptureHotkeyPressed += OnCaptureHotkeyPressed;
        bool ok = _hotkeyService.RegisterCaptureHotkey(Settings.Hotkey);
        StatusText = ok ? $"热键已注册：{Settings.Hotkey.DisplayText}" : "热键注册失败（可能冲突）";
    }

    /// <summary>重新注册热键（修改设置后调用）</summary>
    public bool ReregisterHotkey()
    {
        bool ok = _hotkeyService.RegisterCaptureHotkey(Settings.Hotkey);
        StatusText = ok ? $"热键已更新：{Settings.Hotkey.DisplayText}" : "热键注册失败（可能冲突）";
        return ok;
    }

    private void OnCaptureHotkeyPressed()
    {
        ShowCaptureRequested?.Invoke();
    }

    /// <summary>截图完成后调用编辑器</summary>
    public void OpenEditorWithCapture(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        ShowEditorRequested?.Invoke(bitmap);
    }

    [RelayCommand]
    private void ShowMainWindow() => ShowMainWindowRequested?.Invoke();

    [RelayCommand]
    private void TriggerCapture() => ShowCaptureRequested?.Invoke();

    [RelayCommand]
    private void ExitApp()
    {
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _hotkeyService.Dispose();
            _disposed = true;
        }
    }
}
