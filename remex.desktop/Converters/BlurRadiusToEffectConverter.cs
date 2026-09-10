using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Remex.Desktop.Converters;

/// <summary>A blur radius in device pixels to the <see cref="BlurEffect"/> the wallpaper Image carries.
/// A radius of 0 yields null (no effect at all) so an unblurred wallpaper costs nothing.</summary>
public sealed class BlurRadiusToEffectConverter : IValueConverter
{
    public static readonly BlurRadiusToEffectConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var radius = value is double d && !double.IsNaN(d) ? Math.Max(0.0, d) : 0.0;
        return radius <= 0.0 ? null : new BlurEffect { Radius = radius };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
