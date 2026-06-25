using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Remex.Desktop.Converters;

/// <summary>
/// Converts any enum value to its localized string representation by looking up "EnumName_Value" in Strings.resx.
/// </summary>
public class EnumLocalizerConverter : IValueConverter
{
    public static readonly EnumLocalizerConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null) return null;
        string key = $"{value.GetType().Name}_{value}";
        return Services.LocalizationService.Instance[key] ?? value.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
