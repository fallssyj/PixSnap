using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Helpers;
using Microsoft.Win32;
using System.Windows.Threading;

namespace PixSnap.Services;

internal static class ThemeHelper
{
    private static int _themeIndex;
    private static readonly object HookLock = new();
    private static bool _hooksRegistered;

    /// <summary>主题应用完成后触发（含设置页实时预览）。</summary>
    public static event Action<int>? ThemeApplied;

    /// <summary>0 = Auto, 1 = Dark, 2 = Light。</summary>
    public static void ApplyTheme(int themeIndex)
    {
        _themeIndex = themeIndex;
        EnsureHooks();

        var themeManager = ThemeManager.Current;

        switch (themeIndex)
        {
            case 1:
                themeManager.ApplicationTheme = ApplicationTheme.Dark;
                break;
            case 2:
                themeManager.ApplicationTheme = ApplicationTheme.Light;
                break;
            default:
                themeManager.ApplicationTheme = ResolveSystemTheme();
                break;
        }

        ThemeApplied?.Invoke(themeIndex);
    }

    private static void EnsureHooks()
    {
        lock (HookLock)
        {
            if (_hooksRegistered)
                return;

            _hooksRegistered = true;
            ColorsHelper.Current.SystemThemeChanged += (_, _) =>
            {
                if (_themeIndex == 0)
                    ApplyAutoThemeOnUiThread();
            };
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _themeIndex == 0)
                    ApplyAutoThemeOnUiThread();
                else if (e.Category == UserPreferenceCategory.Color)
                    NotifyThemeResourcesChanged();
            };
        }
    }

    private static void NotifyThemeResourcesChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        if (dispatcher.CheckAccess())
            ThemeApplied?.Invoke(_themeIndex);
        else
            dispatcher.BeginInvoke(() => ThemeApplied?.Invoke(_themeIndex), DispatcherPriority.Normal);
    }

    private static void ApplyAutoThemeOnUiThread()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        if (dispatcher.CheckAccess())
            ApplyResolvedSystemTheme();
        else
            dispatcher.BeginInvoke(ApplyResolvedSystemTheme, DispatcherPriority.Normal);
    }

    private static void ApplyResolvedSystemTheme()
    {
        ThemeManager.Current.ApplicationTheme = ResolveSystemTheme();
        ThemeApplied?.Invoke(_themeIndex);
    }

    private static ApplicationTheme ResolveSystemTheme()
        => ColorsHelper.Current.SystemTheme ?? ReadSystemThemeFromRegistry();

    private static ApplicationTheme ReadSystemThemeFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int useLight && useLight == 0)
                return ApplicationTheme.Dark;
        }
        catch
        {
            // 忽略注册表读取失败
        }

        return ApplicationTheme.Light;
    }
}
