using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PixSnap.Behaviors;

/// <summary>Slider 拖拽开始/结束命令绑定。</summary>
public static class SliderDragBehavior
{
    public static readonly DependencyProperty DragStartedCommandProperty =
        DependencyProperty.RegisterAttached(
            "DragStartedCommand",
            typeof(ICommand),
            typeof(SliderDragBehavior),
            new PropertyMetadata(null, OnCommandPropertyChanged));

    public static readonly DependencyProperty DragCompletedCommandProperty =
        DependencyProperty.RegisterAttached(
            "DragCompletedCommand",
            typeof(ICommand),
            typeof(SliderDragBehavior),
            new PropertyMetadata(null, OnCommandPropertyChanged));

    public static ICommand? GetDragStartedCommand(DependencyObject obj) =>
        (ICommand?)obj.GetValue(DragStartedCommandProperty);

    public static void SetDragStartedCommand(DependencyObject obj, ICommand? value) =>
        obj.SetValue(DragStartedCommandProperty, value);

    public static ICommand? GetDragCompletedCommand(DependencyObject obj) =>
        (ICommand?)obj.GetValue(DragCompletedCommandProperty);

    public static void SetDragCompletedCommand(DependencyObject obj, ICommand? value) =>
        obj.SetValue(DragCompletedCommandProperty, value);

    private static void OnCommandPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider)
            return;

        slider.Loaded -= OnSliderLoaded;
        slider.Loaded += OnSliderLoaded;
        AttachThumbHandlers(slider);
    }

    private static void OnSliderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Slider slider)
            AttachThumbHandlers(slider);
    }

    private static void AttachThumbHandlers(Slider slider)
    {
        if (FindVisualChild<Thumb>(slider) is not { } thumb)
            return;

        thumb.DragStarted -= OnDragStarted;
        thumb.DragCompleted -= OnDragCompleted;
        thumb.DragStarted += OnDragStarted;
        thumb.DragCompleted += OnDragCompleted;
    }

    private static void OnDragStarted(object sender, DragStartedEventArgs e)
    {
        if (FindParent<Slider>(sender as DependencyObject) is { } slider)
            GetDragStartedCommand(slider)?.Execute(null);
    }

    private static void OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (FindParent<Slider>(sender as DependencyObject) is { } slider)
            GetDragCompletedCommand(slider)?.Execute(null);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
                return match;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
