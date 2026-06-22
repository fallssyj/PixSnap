using PixSnap.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PixSnap.Behaviors;

/// <summary>将 TextBox 变为快捷键录制输入框，逻辑委托给 SettingsViewModel。</summary>
public static class HotkeyRecordBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(HotkeyRecordBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox textBox)
            return;

        if ((bool)e.NewValue)
        {
            textBox.PreviewKeyDown += OnPreviewKeyDown;
            textBox.GotFocus += OnGotFocus;
            textBox.LostFocus += OnLostFocus;
        }
        else
        {
            textBox.PreviewKeyDown -= OnPreviewKeyDown;
            textBox.GotFocus -= OnGotFocus;
            textBox.LostFocus -= OnLostFocus;
        }
    }

    private static SettingsViewModel? GetViewModel(DependencyObject element) =>
        (element as FrameworkElement)?.DataContext as SettingsViewModel;

    private static void OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.SelectionLength = 0;
            if (GetViewModel(textBox) is { } vm)
                vm.IsRecordingHotkey = true;
        }
    }

    private static void OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox)
            return;

        if (GetViewModel(textBox) is { } vm)
            vm.IsRecordingHotkey = false;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
            return;

        var vm = GetViewModel(textBox);
        if (vm is null || !vm.IsRecordingHotkey)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin)
            return;

        if (key == Key.Escape)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        vm.RecordKey(key, Keyboard.Modifiers);
        Keyboard.ClearFocus();
        e.Handled = true;
    }
}
