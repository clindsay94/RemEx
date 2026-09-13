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

    // ═══ Column-fit constants (fix round 2) — the pieces DefaultWidth/MaxWidth's own promise
    // ("N columns of cards fit") is pinned against by TrayFlyoutGeometryTests, instead of the
    // promise living only in prose. Read the same sizes TrayFlyoutWindow.axaml uses:
    //   - ChromeSideInset: outer Border Margin="12" (12+12) + content Grid Margin="16" (16+16) = 56.
    //   - ScrollBarAllowance: worst case width the vertical ScrollBar can claim when it shows = 16.
    //   - CardPitch: one flyout-card's footprint — Border Width="200" + Margin="6" each side = 212.
    //   - CardsPanelInset: the cards ItemsControl's own Margin="6,0" (6+6) = 12.
    // A window fits N card columns when Width - ChromeSideInset - ScrollBarAllowance
    // >= N * CardPitch + CardsPanelInset (see TrayFlyoutGeometryTests.MaxWidthFitsFourCardColumns
    // / DefaultWidthFitsTwoCardColumns).
    public const double ChromeSideInset = 56;
    public const double ScrollBarAllowance = 16;
    public const double CardPitch = 212;
    public const double CardsPanelInset = 12;

    /// <summary>
    /// The transient popup's width (the same literal as <c>TrayFlyoutWindow.axaml</c>'s
    /// <c>Width="528"</c>). <c>TrayFlyoutWindow</c> ENFORCES this — not merely assumes it — by
    /// setting <c>Width = DefaultWidth</c> whenever it enters transient mode (its
    /// <c>ApplyMode(isPinned: false)</c>), because nothing else resets <c>Width</c> on unpin or in
    /// <c>ShowAtTray</c>'s transient branch (only <c>Position</c> is set there): a pinned window
    /// resized down to <c>TrayFlyoutGeometryValidator.MinWidth</c> (320, reachable through the
    /// resize grips) and then unpinned would otherwise stay 320 wide.
    /// </summary>
    /// <remarks>
    /// 528, NOT THE OLD 420 (fix round 2 — controller's eyes pass on the installed build). At 420
    /// the popup's own content width (420 − <see cref="ChromeSideInset"/> −
    /// <see cref="ScrollBarAllowance"/> = 348) fits only ONE 212px card column with the scrollbar
    /// showing, not two — the transient default rendered as a single scrolling column instead of a
    /// grid. 528's content width (528 − 56 − 16 = 456) clears two columns' <see cref="CardPitch"/>
    /// (2 × 212 + <see cref="CardsPanelInset"/> = 436) with headroom; pinned by
    /// <c>TrayFlyoutGeometryTests.DefaultWidthFitsTwoCardColumns</c>.
    /// <para>
    /// RemEx-4kv0g.18.5 REPLACED THE TILE GRID WITH A TOOLBAR ROW, so the paragraph this used to
    /// carry about <c>TrayTileColumnsConverter</c>'s column math no longer applies — that converter
    /// and <c>TrayTileColumnsTests</c> are deleted. The toolbar's own width promise is
    /// <see cref="ToolbarRowHeight"/>'s doc and <c>TrayFlyoutGeometryTests.ToolbarFitsSixItemsInOneRowAtDefaultWidth</c>:
    /// the six tiles (<c>Classes="tertiary icon-button"</c>, App.axaml's 32px default, 40px
    /// footprint each with their own <c>Margin="4"</c>) fit one <c>WrapPanel</c> row at this width
    /// with room to spare for a divider and shortcuts before it needs a second row.
    /// <see cref="CardsMaxHeight"/>'s own tile-row budget (<c>TilesMaxHeight</c>, 164, sized for the
    /// old 66px 2-row grid) is NOT re-derived by this change — RemEx-4kv0g.18.5 deliberately left it
    /// as-is, since the new toolbar's actual height is smaller, not larger, so the existing budget
    /// stays a safe (now overly generous) upper bound. The full re-derivation is RemEx-4kv0g.18.6.
    /// </para>
    /// </remarks>
    public const double DefaultWidth = 528;

    /// <summary>
    /// Height budget for one row of the toolbar row's icon buttons (Flyout D2 .1, RemEx-4kv0g.18.5)
    /// — introduced now so <c>CardsMaxHeight</c>'s full re-derivation (RemEx-4kv0g.18.6) has a named
    /// constant to build on, rather than a bare literal appearing in that task's diff with no
    /// history. Derivation: <c>Classes="tertiary icon-button"</c>'s App.axaml default (32px) + its
    /// own <c>Margin="4"</c> top and bottom (32 + 4 + 4 = 40), plus the toolbar
    /// <c>ItemsControl</c>'s own top margin (<c>Margin="0,16,0,0"</c>, 16) = 56. NOT YET WIRED INTO
    /// <see cref="CardsMaxHeight"/>'s own arithmetic — <c>CardsMaxHeight</c> keeps its
    /// RemEx-4kv0g.18.2 value unchanged this task (see <see cref="DefaultWidth"/>'s remarks for why
    /// that is still safe).
    /// </summary>
    public const double ToolbarRowHeight = 56;

    /// <summary>
    /// Cap on the pinned-sensor cards <c>ScrollViewer</c>'s height (<c>TrayFlyoutWindow.axaml</c>,
    /// the cards row) — TRANSIENT MODE ONLY (<c>SizeToContent.Height</c>, no user resize).
    /// </summary>
    /// <remarks>
    /// REQUIRED, NOT A NICETY (docs/REGRESSION-GUARDS.md). A height-sized window has no natural
    /// bound on an inner ScrollViewer, so without this constant a long pin list grows the popup
    /// past the screen and nothing throws.
    /// <para>
    /// PINNED MODE DOES NOT USE THIS CONSTANT (fix round 2). <c>ApplyMode</c> sets the cards
    /// ScrollViewer's <c>MaxHeight</c> to <c>double.PositiveInfinity</c> when pinned, so the
    /// star-sized cards row can take all the free height the user's resize leaves above the
    /// fixed-height tiles row — the earlier build still capped at 486 while pinned, leaving ~50px
    /// empty below the tiles on an 894×787 popup with rows hidden behind a needless scrollbar. The
    /// XAML <c>MaxHeight="{x:Static svc:TrayFlyoutGeometry.CardsMaxHeight}"</c> attribute is only
    /// the TRANSIENT starting value; <c>ApplyMode</c> overrides it every mode switch.
    /// </para>
    /// <para>
    /// Derivation, all logical px, from <c>TrayFlyoutWindow.axaml</c> as of RemEx-4kv0g.18.2, at
    /// <see cref="DefaultWidth"/> (528) — ENFORCED for the transient window by
    /// <c>TrayFlyoutWindow.ApplyMode(isPinned: false)</c>, not merely assumed, so 528 really is the
    /// only width this budget has to hold for:
    /// <code>
    ///   TrayFlyoutGeometryValidator.MaxHeight ................................ 800
    /// − outer Border margin (Margin="12" around the card, 12 top + 12 bottom) ... 24
    /// − content Grid margin (Margin="16", 16 top + 16 bottom) .................. 32
    /// − header row (badge/status text/icon buttons; conservative estimate that
    ///   leaves headroom for a larger Personalize → Text scale) .................. 56
    /// − cards row's own top margin (ScrollViewer Margin="0,12,0,0") .............. 12
    /// − tiles row at its tallest: 6 tiles at width 528 lay out 4 columns via
    ///   TrayTileColumnsConverter.ColumnsFor(528), i.e. still 2 rows (⌈6/4⌉ = 2) of Button
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
    /// <para>
    /// The flyout's geometry (position/size/pin state) persists to
    /// <c>C:\ProgramData\RemEx\tray_flyout_layout.json</c> — machine-wide, not per-user
    /// (<c>RemexDataPaths.ResolveDirectory</c> relocates Windows stores there; see
    /// <c>TrayFlyoutLayoutStore</c>). A stale saved width below <see cref="DefaultWidth"/> is not a
    /// concern for this budget: <c>ApplyMode(isPinned: false)</c> enforces <see cref="DefaultWidth"/>
    /// on every transient transition regardless of what was loaded from disk.
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

    // 944, not the old 900 (fix round 2 — controller's eyes pass on the installed build). At 900
    // the popup's content width (900 − TrayFlyoutGeometry.ChromeSideInset −
    // TrayFlyoutGeometry.ScrollBarAllowance = 828) falls short of four 212px card columns
    // (4 × TrayFlyoutGeometry.CardPitch + TrayFlyoutGeometry.CardsPanelInset = 860) by 32px, so the
    // widest pinned size only ever showed three columns. 944's content width (872) clears it with
    // room; pinned by TrayFlyoutGeometryTests.MaxWidthFitsFourCardColumns.
    public const double MaxWidth = 944;
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
