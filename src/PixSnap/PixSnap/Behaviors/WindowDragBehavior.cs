using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PixSnap.Behaviors;

/// <summary>在无边框窗口上拖动指定元素以移动窗口。</summary>
public static class WindowDragBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(WindowDragBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        if ((bool)e.NewValue)
            element.MouseLeftButtonDown += OnMouseLeftButtonDown;
        else
            element.MouseLeftButtonDown -= OnMouseLeftButtonDown;
    }

    private static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement element)
            return;

        if (Window.GetWindow(element) is { } window)
            window.DragMove();
    }
}
