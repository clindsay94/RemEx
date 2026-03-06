using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Remex.Client.Converters;

/// <summary>
/// Formats sensor values: whole numbers for most units, 2 decimal places for voltage.
/// Usage: pass the sensor unit as the converter parameter.
/// </summary>
public class SensorValueConverter : IMultiValueConverter
{
    public static readonly SensorValueConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not double value)
            return "—";

        var unit = values[1] as string ?? "";

        // Voltage gets 2 decimal places
        if (unit.Contains("V", StringComparison.OrdinalIgnoreCase) && !unit.Contains("VRM", StringComparison.OrdinalIgnoreCase))
            return value.ToString("F2", culture);

        // Everything else is whole numbers
        return value.ToString("F0", culture);
    }
}
