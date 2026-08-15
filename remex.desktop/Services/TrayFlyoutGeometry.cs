using Avalonia;

namespace Remex.Desktop.Services;

/// <summary>The tray flyout's persisted window state.</summary>
public sealed record TrayFlyoutGeometry
{
    public bool IsPinned { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

/// <summary>
/// Decides whether a saved <see cref="TrayFlyoutGeometry"/> is still usable on the screens that
/// exist right now, and clamps it to a sane size.
/// </summary>
/// <remarks>
/// SEPARATE FROM THE STORE, AND WITH NO FILE I/O, so the rule that matters most here can be tested
/// against a fabricated monitor layout rather than the tester's actual desktop. The rule that
/// matters most is the visibility check: a window restored onto a monitor that has since been
/// unplugged has no visible chrome to drag and no in-app recovery — the user's only option is to
/// find and delete a JSON file they do not know exists.
/// <para>
/// The check is an INTERSECTION against every screen, not a bounds test against the primary. A
/// second monitor to the left of the primary has negative coordinates, so "x is negative" is not
/// evidence of anything.
/// </para>
/// </remarks>
public static class TrayFlyoutGeometryValidator
{
    public const double MinWidth = 320;
    public const double MaxWidth = 900;
    public const double MinHeight = 240;
    public const double MaxHeight = 800;

    /// <summary>How much of the window must remain on some screen, in logical pixels, each axis.</summary>
    public const double MinVisible = 100;

    public static TrayFlyoutGeometry? Validate(
        TrayFlyoutGeometry? candidate,
        IReadOnlyList<PixelRect> workingAreas)
    {
        if (candidate is null)
            return null;

        if (!IsFinite(candidate.X) || !IsFinite(candidate.Y) ||
            !IsFinite(candidate.Width) || !IsFinite(candidate.Height))
            return null;

        // Clamp BEFORE judging visibility: a saved size below the minimum is grown here, and it
        // would be wrong to reject it for an overlap it only failed at its pre-clamp size.
        var clamped = candidate with
        {
            Width = Math.Clamp(candidate.Width, MinWidth, MaxWidth),
            Height = Math.Clamp(candidate.Height, MinHeight, MaxHeight),
        };

        foreach (var area in workingAreas)
        {
            var overlapX = Math.Min(clamped.X + clamped.Width, area.X + area.Width) - Math.Max(clamped.X, area.X);
            var overlapY = Math.Min(clamped.Y + clamped.Height, area.Y + area.Height) - Math.Max(clamped.Y, area.Y);

            if (overlapX >= MinVisible && overlapY >= MinVisible)
                return clamped;
        }

        return null;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
