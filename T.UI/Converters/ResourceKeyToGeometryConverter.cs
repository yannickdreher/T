using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace T.UI.Converters;

public class ResourceKeyToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && Application.Current is { } app)
            if (app.TryGetResource(key, app.ActualThemeVariant, out var resource) && resource is StreamGeometry geo)
                return geo;

        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}