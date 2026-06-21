using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PixSnap.Behaviors;

/// <summary>TreeView SelectedItem 双向绑定辅助（WPF 原生不支持）。</summary>
public static class TreeViewSelectedItemBehavior
{
    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.RegisterAttached(
            "SelectedItem",
            typeof(object),
            typeof(TreeViewSelectedItemBehavior),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedItemChanged));

    public static object? GetSelectedItem(DependencyObject obj) => obj.GetValue(SelectedItemProperty);
    public static void SetSelectedItem(DependencyObject obj, object? value) => obj.SetValue(SelectedItemProperty, value);

    private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeView treeView)
            return;

        treeView.SelectedItemChanged -= OnTreeViewSelectedItemChanged;
        treeView.SelectedItemChanged += OnTreeViewSelectedItemChanged;
    }

    private static void OnTreeViewSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (sender is not TreeView treeView)
            return;

        if (Equals(GetSelectedItem(treeView), e.NewValue))
            return;

        SetSelectedItem(treeView, e.NewValue);
        BindingOperations.GetBindingExpression(treeView, SelectedItemProperty)?.UpdateSource();
    }
}
