using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace T.UI.Converters;

public class BoolToBrushConverter : IValueConverter
{
    public IBrush? TrueBrush { get; set; }
    public IBrush? FalseBrush { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return boolValue ? TrueBrush : FalseBrush;
        }
        return FalseBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}