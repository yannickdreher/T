using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;
using T.Models;

namespace T.Converters;

public class ConnectionStatusToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ConnectionStatus status)
            return Brushes.Gray;

        return status switch
        {
            ConnectionStatus.Connected => Brushes.LimeGreen,
            ConnectionStatus.Connecting => Brushes.Orange,
            ConnectionStatus.Reconnecting => Brushes.DarkOrange,
            ConnectionStatus.Disconnecting => Brushes.LightGray,
            ConnectionStatus.Disconnected => Brushes.Gray,
            _ => Brushes.Gray
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}