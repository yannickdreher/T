using Avalonia;
using Avalonia.Data.Converters;
using System;
using System.Collections;
using System.Globalization;
using System.Linq;

namespace T.UI.Converters;

public sealed class FirstValidationErrorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IEnumerable errors)
        {
            var error = errors.Cast<object>().FirstOrDefault();
            if (error is null)
                return AvaloniaProperty.UnsetValue;

            var errorContent = error.GetType().GetProperty("ErrorContent")?.GetValue(error);
            return errorContent?.ToString() ?? error.ToString();
        }

        return AvaloniaProperty.UnsetValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        AvaloniaProperty.UnsetValue;
}