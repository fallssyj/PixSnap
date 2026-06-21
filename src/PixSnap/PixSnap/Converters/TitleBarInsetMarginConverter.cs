using iNKORE.UI.WPF.Modern.Controls.Primitives;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PixSnap.Converters;

/// <summary>
/// 将窗口标题栏系统按钮占位转换为 Margin，避免自定义工具栏与最小化/最大化/关闭按钮重叠。
/// </summary>
public sealed class TitleBarInsetMarginConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Window window)
            return new Thickness(0);

        var left = TitleBar.GetSystemOverlayLeftInset(window);
        var right = TitleBar.GetSystemOverlayRightInset(window);
        return new Thickness(left, 0, right, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
