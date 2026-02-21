using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace T.UI.Converters;

public class IndexEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int index && parameter is string paramStr && int.TryParse(paramStr, out int target))
            return index == target;
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}