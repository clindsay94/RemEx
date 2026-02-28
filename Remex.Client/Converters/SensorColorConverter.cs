using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;
using Remex.Core.Messages;

namespace Remex.Client.Converters;

public class SensorColorConverter : IValueConverter
{
    public static readonly SensorColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SensorReading reading)
            return new SolidColorBrush(Color.Parse("#E0E0FF"));

        // Default neutral color
        var brush = new SolidColorBrush(Color.Parse("#E0E0FF"));

        if (reading.Unit.Contains("°C") || reading.Unit.Contains("°F"))
        {
            if (reading.Value >= 85)
                return new SolidColorBrush(Color.Parse("#FF6B6B")); // Red
            if (reading.Value >= 75)
                return new SolidColorBrush(Color.Parse("#FFAA00")); // Yellow
        }
        else if (reading.Unit == "%")
        {
            if (reading.Value >= 90)
                return new SolidColorBrush(Color.Parse("#FF6B6B")); // Red
        }

        return brush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
