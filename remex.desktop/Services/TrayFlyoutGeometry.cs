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

    /// <summary>
    /// Cap on the pinned-sensor cards <c>ScrollViewer</c>'s height (<c>TrayFlyoutWindow.axaml</c>,
    /// the cards row) while the window is transient (<c>SizeToContent.Height</c>, no user resize).
    /// </summary>
    /// <remarks>
    /// REQUIRED, NOT A NICETY (docs/REGRESSION-GUARDS.md). A height-sized window has no natural
    /// bound on an inner ScrollViewer, so without this constant a long pin list grows the popup
    /// past the screen and nothing throws.
    /// <para>
    /// Derivation, all logical px, from <c>TrayFlyoutWindow.axaml</c> as of RemEx-4kv0g.18.2, at
    /// the transient window's fixed default <c>Width="420"</c> (transient is not user-resizable,
    /// so 420 is the only width this has to hold for):
    /// <code>
    ///   TrayFlyoutGeometryValidator.MaxHeight ................................ 800
    /// − outer Border margin (Margin="12" around the card, 12 top + 12 bottom) ... 24
    /// − content Grid margin (Margin="16", 16 top + 16 bottom) .................. 32
    /// − header row (badge/status text/icon buttons; conservative estimate that
    ///   leaves headroom for a larger Personalize → Text scale) .................. 56
    /// − cards row's own top margin (ScrollViewer Margin="0,12,0,0") .............. 12
    /// − tiles row at its tallest: 6 tiles at width 420 lay out 3 columns via
    ///   TrayTileColumnsConverter.ColumnsFor(420), i.e. 2 rows of Button
    ///   Height="66" Margin="4" (66 + 4+4 = 74 per row, ×2 = 148), plus the
    ///   ItemsControl's own top margin (Margin="0,16,0,0") .................... 164
    ///                                                                        ------
    ///                                                          fixed chrome total = 288
    ///   800 − 288 = 512, rounded DOWN to a whole number of card rows (200×150 cards,
    ///   Margin="6" each side ⇒ 150 + 6+6 = 162 px per row): floor(512 / 162) = 3 rows.
    ///   3 × 162 = 486.
    /// </code>
    /// The header-row estimate is the only soft number above — it depends on the user's
    /// Personalize → Text scale via <c>Typo.*</c> resources — and is deliberately generous so the
    /// derived cap stays a floor rather than a value that could clip. Re-derive by hand if the
    /// header row, the tile button height/margin, or the card size (200×150, Margin="6") change.
    /// </para>
    /// </remarks>
    public const double CardsMaxHeight = 486;
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
