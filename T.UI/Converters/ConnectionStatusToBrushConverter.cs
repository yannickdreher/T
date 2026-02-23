using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;
using T.Models;

namespace T.UI.Converters;

public sealed class ConnectionStatusToBrushConverter : IValueConverter
{
    public IBrush? DisconnectedBrush { get; set; }
    public IBrush? ConnectingBrush { get; set; }
    public IBrush? ConnectedBrush { get; set; }
    public IBrush? DisconnectingBrush { get; set; }
    public IBrush? ReconnectingBrush { get; set; }
    public IBrush? DefaultBrush { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ConnectionStatus status)
            return DefaultBrush ?? Brushes.Gray;

        return status switch
        {
            ConnectionStatus.Connected => ConnectedBrush ?? Brushes.LimeGreen,
            ConnectionStatus.Connecting => ConnectingBrush ?? Brushes.Gold,
            ConnectionStatus.Reconnecting => ReconnectingBrush ?? Brushes.Orange,
            ConnectionStatus.Disconnecting => DisconnectingBrush ?? Brushes.OrangeRed,
            ConnectionStatus.Disconnected => DisconnectedBrush ?? Brushes.Gray,
            _ => DefaultBrush ?? Brushes.Gray
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}