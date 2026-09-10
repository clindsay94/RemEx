using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using FluentAssertions;
using Material.Styles.Controls;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// The assertions RemEx-0e9eq exists for: a rendered <see cref="Views.ShellView"/> frame is not one
/// flat colour, and the named elements the tree says are visible actually contribute pixels. This is
/// the check that would have caught RemEx-b8dxy - an opaque SideSheet silently covering the whole
/// shell in one flat colour while a text-only test asserted the bug (it required that no
/// <c>Background=</c> attribute appear on the element at all).
/// </summary>
public sealed class ShellFrameTests
{
    /// <summary>
    /// Below this, a region reads as "essentially one colour" for <see cref="TheShellIsNotOneFlatColour"/>
    /// and the positive control below it - low enough that ordinary anti-aliasing noise on a few
    /// glyphs and icon edges cannot cross it by accident, high enough that a single opaque panel
    /// covering the shell (RemEx-b8dxy's shape) cannot either.
    /// </summary>
    private const int MinimumDistinctColours = 8;

    /// <summary>Above this, one colour is judged to dominate the frame.</summary>
    private const double MaximumDominantCoverage = 0.95;

    [AvaloniaFact]
    public async Task TheShellIsNotOneFlatColour()
    {
        using var fixture = await ShellRenderFixture.CreateAsync();
        var frame = FramePixels.From(fixture.CaptureFrame());
        var whole = new PixelRect(0, 0, frame.Size.Width, frame.Size.Height);

        IsEffectivelyFlat(frame, whole).Should().BeFalse(
            "the rendered shell should carry the drawer, app bar, FAB and background - not present "
            + "as one solid colour the way RemEx-b8dxy's opaque SideSheet did");
    }

    public static IEnumerable<object[]> NamedShellElements()
    {
        yield return new object[] { "AppBar" };
        yield return new object[] { "ShellDrawer" };
        yield return new object[] { "DrawerHeaderMachineName" };
        yield return new object[] { "GearFabWrap" };
    }

    [AvaloniaTheory]
    [MemberData(nameof(NamedShellElements))]
    public async Task NamedShellElementsContributePixels(string elementName)
    {
        using var fixture = await ShellRenderFixture.CreateAsync();
        var control = fixture.View.FindControl<Control>(elementName);
        control.Should().NotBeNull($"{elementName} is declared by name in ShellView.axaml");

        var frame = FramePixels.From(fixture.CaptureFrame());

        control!.IsEffectivelyVisible.Should().BeTrue(
            $"{elementName} is expected visible with the fixture's IsDrawerOpen=true, chrome-shown state");

        var rect = FramePixels.RectOf(control, fixture.Window);
        rect.Width.Should().BeGreaterThan(0, $"{elementName} should occupy real screen space");
        rect.Height.Should().BeGreaterThan(0, $"{elementName} should occupy real screen space");

        frame.DistinctQuantisedColours(rect).Should().BeGreaterThanOrEqualTo(2,
            $"{elementName}'s own footprint should show more than a single flat colour - text, an "
            + "icon, a button fill against its shadow, or similar - proving it actually painted "
            + "something rather than merely occupying layout space");
    }

    /// <summary>
    /// The positive control: reproduces RemEx-b8dxy directly by giving the SideSheet an opaque
    /// background, and asserts that <see cref="TheShellIsNotOneFlatColour"/>'s own predicate now
    /// fails. Without this, a harness whose flat-colour check never actually fires for ANY input
    /// would pass silently forever - this is the test that proves the check bites.
    /// </summary>
    [AvaloniaFact]
    public async Task AnOpaqueSideSheetIsDetectedAsFlat()
    {
        using var fixture = await ShellRenderFixture.CreateAsync();
        var sideSheet = fixture.View.FindControl<SideSheet>("SettingsSideSheet");
        sideSheet.Should().NotBeNull();
        sideSheet!.Background = new SolidColorBrush(Color.Parse("#303030"));

        var frame = FramePixels.From(fixture.CaptureFrame());
        var whole = new PixelRect(0, 0, frame.Size.Width, frame.Size.Height);

        IsEffectivelyFlat(frame, whole).Should().BeTrue(
            "an opaque #303030 SideSheet spanning the whole shell (RemEx-b8dxy's exact shape) should "
            + "read as flat - if it does not, the not-flat check above is not actually discriminating");
    }

    private static bool IsEffectivelyFlat(FramePixels frame, PixelRect rect)
        => frame.DistinctQuantisedColours(rect) < MinimumDistinctColours
           || frame.DominantCoverage(rect) > MaximumDominantCoverage;
}
