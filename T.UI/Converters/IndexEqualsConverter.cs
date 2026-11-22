using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace T.UI.Converters;

/// <summary>True when the bound value equals the parameter (an index, or the text of a string or enum value).</summary>
public class IndexEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int index && parameter is string paramStr && int.TryParse(paramStr, out int target))
            return index == target;
        return value is not null && parameter is not null &&
               string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
