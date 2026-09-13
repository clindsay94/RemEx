using Avalonia;
using Remex.Desktop.Converters;
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

    private static TrayFlyoutGeometry Geometry(double x, double y, double w = 460, double h = 380) =>
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

    // Named independently of TrayFlyoutGeometry.CardsMaxHeight's own derivation comment, so the
    // test below is a real check against the window's fixed-height chrome rather than a tautology
    // repeating the production constant's literals back at itself (RemEx-4kv0g.18.2). Header is a
    // conservative (generous) estimate — see CardsMaxHeight's XML doc for the full derivation;
    // tiles is exact, since it comes from fixed Button/margin values in TrayFlyoutWindow.axaml.
    private const double HeaderHeight = 56;
    private const double TilesMaxHeight = 164;

    // Fix round 1: the budget test omitted the 68px of window/Grid/cards-row margins the
    // derivation comment counts (24 outer Border + 32 content Grid + 12 cards row's own top
    // margin). Named so the full budget — not just header+tiles — has to fit under MaxHeight.
    private const double FixedMargins = 68;

    [Fact]
    public void CardsMaxHeightLeavesRoomForHeaderAndTilesInsideMaxHeight()
    {
        Assert.True(
            TrayFlyoutGeometry.CardsMaxHeight + HeaderHeight + TilesMaxHeight + FixedMargins
                <= TrayFlyoutGeometryValidator.MaxHeight);
    }

    // Fix round 1 (Opus review, MEDIUM): CardsMaxHeight's derivation assumes the transient window
    // is TrayFlyoutGeometry.DefaultWidth wide, giving TilesMaxHeight's "2 rows of 6 tiles".
    // TrayFlyoutWindow.ApplyMode(isPinned: false) now enforces that width on every transition to
    // transient mode; this pins the OTHER half of that guarantee — that DefaultWidth itself still
    // resolves to 2 tile rows — so a change to either constant that broke the pairing would fail
    // here instead of silently clipping the last tile row live.
    //
    // Fix round 2 (controller's eyes pass): DefaultWidth moved 420 -> 528 to fit two card columns
    // (see DefaultWidthFitsTwoCardColumns below), which crosses TrayTileColumnsConverter's 520
    // FourColumnWidth threshold — 4 columns now, not 3. 6 tiles still make 2 rows either way
    // (ceil(6/4) == ceil(6/3) == 2), so CardsMaxHeight's tile budget is unaffected; renamed to say
    // what is actually true now.
    [Fact]
    public void DefaultWidthYieldsFourTileColumns()
    {
        Assert.Equal(4, TrayTileColumnsConverter.ColumnsFor(TrayFlyoutGeometry.DefaultWidth));
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
}
