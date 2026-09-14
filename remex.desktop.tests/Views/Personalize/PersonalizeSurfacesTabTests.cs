using System.Linq;
using FluentAssertions;
using Xunit;
using static Remex.Desktop.Tests.Views.Personalize.PersonalizeTabTestSupport;

namespace Remex.Desktop.Tests.Views.Personalize;

/// <summary>
/// The Surfaces tab (spec 2026-09-13-personalize-tabs §2, RemEx-4kv0g.4.3): the Look section's
/// contents, then Corner radius, Card opacity and Glow strength lifted out of the retired Advanced
/// tuning disclosure. Source-text, like <see cref="PersonalizationFlyoutSectionTests"/> and its
/// siblings — <c>remex.desktop.tests</c> has no headless render.
/// </summary>
public class PersonalizeSurfacesTabTests
{
    private const string FileName = "PersonalizeSurfacesTab.axaml";

    [Fact]
    public void TheControlsAppearInSpecOrder()
    {
        var markup = Markup();
        var keys = new[]
        {
            "Custom_BackgroundMode", "Custom_WallpaperSource", "Custom_AppWindowOpacity",
            "Custom_CornerRadius", "Custom_GlassOpacity", "Custom_GlowStrength",
        };
        var positions = keys.Select(k => markup.IndexOf(k, System.StringComparison.Ordinal)).ToArray();

        positions.Should().OnlyContain(p => p >= 0, "every control this tab carries must be on it");
        positions.Should().BeInAscendingOrder(
            "spec §2: background/wallpaper/window-opacity (Look), then Corner radius, Card opacity, Glow strength");
    }

    [Fact]
    public void NoInlineFontSizeBeyondWhatThisTabInherited() => AssertInlineFontSizeCountIs(Markup(), 6);

    [Fact]
    public void NoLiteralColour() => AssertNoLiteralColour(Markup());

    [Fact]
    public void EveryVisibleStringRoutesThroughLocalizeOrABinding() => AssertEveryVisibleStringRoutesThroughLocalizeOrABinding(Markup());

    [Fact]
    public void RootsInAScrollViewer() => AssertRootsInScrollViewer(Markup());

    [Fact]
    public void TheRetiredSectionHeaderAndDisclosureAreGone()
    {
        var markup = Markup();
        markup.Should().NotContain("Custom_SectionLook",
            "the tab strip is the header now - the section header this tab used to carry is retired");
        markup.Should().NotContain("<Expander",
            "the Advanced-tuning disclosure this geometry came out of is dissolved, not relocated");
    }

    private static string Markup() => TabMarkup(FileName);
}
