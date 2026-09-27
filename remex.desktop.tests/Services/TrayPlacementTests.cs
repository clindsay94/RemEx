using Avalonia;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Tray windows land inside the corner at every display scaling (RemEx-q7ak).
/// </summary>
/// <remarks>
/// The flyout subtracted a LOGICAL window size from a PHYSICAL screen edge and scaled the result.
/// That is correct at 100% — which is what a developer's own machine is most likely to be set to —
/// and drifts further off the bottom-right corner the higher the scaling goes, so the bug is
/// invisible exactly where it would be found by accident.
/// </remarks>
public class TrayPlacementTests
{
    // A 1920x1080 logical desktop at each scaling, with a 40-logical-pixel taskbar.
    private static PixelRect WorkArea(double scaling) =>
        new(0, 0, (int)(1920 * scaling), (int)((1080 - 40) * scaling));

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void TheWindowLandsFullyInsideTheWorkAreaAtEveryScaling(double scaling)
    {
        var area = WorkArea(scaling);

        var at = TrayPlacement.BottomRight(area, widthLogical: 320, heightLogical: 220, scaling, marginLogical: 12);

        // BOTH EDGES, because the old form failed on both and a check of one alone would have
        // passed a fix that scaled the width and forgot the height.
        (at.X + (int)(320 * scaling)).Should().BeLessThanOrEqualTo(area.Right,
            "the right edge of the window must not run past the work area");
        (at.Y + (int)(220 * scaling)).Should().BeLessThanOrEqualTo(area.Bottom,
            "the bottom edge of the window must not run past the work area");
        at.X.Should().BeGreaterThan(0);
        at.Y.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TheMarginIsTheSameAPPARENTSizeAtEveryScaling()
    {
        // The margin is given in logical units, so it must grow with the display rather than
        // shrinking to nothing at 200% — a 12px gap on a 2x display would read as 6.
        var at100 = TrayPlacement.BottomRight(WorkArea(1.0), 320, 220, 1.0, 12);
        var at200 = TrayPlacement.BottomRight(WorkArea(2.0), 320, 220, 2.0, 12);

        var gap100 = WorkArea(1.0).Right - (at100.X + (int)(320 * 1.0));
        var gap200 = WorkArea(2.0).Right - (at200.X + (int)(320 * 2.0));

        gap200.Should().BeInRange((gap100 * 2) - 2, (gap100 * 2) + 2,
            "a logical margin doubles in physical pixels when the display does");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void CenterPutsTheWindowInTheMiddleOfTheWorkAreaAtEveryScaling(double scaling)
    {
        // The routine countdown (RemEx-pp0rt.16): centred, in the screen's own physical pixels.
        var area = WorkArea(scaling);

        var at = TrayPlacement.Center(area, widthLogical: 400, heightLogical: 260, scaling);

        var width = (int)Math.Ceiling(400 * scaling);
        var height = (int)Math.Ceiling(260 * scaling);
        var leftGap = at.X - area.X;
        var rightGap = area.Right - (at.X + width);
        var topGap = at.Y - area.Y;
        var bottomGap = area.Bottom - (at.Y + height);
        Math.Abs(leftGap - rightGap).Should().BeLessThanOrEqualTo(1);
        Math.Abs(topGap - bottomGap).Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void CenterIsRelativeToTheWorkAreaNotTheDesktopOrigin()
    {
        // A primary screen that is not at (0,0) of the virtual desktop, with a taskbar on top.
        var area = new PixelRect(2560, 48, 2560, 1392);

        var at = TrayPlacement.Center(area, 400, 200, 1.0);

        at.Should().Be(new PixelPoint(2560 + 1080, 48 + 596));
    }

    [Fact]
    public void AWindowLargerThanTheWorkAreaIsPinnedToItsTopLeft()
    {
        var area = new PixelRect(100, 50, 800, 600);

        var at = TrayPlacement.Center(area, 1000, 700, 1.0);

        at.Should().Be(new PixelPoint(100, 50), "the title bar and Cancel must stay on screen");
    }

    [Fact]
    public void ScalingTheRESULTIsWhatWasWrong()
    {
        // The old arithmetic, spelled out: subtract the logical size from the physical edge, then
        // scale. At 100% it agrees with the fix; at 200% it lands nearly a window-width past the
        // corner, which is the drift users saw.
        const double scaling = 2.0;
        var area = WorkArea(scaling);

        var correct = TrayPlacement.BottomRight(area, 320, 220, scaling, 12).X;
        var old = (int)((area.Right - 320 - 12) * scaling);

        old.Should().BeGreaterThan(area.Right - (int)(320 * scaling),
            "the old form put the window's left edge so far right that it ran off the screen");
        correct.Should().BeLessThan(old);
    }
}
