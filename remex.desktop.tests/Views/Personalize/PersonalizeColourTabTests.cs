using System.Linq;
using FluentAssertions;
using Xunit;
using static Remex.Desktop.Tests.Views.Personalize.PersonalizeTabTestSupport;

namespace Remex.Desktop.Tests.Views.Personalize;

/// <summary>
/// The Colour tab (spec 2026-09-13-personalize-tabs §2, RemEx-4kv0g.4.3): Mode, then the Colour
/// section's contents, moved verbatim off PersonalizationPanelView with only the two retired
/// section headers gone. Source-text, like <see cref="PersonalizationFlyoutSectionTests"/> and its
/// siblings — <c>remex.desktop.tests</c> has no headless render.
/// </summary>
public class PersonalizeColourTabTests
{
    private const string FileName = "PersonalizeColourTab.axaml";

    [Fact]
    public void TheControlsAppearInSpecOrder()
    {
        var markup = Markup();
        var keys = new[]
        {
            "Custom_BaseMode", "Custom_ColorSource", "Custom_SeedWheel", "Custom_RecentSeeds",
            "Custom_Vibrancy", "Custom_ContrastLevel", "SchemeVariantStrips", "Custom_Preview",
        };
        var positions = keys.Select(k => markup.IndexOf(k, System.StringComparison.Ordinal)).ToArray();

        positions.Should().OnlyContain(p => p >= 0, "every control this tab carries must be on it");
        positions.Should().BeInAscendingOrder(
            "spec §2: Mode, then Source, seed wheel, recent seeds, Vibrancy, Contrast, the variant chips, then Preview");
    }

    [Fact]
    public void NoInlineFontSizeBeyondWhatThisTabInherited() => AssertInlineFontSizeCountIs(Markup(), 9);

    [Fact]
    public void NoLiteralColour() => AssertNoLiteralColour(Markup());

    [Fact]
    public void EveryVisibleStringRoutesThroughLocalizeOrABinding() => AssertEveryVisibleStringRoutesThroughLocalizeOrABinding(Markup());

    [Fact]
    public void RootsInAScrollViewer() => AssertRootsInScrollViewer(Markup());

    [Fact]
    public void TheRetiredSectionHeadersAreGone()
    {
        var markup = Markup();
        markup.Should().NotContain("Custom_SectionMode").And.NotContain("Custom_SectionColor",
            "the tab strip is the header now - both section headers this tab used to carry are retired");
    }

    private static string Markup() => TabMarkup(FileName);
}
