using Avalonia;
using FluentAssertions;
using Remex.Desktop.Controls;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// The seed wheel's hit maths (RemEx-5u0vy). There is no headless render in this suite, so the disc
/// itself cannot be asserted — but the mapping between a pointer position and the hue/chroma it
/// selects is pure geometry, and it is the half that decides whether the thumb lands where the user
/// clicked.
/// </summary>
public class HctColorWheelGeometryTests
{
    private const double Side = 200.0;
    private const double Centre = Side / 2.0;

    [Theory]
    [InlineData(1.0, 0.0, 0.0)]    // right
    [InlineData(0.0, 1.0, 90.0)]   // up
    [InlineData(-1.0, 0.0, 180.0)] // left
    [InlineData(0.0, -1.0, 270.0)] // down
    public void HueRunsAnticlockwiseFromTheRight(double dx, double dy, double expectedHue)
    {
        // Y GROWS DOWNWARDS IN SCREEN SPACE AND HUE DOES NOT. Getting this backwards is invisible in
        // a screenshot — the disc still looks like a colour wheel — but every pick lands on the hue
        // mirrored across the horizontal, and the thumb sits on a colour the app is not painting.
        var point = new Point(Centre + (dx * 80), Centre - (dy * 80));

        var (hue, _) = HctColorWheel.PointToHueChroma(point, Side);

        hue.Should().BeApproximately(expectedHue, 0.001);
    }

    [Fact]
    public void TheCentreIsZeroChroma()
    {
        var (_, chroma) = HctColorWheel.PointToHueChroma(new Point(Centre, Centre), Side);

        chroma.Should().Be(0.0);
    }

    [Fact]
    public void TheRimIsFullChroma()
    {
        var (_, chroma) = HctColorWheel.PointToHueChroma(new Point(Side, Centre), Side);

        chroma.Should().BeApproximately(SeedHct.MaxChroma, 0.001);
    }

    [Fact]
    public void APointerDraggedOutsideTheDiscClampsToTheRimRatherThanOvershooting()
    {
        // A drag that leaves the control keeps its capture, so this runs constantly. Without the
        // clamp the chroma would keep climbing past the top of the range as the pointer travels.
        var (_, chroma) = HctColorWheel.PointToHueChroma(new Point(Side * 4, Centre), Side);

        chroma.Should().Be(SeedHct.MaxChroma);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(45.0, 30.0)]
    [InlineData(200.0, 120.0)]
    [InlineData(359.0, 75.0)]
    public void TheThumbSitsWhereAClickAtThatPointWouldLand(double hue, double chroma)
    {
        // THE TWO DIRECTIONS HAVE TO BE INVERSES OR THE THUMB LIES. Clicking sets hue and chroma from
        // a point; rendering turns hue and chroma back into a point. If they disagree the thumb
        // jumps away from the pointer on every click, which reads as the wheel ignoring the input.
        var point = HctColorWheel.HueChromaToPoint(hue, chroma, Side);

        var (roundTrippedHue, roundTrippedChroma) = HctColorWheel.PointToHueChroma(point, Side);

        roundTrippedChroma.Should().BeApproximately(chroma, 0.001);

        // Hue is meaningless at the centre, where every direction is the same point.
        if (chroma > 0) roundTrippedHue.Should().BeApproximately(hue, 0.001);
    }

    [Fact]
    public void ChromaBeyondTheCeilingStillPlacesTheThumbInsideTheDisc()
    {
        // The seed's chroma is read from a colour, not from the slider, so nothing structurally stops
        // a value above MaxChroma reaching the renderer. Drawing the thumb outside the disc would put
        // it over the panel behind the wheel.
        var point = HctColorWheel.HueChromaToPoint(90.0, SeedHct.MaxChroma * 4, Side);

        var dx = point.X - Centre;
        var dy = point.Y - Centre;
        Math.Sqrt((dx * dx) + (dy * dy)).Should().BeLessThanOrEqualTo(Centre + 0.001);
    }

    [Fact]
    public void AZeroSizedWheelIsAnsweredRatherThanDividedBy()
    {
        // Layout hands a control a zero size before its first measure, and Render is not the only
        // caller — a pointer event can arrive in that window.
        var act = () => HctColorWheel.PointToHueChroma(new Point(0, 0), 0);

        act.Should().NotThrow();
        HctColorWheel.PointToHueChroma(new Point(0, 0), 0).Should().Be((0.0, 0.0));
    }
}
