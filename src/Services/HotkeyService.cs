// 编码：UTF-8 BOM
// 全局快捷键服务，使用 Win32 RegisterHotKey 实现

using System.Windows.Interop;
using PixSnap.Helpers;
using PixSnap.Models;

namespace PixSnap.Services;

/// <summary>全局热键服务，负责注册/注销系统级快捷键</summary>
public class HotkeyService : IDisposable
{
    private const int HOTKEY_ID_CAPTURE = 9001;

    private HwndSource? _hwndSource;
    private bool _disposed;

    /// <summary>截图快捷键被触发时的事件</summary>
    public event Action? CaptureHotkeyPressed;

    /// <summary>初始化热键服务，需要传入宿主窗口句柄</summary>
    public void Initialize(IntPtr hwnd)
    {
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
    }

    /// <summary>注册截图全局热键</summary>
    public bool RegisterCaptureHotkey(HotkeyConfig config)
    {
        if (_hwndSource == null) return false;

        // 先注销旧的热键
        UnregisterCaptureHotkey();

        uint modifiers = config.Modifiers | NativeMethods.MOD_NOREPEAT;
        bool result = NativeMethods.RegisterHotKey(_hwndSource.Handle, HOTKEY_ID_CAPTURE, modifiers, config.Key);
        return result;
    }

    /// <summary>注销截图全局热键</summary>
    public void UnregisterCaptureHotkey()
    {
        if (_hwndSource != null)
        {
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID_CAPTURE);
        }
    }

    /// <summary>WndProc 消息处理</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HOTKEY_ID_CAPTURE)
            {
                CaptureHotkeyPressed?.Invoke();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            UnregisterCaptureHotkey();
            _hwndSource?.RemoveHook(WndProc);
            _disposed = true;
        }
    }
}
