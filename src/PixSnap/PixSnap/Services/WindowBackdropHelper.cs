using iNKORE.UI.WPF.Modern.Controls.Helpers;
using iNKORE.UI.WPF.Modern.Helpers.Styles;
using System.Windows;

namespace PixSnap.Services;

/// <summary>应用 iNKORE <c>WindowHelper.SystemBackdropType</c> 到现代风格窗口。</summary>
internal static class WindowBackdropHelper
{
    private static readonly BackdropType[] BackdropOptions =
    [
        BackdropType.Mica,
        BackdropType.Acrylic,
        BackdropType.Tabbed,
        BackdropType.Acrylic10,
        BackdropType.Acrylic11,
        BackdropType.None
    ];

    private static int _backdropIndex;
    private static bool _hookRegistered;

    public static event Action<int>? BackdropApplied;

    public static IReadOnlyList<string> DisplayNames { get; } =
    [
        "Mica",
        "Acrylic",
        "Tabbed",
        "Acrylic（Windows 10）",
        "Acrylic（Windows 11）",
        "无"
    ];

    public static void EnsureHooks()
    {
        if (_hookRegistered)
            return;

        _hookRegistered = true;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
    }

    public static void ApplyBackdrop(int backdropIndex)
    {
        _backdropIndex = NormalizeIndex(backdropIndex);
        EnsureHooks();
        ApplyToAllOpenWindows();
        BackdropApplied?.Invoke(_backdropIndex);
    }

    public static void ApplyTo(Window window)
    {
        if (!UsesModernChrome(window))
            return;

        WindowHelper.SetSystemBackdropType(window, ToBackdropType(_backdropIndex));
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
            ApplyTo(window);
    }

    private static void ApplyToAllOpenWindows()
    {
        if (Application.Current is null)
            return;

        foreach (Window window in Application.Current.Windows)
            ApplyTo(window);
    }

    private static bool UsesModernChrome(Window window) =>
        WindowHelper.GetUseModernWindowStyle(window);

    private static int NormalizeIndex(int backdropIndex) =>
        backdropIndex < 0 || backdropIndex >= BackdropOptions.Length ? 0 : backdropIndex;

    private static BackdropType ToBackdropType(int backdropIndex) =>
        BackdropOptions[NormalizeIndex(backdropIndex)];
}
