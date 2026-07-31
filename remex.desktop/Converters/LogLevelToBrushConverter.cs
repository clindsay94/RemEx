using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Microsoft.Extensions.Logging;

namespace Remex.Desktop.Converters;

/// <summary>
/// Maps a <see cref="LogLevel"/> to a severity colour for the diagnostic log list, resolved from the
/// active theme rather than hardcoded.
/// </summary>
/// <remarks>
/// This used to hold five fixed colours, justified by a comment claiming "the log panel always sits
/// on a dark glass background". That premise was false. On SolarFlare <c>GlassBaseDark</c> is
/// <c>#F8FAFC</c> — near-white — so the informational level's <c>#D1D5DB</c> rendered light grey on
/// white and the log list was barely readable on that theme (RemEx-8tt2).
/// <para>
/// The severity SEMANTICS are preserved, which is what that comment was really protecting: warnings
/// stay the theme's warning colour and errors its error colour, so amber-means-warning and
/// red-means-error still read identically everywhere. What changes is that each theme now supplies
/// its own version of those, at a contrast that works against its own background. The three
/// non-alerting levels map onto the text ramp — muted, secondary, primary — which is the visual
/// hierarchy the original greys were imitating.
/// </para>
/// <para>
/// Resolution happens per call rather than in a static field, and that is the live-theme-switch
/// story: the app can change theme at runtime, and a brush captured once at class-initialisation
/// time would freeze whichever theme happened to be active first. Looking it up on each conversion
/// means every re-evaluated binding picks up the current theme.
/// </para>
/// </remarks>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    public static readonly LogLevelToBrushConverter Instance = new();

    /// <summary>
    /// Last-resort colours, used only if a theme is missing one of the keys below.
    /// </summary>
    /// <remarks>
    /// All four themes define all five keys today, so these should never render. They exist because
    /// a converter returning null would leave the text invisible rather than merely mis-toned, and
    /// an unreadable log is worse than an off-palette one. The values are the ones this converter
    /// shipped with, so the fallback is a known-legible dark ramp rather than a guess.
    /// </remarks>
    private static readonly IBrush FallbackTrace = new SolidColorBrush(Color.Parse("#6B7280"));

    private static readonly IBrush FallbackDebug = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush FallbackInfo = new SolidColorBrush(Color.Parse("#D1D5DB"));
    private static readonly IBrush FallbackWarn = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush FallbackError = new SolidColorBrush(Color.Parse("#F43F5E"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogLevel.Trace => Resolve("TextMutedBrush", FallbackTrace),
        LogLevel.Debug => Resolve("TextSecondaryBrush", FallbackDebug),
        LogLevel.Information => Resolve("TextPrimaryBrush", FallbackInfo),
        LogLevel.Warning => Resolve("SystemWarningBrush", FallbackWarn),
        LogLevel.Error or LogLevel.Critical => Resolve("SystemErrorBrush", FallbackError),
        _ => Resolve("TextPrimaryBrush", FallbackInfo),
    };

    /// <summary>Looks <paramref name="key"/> up in the active theme, falling back if absent.</summary>
    private static IBrush Resolve(string key, IBrush fallback)
    {
        var app = Application.Current;
        if (app is null)
            return fallback;

        // TryGetResource rather than the indexer: a missing key must degrade to the fallback, not
        // throw inside a converter, where an exception would take out the whole log list. The
        // theme variant is passed explicitly so the lookup follows the app's ACTUAL variant rather
        // than whatever the resource host would default to.
        return app.TryGetResource(key, app.ActualThemeVariant, out var found) && found is IBrush brush
            ? brush
            : fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
