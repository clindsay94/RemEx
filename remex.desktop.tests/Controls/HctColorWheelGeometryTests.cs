using System;
using System.IO;
using System.Runtime.CompilerServices;
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
    public void AnArrowKeyIsANudgeAndNotACommit()
    {
        // MEASURED, NOT REASONED. A verification run on the real app pressed Left five times and the
        // recently-used row came back holding #068EC6, #008EC4, #008FC1, #008FBE, #0090BC, #0090B9
        // and #0091B2 — seven swatches nobody could tell apart, filling a row of eight and pushing
        // out every colour that had actually been chosen. The keyboard's "I have finished" is leaving
        // the control.
        //
        // Source text because there is no headless render here to raise a real key event; what this
        // pins is that the commit is NOT wired to a per-keystroke handler, which is the whole defect.
        var source = ControlSource();

        source.Should().NotMatchRegex(@"OnKeyUp[\s\S]{0,400}?SeedCommitted\?\.Invoke",
            "committing on key-up makes every 2° nudge a separate entry in the recently-used row");
        // Two independent facts rather than one order-dependent window: the handler is subscribed,
        // and the handler is the thing that commits. A single regex spanning both breaks the moment
        // either moves, which makes it a test about line order rather than about behaviour.
        source.Should().Contain("LostFocus += OnLostFocusCommit;",
            "the keyboard path still has to commit SOMETHING, or a seed chosen with the arrows never "
            + "reaches the recently-used row at all");
        MethodBody(source, "private void OnLostFocusCommit").Should().Contain("SeedCommitted?.Invoke",
            "the subscribed handler has to be the one that raises the commit");
        source.Should().MatchRegex(@"OnPointerReleased[\s\S]{0,400}?SeedCommitted\?\.Invoke",
            "a drag has a real end event and must keep using it");
    }

    [Fact]
    public void LeavingTheWheelWithoutTouchingItIsNotAChoice()
    {
        // TABBING THROUGH IS NOT PICKING. With the commit on LostFocus, an unconditional raise means
        // moving keyboard focus across the drawer commits whatever seed was already loaded — and once
        // the recently-used row holds its maximum of eight, that insert EVICTS a colour the user
        // deliberately saved, from navigation alone. The nastier variant is a pointer: if focus leaves
        // because the user pressed the eighth recents swatch, the eviction destroys the Button under
        // the pointer before it can be released, so that swatch silently does nothing.
        var source = ControlSource();

        source.Should().Contain("GotFocus += OnGotFocusRemember;",
            "telling an edit from a visit needs the seed as it stood on arrival");
        source.Should().MatchRegex(@"OnGotFocusRemember[^\n]*_seedAtFocus = \(Hue, Chroma, Tone\)",
            "all three axes, or a tone-only change made while the wheel held focus reads as untouched");

        var commit = MethodBody(source, "private void OnLostFocusCommit");
        commit.Should().Contain("_seedAtFocus",
            "the commit has to consult the remembered seed rather than firing unconditionally");
        commit.Should().MatchRegex(@"if \(moved\) SeedCommitted",
            "the raise must be conditional on the seed having actually moved");
    }

    /// <summary>Everything from a member's signature to the closing brace of its own block.</summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "{0} moved or was renamed", signature);

        var brace = source.IndexOf('{', start);
        brace.Should().BeGreaterThanOrEqualTo(0);

        var depth = 0;
        for (var i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }

        throw new InvalidOperationException($"{signature}'s body is unterminated");
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

    /// <summary>The control's own source, for the one assertion that has to be made over text.</summary>
    private static string ControlSource()
    {
        var path = Path.Combine(RepoRoot(), "remex.desktop", "Controls", "HctColorWheel.cs");
        var source = File.ReadAllText(path);

        // Anti-vacuity: a moved file would make every NotMatchRegex above pass over nothing.
        source.Length.Should().BeGreaterThan(2000);
        source.Should().Contain("SeedCommitted");
        return source;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
