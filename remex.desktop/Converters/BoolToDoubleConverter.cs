using System.Globalization;
using Avalonia.Data.Converters;

namespace Remex.Desktop.Converters;

/// <summary>Converts bool → double using a "trueValue|falseValue" ConverterParameter (e.g. '1.0|0.0').</summary>
public sealed class BoolToDoubleConverter : IValueConverter
{
    public static readonly BoolToDoubleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string)?.Split('|');
        double t = 1.0, f = 0.0;
        if (parts is { Length: 2 })
        {
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out t);
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out f);
        }
        return value is true ? t : f;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
