using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace PixSnap.Converters;

public sealed class IntPtrToBitmapSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value as BitmapSource;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}