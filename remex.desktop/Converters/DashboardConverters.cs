using System.Globalization;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Data.Converters;
using Avalonia.Styling;

namespace Remex.Desktop.Converters;

/// <summary>
/// Converts a boolean value to one of two colors.
/// Pass "TrueColor|FalseColor" as the parameter (e.g. "#4ADE80|#FF6B6B").
/// </summary>
public class BoolToColorConverter : IValueConverter
{
    public static readonly BoolToColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && parameter is string s)
        {
            var parts = s.Split('|');
            if (parts.Length == 2)
            {
                return Avalonia.Media.Color.Parse(b ? parts[0] : parts[1]);
            }
        }
        return Avalonia.Media.Colors.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts an integer index to an opacity. If the value equals the parameter, returns 1.0; otherwise 0.3.
/// Used for page indicator dots.
/// </summary>
public class IndexToOpacityConverter : IValueConverter
{
    public static readonly IndexToOpacityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int index && parameter is string s && int.TryParse(s, out var target))
        {
            return index == target ? 1.0 : 0.3;
        }
        return 0.3;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts an integer to a boolean: true when the value equals the integer parsed from
/// the string ConverterParameter. Fixes the type-mismatch bug where ObjectConverters.Equal
/// compares int to string and always returns false.
/// </summary>
public class IntEqualConverter : IValueConverter
{
    public static readonly IntEqualConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int index && parameter is string s && int.TryParse(s, out var target))
            return index == target;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Inverse of <see cref="IntEqualConverter"/>: true when the value does NOT equal the parameter.
/// </summary>
public class IntNotEqualConverter : IValueConverter
{
    public static readonly IntNotEqualConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int index && parameter is string s && int.TryParse(s, out var target))
            return index != target;
        return true;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Bridges <c>ShellViewModel.IsReducedMotion</c> to the tutorial Carousel's PageTransition
/// (RemEx-9iz00.1): a short Material-eased horizontal slide normally, or <c>null</c> - no
/// transition at all - when the user has asked for reduced motion.
/// </summary>
public class BoolToPageTransitionConverter : IValueConverter
{
    public static readonly BoolToPageTransitionConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true)
            return null;

        var easing = new CubicEaseOut();
        return new PageSlide(TimeSpan.FromMilliseconds(250), PageSlide.SlideAxis.Horizontal)
        {
            SlideInEasing = easing,
            SlideOutEasing = easing,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a boolean to one of two strings.
/// Pass "TrueString|FalseString" as the parameter (e.g. "✓|✕").
/// </summary>
public class BoolToStringConverter : IValueConverter
{
    public static readonly BoolToStringConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && parameter is string s)
        {
            var parts = s.Split('|');
            return b ? parts[0] : (parts.Length > 1 ? parts[1] : string.Empty);
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a double (latency ms) to a bar height for the mini chart.
/// Clamps to a reasonable pixel range (2–60px).
/// </summary>
public class LatencyToHeightConverter : IValueConverter
{
    public static readonly LatencyToHeightConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double ms)
        {
            // Scale: 0ms → 2px, 100ms → 60px (clamped)
            var height = Math.Clamp(ms / 100.0 * 60.0, 2.0, 60.0);
            return height;
        }
        return 2.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Multiplies a bound double by a fixed factor parsed from the ConverterParameter, clamped to
/// [0, 1]. Used to scale the Mica/Acrylic canvas base-tint veils by Customization.GlassOpacity
/// (RemEx-mmrgc): "Clear" (0.01) leaves the OS backdrop almost bare, "Frosted" (1.0) reproduces
/// the fixed ceiling passed as ConverterParameter rather than going fully opaque.
/// </summary>
public class MultiplyConverter : IValueConverter
{
    public static readonly MultiplyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d &&
            parameter is string s &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor))
        {
            return Math.Clamp(d * factor, 0.0, 1.0);
        }
        return 0.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Opacity of the surface veil DashboardBackgroundControl lays over a wallpaper or an Acrylic
/// backdrop. Inputs: [0] the user's opacity knob (Customization.AppWindowOpacity or
/// GlassOpacity), [1] the rectangle's ActualThemeVariant. ConverterParameter is the ceiling,
/// multiplied in exactly as <see cref="MultiplyConverter"/> does. Then, only when the palette
/// resolved LIGHT, the result is raised to <see cref="LightFloor"/> (RemEx-4kv0g.5.1).
/// </summary>
/// <remarks>
/// GlassBaseDarkBrush is the solved Surface, so in Light mode the veil is a light sheet — but at
/// GlassOpacity × 0.25 it is far too thin to lift a dark wallpaper or a dark Acrylic backdrop,
/// and the light palette's dark onSurface ink lands on a dark ground on every view. Dark mode is
/// untouched on purpose: its ceilings were set by measurement (DashboardBackdropTintTests) and a
/// heavier dark veil is the exact change that once made Mica look dead. Bad or unresolved
/// inputs fall to "no veil" in Dark (the property default, 1.0, would be an opaque sheet);
/// Light still gets its floor, since the floor IS the fix. The Mica veil does NOT use this
/// converter: DWM already paints Mica light under a light theme (MicaBackdrop.TryApply sets
/// immersive-dark from the theme), so it needs no floor and a floor would bury it.
/// </remarks>
public class VeilOpacityConverter : IMultiValueConverter
{
    public static readonly VeilOpacityConverter Instance = new();

    /// <summary>
    /// Minimum veil alpha once the palette resolves Light: a readable light surface behind the
    /// cards that still lets the wallpaper or backdrop show through. Re-measure before moving it.
    /// </summary>
    public const double LightFloor = 0.55;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var knob = values.Count > 0 && values[0] is double d ? d : 0.0;
        var factor = parameter is string s &&
                     double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
            ? f
            : 0.0;
        var veil = Math.Clamp(knob * factor, 0.0, 1.0);

        var isLight = values.Count > 1 && values[1] is ThemeVariant variant && variant == ThemeVariant.Light;
        return isLight ? Math.Max(veil, LightFloor) : veil;
    }
}

/// <summary>
/// Converts a CornerRadius to a Thickness (padding/margin) that scales with the corner arc,
/// ensuring card content never visually crowds the rounded edges.
/// Formula: max(16, maxCorner * 0.7) so large radii get proportionally more breathing room.
/// </summary>
public class CornerRadiusToMarginConverter : IValueConverter
{
    public static readonly CornerRadiusToMarginConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Avalonia.CornerRadius cr)
        {
            var max = Math.Max(cr.TopLeft, Math.Max(cr.TopRight, Math.Max(cr.BottomLeft, cr.BottomRight)));
            var margin = Math.Max(16.0, max * 0.7);
            return new Avalonia.Thickness(margin);
        }
        return new Avalonia.Thickness(16);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
