using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PixSnap.Converters;

/// <summary>
/// 附加属性：将 TextBox 限制为只允许输入非负整数。
/// 用法：<TextBox local:NumericInputBehavior.IsEnabled="True" />
/// 也可在 Style 的 Setter 中设置。
/// </summary>
public static class NumericInputBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(NumericInputBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) =>
        (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) =>
        obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox textBox) return;

        if ((bool)e.NewValue)
        {
            textBox.PreviewTextInput    += OnPreviewTextInput;
            textBox.PreviewKeyDown      += OnPreviewKeyDown;
            DataObject.AddPastingHandler(textBox, OnPasting);
        }
        else
        {
            textBox.PreviewTextInput    -= OnPreviewTextInput;
            textBox.PreviewKeyDown      -= OnPreviewKeyDown;
            DataObject.RemovePastingHandler(textBox, OnPasting);
        }
    }

    private static readonly Regex _digitOnly = new(@"^\d+$", RegexOptions.Compiled);

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !_digitOnly.IsMatch(e.Text);
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 屏蔽空格键
        if (e.Key == Key.Space)
            e.Handled = true;
    }

    private static void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)))
        {
            var text = (string)e.DataObject.GetData(typeof(string))!;
            if (!_digitOnly.IsMatch(text))
                e.CancelCommand();
        }
        else
        {
            e.CancelCommand();
        }
    }
}
