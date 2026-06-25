using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;
using Remex.Core.Messages;

namespace Remex.Desktop.Converters;

public class SensorColorConverter : IValueConverter
{
    public static readonly SensorColorConverter Instance = new();

    private static readonly SolidColorBrush NeutralBrush = new(Color.Parse("#E0E0FF"));
    private static readonly SolidColorBrush RedBrush = new(Color.Parse("#FF6B6B"));
    private static readonly SolidColorBrush YellowBrush = new(Color.Parse("#FFAA00"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SensorReading reading)
            return NeutralBrush;

        if (reading.Unit.Contains("°C") || reading.Unit.Contains("°F"))
        {
            if (reading.Value >= 85)
                return RedBrush;
            if (reading.Value >= 75)
                return YellowBrush;
        }
        else if (reading.Unit == "%")
        {
            if (reading.Value >= 90)
                return RedBrush;
        }

        return NeutralBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
