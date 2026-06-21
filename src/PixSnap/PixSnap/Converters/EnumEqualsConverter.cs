using System.Globalization;
using System.Windows.Data;

namespace PixSnap.Converters;

/// <summary>比较绑定值是否等于 ConverterParameter 指定的枚举值。</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
            return false;

        if (value.GetType().IsEnum && parameter is string paramStr)
        {
            if (Enum.TryParse(value.GetType(), paramStr, ignoreCase: true, out var parsed))
                return Equals(value, parsed);
        }

        return Equals(value.ToString(), parameter.ToString());
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
