using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PixSnap.Services;

/// <summary>托盘右键菜单 Popup 的创建、定位与主题刷新。</summary>
public sealed class TrayMenuService : IDisposable
{
    private Popup? _popup;

    public TrayMenuService()
    {
        ThemeHelper.ThemeApplied += OnThemeApplied;
    }

    public void Show(UserControl menu)
    {
        _popup ??= CreatePopup();
        _popup.Child = menu;
        ApplyCursorPlacement(_popup);
        _popup.IsOpen = true;
    }

    public void Close()
    {
        if (_popup is null)
            return;

        _popup.IsOpen = false;
        _popup.Child = null;
        _popup = null;
    }

    public void Dispose()
    {
        ThemeHelper.ThemeApplied -= OnThemeApplied;
        Close();
    }

    private void OnThemeApplied(int _) => Close();

    private static Popup CreatePopup() => new()
    {
        AllowsTransparency = true,
        StaysOpen = false,
        Placement = PlacementMode.AbsolutePoint,
        PopupAnimation = PopupAnimation.Fade
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point pt);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>
    /// Hardcodet 使用 Win32 像素坐标；PlacementMode.AbsolutePoint 需要 WPF DIP。
    /// </summary>
    private static void ApplyCursorPlacement(Popup popup)
    {
        GetCursorPos(out var pt);
        var dpi = GetDpiForSystem() / 96.0;
        popup.Placement = PlacementMode.AbsolutePoint;
        popup.HorizontalOffset = pt.X / dpi;
        popup.VerticalOffset = pt.Y / dpi;
    }
}
