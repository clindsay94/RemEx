using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;
using Remex.Core.Messages;

namespace Remex.Desktop.Converters;

/// <summary>
/// Colours a sensor reading by severity — normal, warm, or too hot — from the active theme.
/// </summary>
/// <remarks>
/// The three colours used to be hardcoded, which meant a reading that had crossed a threshold was
/// shown in a fixed red or amber regardless of what it was sitting on. SolarFlare's near-white
/// surface is the case that makes this visible, the same one that made the log list unreadable
/// (RemEx-8tt2). Mapping onto the theme's own warning and error brushes keeps the meaning — amber
/// is warm, red is too hot — while letting each theme pick a contrast that works against its own
/// background. (RemEx-xw1u.)
/// <para>
/// Resolved per call rather than in a static field so a runtime theme switch is picked up, and a
/// missing key degrades to the previous literal rather than to null, because an invisible reading
/// is worse than an off-palette one. Neither of those should ever be needed — all four themes
/// define all three keys — but a converter is the wrong place to discover otherwise.
/// </para>
/// </remarks>
public class SensorColorConverter : IValueConverter
{
    public static readonly SensorColorConverter Instance = new();

    private static readonly SolidColorBrush FallbackNeutral = new(Color.Parse("#E0E0FF"));
    private static readonly SolidColorBrush FallbackRed = new(Color.Parse("#FF6B6B"));
    private static readonly SolidColorBrush FallbackYellow = new(Color.Parse("#FFAA00"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SensorReading reading)
            return Neutral();

        if (reading.Unit.Contains("°C") || reading.Unit.Contains("°F"))
        {
            if (reading.Value >= 85)
                return TooHot();
            if (reading.Value >= 75)
                return Warm();
        }
        else if (reading.Unit == "%")
        {
            if (reading.Value >= 90)
                return TooHot();
        }

        return Neutral();
    }

    private static IBrush Neutral() => Resolve("TextPrimaryBrush", FallbackNeutral);

    private static IBrush Warm() => Resolve("SystemWarningBrush", FallbackYellow);

    private static IBrush TooHot() => Resolve("SystemErrorBrush", FallbackRed);

    /// <summary>Looks <paramref name="key"/> up in the active theme, falling back if absent.</summary>
    private static IBrush Resolve(string key, IBrush fallback)
    {
        var app = Application.Current;
        if (app is null)
            return fallback;

        // TryGetResource, not the indexer: a missing key must degrade rather than throw inside a
        // converter. Note TryFindResource does not exist on Application in this Avalonia version.
        return app.TryGetResource(key, app.ActualThemeVariant, out var found) && found is IBrush brush
            ? brush
            : fallback;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
