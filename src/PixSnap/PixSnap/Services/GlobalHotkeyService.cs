using Serilog;
using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace PixSnap.Services;

/// <summary>
/// 注册/注销系统级全局快捷键。
/// 内部使用消息专用窗口（HWND_MESSAGE）接收 WM_HOTKEY，不影响主窗口消息处理。
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x4E10;
    // WS_POPUP 是创建消息窗口所需的最小窗口样式
    private const int WS_POPUP = unchecked((int)0x80000000);
    // HWND_MESSAGE：使窗口成为消息专用窗口，不显示在任务栏和屏幕上
    private const int HWND_MESSAGE = -3;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _msgWindow;
    private Action? _callback;
    private bool _registered;

    public GlobalHotkeyService()
    {
        // 创建不可见的消息专用窗口，专门用于接收 WM_HOTKEY
        var param = new HwndSourceParameters("PixSnapHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = WS_POPUP,
            ParentWindow = new IntPtr(HWND_MESSAGE)
        };
        _msgWindow = new HwndSource(param);
        _msgWindow.AddHook(WndProc);
    }

    /// <summary>
    /// 注册全局快捷键。若当前已注册则先注销旧的再注册新的。
    /// </summary>
    /// <returns>注册是否成功。Key.None 时直接返回 false 不尝试注册。</returns>
    public bool Register(ModifierKeys modifiers, Key key, Action onPressed)
    {
        if (_msgWindow is null || key == Key.None) return false;

        Unregister();
        _callback = onPressed;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        _registered = RegisterHotKey(_msgWindow.Handle, HotkeyId, (uint)modifiers, vk);
        if (_registered)
            Log.Information("全局快捷键注册成功: {Modifiers}+{Key}", modifiers, key);
        else
            Log.Warning("全局快捷键注册失败: {Modifiers}+{Key}", modifiers, key);
        return _registered;
    }

    /// <summary>注销当前已注册的快捷键。</summary>
    public void Unregister()
    {
        if (_registered && _msgWindow != null)
        {
            UnregisterHotKey(_msgWindow.Handle, HotkeyId);
            Log.Debug("全局快捷键已注销");
            _registered = false;
        }
        _callback = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && (int)wParam == HotkeyId)
        {
            _callback?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _msgWindow?.Dispose();
        _msgWindow = null;
        Log.Debug("GlobalHotkeyService 已释放");
    }
}
