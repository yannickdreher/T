using Avalonia;
using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace T.UI.Converters;

public sealed class BoolNegationConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : AvaloniaProperty.UnsetValue;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : AvaloniaProperty.UnsetValue;
}