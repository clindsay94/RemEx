using Avalonia;
using Remex.Desktop.Services;

namespace Remex.Desktop.Tests;

public class TrayFlyoutGeometryTests
{
    // A single 1920x1080 monitor at the origin.
    private static readonly IReadOnlyList<PixelRect> SingleScreen =
        [new PixelRect(0, 0, 1920, 1080)];

    // A second monitor to the LEFT of the primary, which is where negative
    // coordinates legitimately come from — the case a naive "x >= 0" check breaks.
    private static readonly IReadOnlyList<PixelRect> DualScreen =
        [new PixelRect(0, 0, 1920, 1080), new PixelRect(-1920, 0, 1920, 1080)];

    // 400, not the old 380 (Flyout D2 .2, RemEx-4kv0g.18.6): MinHeight rose from 240 to 382, and a
    // "fully visible, returned unchanged" fixture below MinHeight would get clamped UP by Validate,
    // silently changing what these tests actually assert.
    private static TrayFlyoutGeometry Geometry(double x, double y, double w = 460, double h = 400) =>
        new() { IsPinned = true, X = x, Y = y, Width = w, Height = h };

    [Fact]
    public void Null_candidate_returns_null()
    {
        Assert.Null(TrayFlyoutGeometryValidator.Validate(null, SingleScreen));
    }

    [Fact]
    public void Fully_visible_rect_is_returned_unchanged()
    {
        var input = Geometry(400, 300);
        var result = TrayFlyoutGeometryValidator.Validate(input, SingleScreen);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Oversize_is_clamped_to_the_maximum()
    {
        var result = TrayFlyoutGeometryValidator.Validate(Geometry(0, 0, 5000, 4000), SingleScreen);
        Assert.NotNull(result);
        Assert.Equal(TrayFlyoutGeometryValidator.MaxWidth, result!.Width);
        Assert.Equal(TrayFlyoutGeometryValidator.MaxHeight, result.Height);
    }

    [Fact]
    public void Undersize_is_clamped_to_the_minimum()
    {
        var result = TrayFlyoutGeometryValidator.Validate(Geometry(0, 0, 10, 10), SingleScreen);
        Assert.NotNull(result);
        Assert.Equal(TrayFlyoutGeometryValidator.MinWidth, result!.Width);
        Assert.Equal(TrayFlyoutGeometryValidator.MinHeight, result.Height);
    }

    [Fact]
    public void Rect_entirely_off_the_only_screen_is_rejected()
    {
        // The disconnected-monitor case: saved on a screen that no longer exists.
        Assert.Null(TrayFlyoutGeometryValidator.Validate(Geometry(-4000, 300), SingleScreen));
    }

    [Fact]
    public void Rect_overlapping_by_less_than_the_minimum_is_rejected()
    {
        // 460 wide at x = 1920 - 50 leaves only 50px on screen; below MinVisible of 100.
        Assert.Null(TrayFlyoutGeometryValidator.Validate(Geometry(1870, 300), SingleScreen));
    }

    [Fact]
    public void Rect_hanging_below_the_screen_is_rejected_on_the_Y_axis_alone()
    {
        // The X axis is comfortably on screen; only Y is short. Without this the `overlapY` half of
        // the visibility check is never exercised, and deleting it breaks no test — which is how a
        // window dragged down behind the taskbar comes back with no grabbable title bar.
        Assert.Null(TrayFlyoutGeometryValidator.Validate(Geometry(400, 1030), SingleScreen));
    }

    [Fact]
    public void Rect_overlapping_by_exactly_the_minimum_is_accepted()
    {
        // 100px of the window remains on screen — the boundary, and it must pass.
        var result = TrayFlyoutGeometryValidator.Validate(Geometry(1820, 300), SingleScreen);
        Assert.NotNull(result);
    }

    [Fact]
    public void Rect_on_a_secondary_screen_at_negative_coordinates_is_preserved()
    {
        var input = Geometry(-1500, 200);
        var result = TrayFlyoutGeometryValidator.Validate(input, DualScreen);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Rect_is_rejected_when_no_screens_are_reported()
    {
        Assert.Null(TrayFlyoutGeometryValidator.Validate(Geometry(400, 300), []));
    }

    [Fact]
    public void Non_finite_coordinates_are_rejected()
    {
        Assert.Null(TrayFlyoutGeometryValidator.Validate(Geometry(double.NaN, 300), SingleScreen));
        Assert.Null(TrayFlyoutGeometryValidator.Validate(Geometry(400, double.PositiveInfinity), SingleScreen));
    }

    [Fact]
    public void Clamping_happens_before_the_visibility_check()
    {
        // A 10x10 rect near the right edge is offscreen-ish before clamping and comfortably
        // visible after. Clamp first, then judge — otherwise a tiny saved size is rejected
        // for a reason that no longer applies once it has been grown to the minimum.
        var result = TrayFlyoutGeometryValidator.Validate(Geometry(1700, 300, 10, 10), SingleScreen);
        Assert.NotNull(result);
        Assert.Equal(TrayFlyoutGeometryValidator.MinWidth, result!.Width);
    }

    // HeaderHeight and FixedMargins stay independent of TrayFlyoutGeometry's own derivation comment,
    // so the test below is a real check against the window's fixed-height chrome rather than a
    // tautology repeating the production constant's literals back at itself (RemEx-4kv0g.18.2).
    // Header is a conservative (generous) estimate — see CardsMaxHeight's XML doc for the full
    // derivation.
    private const double HeaderHeight = 56;

    // Fix round 1: the budget test omitted the 68px of window/Grid/cards-row margins the
    // derivation comment counts (24 outer Border + 32 content Grid + 12 cards row's own top
    // margin). Named so the full budget — not just header+toolbar — has to fit under MaxHeight.
    private const double FixedMargins = 68;

    // One flyout-card row's footprint (Border Width="200" Height="150" Margin="6"): 150 + 6 + 6.
    // Independent of TrayFlyoutGeometry.CardRowHeight for the same anti-tautology reason as
    // HeaderHeight/FixedMargins above.
    private const double CardRowHeight = 162;

    [Fact]
    public void CardsMaxHeightLeavesRoomForHeaderAndToolbarInsideMaxHeight()
    {
        // RemEx-4kv0g.18.6: the old tile grid's TilesMaxHeight (164, a local literal — no production
        // constant existed for it) is retired in favour of TrayFlyoutGeometry.ToolbarMaxHeight, which
        // DOES have a real, exact production constant now (the toolbar's height is two fixed-size
        // WrapPanel rows, not a variable-column grid) — so this budget test references it directly
        // rather than re-declaring the same number as a second local literal.
        Assert.True(
            TrayFlyoutGeometry.CardsMaxHeight + HeaderHeight + TrayFlyoutGeometry.ToolbarMaxHeight + FixedMargins
                <= TrayFlyoutGeometryValidator.MaxHeight);
    }

    // RemEx-4kv0g.18.6, closes RemEx-4kv0g.18.4: MinHeight must fit a header, a toolbar row (up to
    // two lines) and at least one full card row — the popup's entire reason to exist is the cards,
    // so a MinHeight that lets a resize hide every card row is a hole, not a floor.
    [Fact]
    public void MinHeightShowsOneCardRow()
    {
        Assert.True(
            TrayFlyoutGeometryValidator.MinHeight
                >= HeaderHeight + CardRowHeight + TrayFlyoutGeometry.ToolbarMaxHeight + FixedMargins);
    }

    // RemEx-4kv0g.18.5: the tile grid (UniformGrid + TrayTileColumnsConverter) is gone, replaced by
    // one WrapPanel toolbar row over TrayFlyoutViewModel.ToolbarItems. This test replaces
    // DefaultWidthYieldsFourTileColumns (which asserted the deleted converter's column count) with
    // the promise that actually matters now: the six action tiles fit ONE row at DefaultWidth
    // without wrapping, so the common case (no app shortcuts ticked) never pays for a second row.
    // Shortcuts/a divider only ever ADD width, so this is the minimum-width case, not the maximum.
    [Fact]
    public void ToolbarFitsSixItemsInOneRowAtDefaultWidth()
    {
        const int ToolbarItemCount = 6; // the six action tiles (lock/sleep/remote/send/pair/power)
        const double ToolbarItemFootprint = 40; // Classes="tertiary icon-button" 32px default + Margin="4" each side

        Assert.True(
            ToolbarItemCount * ToolbarItemFootprint
                <= TrayFlyoutGeometry.DefaultWidth - TrayFlyoutGeometry.ChromeSideInset);
    }

    // Fix round 2 (controller's eyes pass on the installed build): DefaultWidth/MaxWidth's "N card
    // columns fit" promise, pinned by arithmetic instead of left to prose that can drift from the
    // XAML. A window fits N columns when its content width (Width minus the chrome margins minus
    // room for the vertical scrollbar) covers N card footprints plus the cards panel's own inset —
    // see TrayFlyoutGeometry's ChromeSideInset/ScrollBarAllowance/CardPitch/CardsPanelInset XML
    // doc for where each number comes from.
    [Fact]
    public void DefaultWidthFitsTwoCardColumns()
    {
        Assert.True(
            TrayFlyoutGeometry.DefaultWidth - TrayFlyoutGeometry.ChromeSideInset - TrayFlyoutGeometry.ScrollBarAllowance
                >= 2 * TrayFlyoutGeometry.CardPitch + TrayFlyoutGeometry.CardsPanelInset);
    }

    [Fact]
    public void MaxWidthFitsFourCardColumns()
    {
        Assert.True(
            TrayFlyoutGeometryValidator.MaxWidth - TrayFlyoutGeometry.ChromeSideInset - TrayFlyoutGeometry.ScrollBarAllowance
                >= 4 * TrayFlyoutGeometry.CardPitch + TrayFlyoutGeometry.CardsPanelInset);
    }

    // Fix round 1 (RemEx-4kv0g.18.6 review, MEDIUM): Avalonia's MaxHeight bounds an element's own
    // content box, not its externally-applied Margin. Wiring ToolbarMaxHeight (96 - the row's
    // 16px top margin plus two 40px content rows) straight into the toolbar ItemsControl's
    // MaxHeight left 16px of slack inside the cap, so a third row's first 16px rendered and
    // clipped mid-button instead of not rendering at all. ToolbarContentMaxHeight is the
    // content-only budget the XAML attribute actually needs: exactly two 40px rows, no margin.
    private const double ToolbarItemContentFootprint = 40; // Classes="tertiary icon-button" 32px default + Margin="4" each side

    [Fact]
    public void ToolbarContentMaxHeightIsExactlyTwoContentRows()
    {
        Assert.Equal(2 * ToolbarItemContentFootprint, TrayFlyoutGeometry.ToolbarContentMaxHeight);
    }

    [Fact]
    public void ToolbarContentMaxHeightExcludesTheRowsOwnTopMargin()
    {
        Assert.Equal(
            TrayFlyoutGeometry.ToolbarMaxHeight - TrayFlyoutGeometry.ToolbarTopMargin,
            TrayFlyoutGeometry.ToolbarContentMaxHeight);
    }
}
