using System.Linq;
using FluentAssertions;
using Xunit;
using static Remex.Desktop.Tests.Views.Personalize.PersonalizeTabTestSupport;

namespace Remex.Desktop.Tests.Views.Personalize;

/// <summary>
/// The Layout tab (spec 2026-09-13-personalize-tabs §2, RemEx-4kv0g.4.3): Snap to grid, Grid size
/// and Pinned sensors (RemEx-4kv0g.4.2's temporary section, now permanent), then UI scale lifted out
/// of the retired Advanced tuning disclosure, then the Flyout and Behaviour blocks unchanged as
/// sub-headed cards. Source-text, like <see cref="PersonalizationFlyoutSectionTests"/> and its
/// siblings — <c>remex.desktop.tests</c> has no headless render.
/// </summary>
public class PersonalizeLayoutTabTests
{
    private const string FileName = "PersonalizeLayoutTab.axaml";

    [Fact]
    public void TheControlsAppearInSpecOrder()
    {
        var markup = Markup();
        var keys = new[]
        {
            "Settings_SnapToGrid", "Settings_GridSize", "Settings_PinnedSensors", "Custom_UiScale",
            "Custom_SectionFlyout", "Custom_FlyoutOpacity", "Custom_FlyoutCards", "Custom_FlyoutButtons",
            "Custom_FlyoutApps", "Custom_SectionBehaviour", "Custom_SplashScreen", "Custom_ReducedMotionTitle",
        };
        var positions = keys.Select(k => markup.IndexOf(k, System.StringComparison.Ordinal)).ToArray();

        positions.Should().OnlyContain(p => p >= 0, "every control this tab carries must be on it");
        positions.Should().BeInAscendingOrder(
            "spec §2: the Layout rows, then UI scale, then the Flyout sub-heading + block, then the Behaviour sub-heading + block");
    }

    [Fact]
    public void TheLayoutSectionsOwnHeaderIsGoneButTheSubHeadingsSurvive()
    {
        var markup = Markup();
        markup.Should().NotContain("Settings_Layout",
            "the tab strip is the header now - the temporary LAYOUT section's own header is retired");
        markup.Should().Contain("Localize Custom_SectionFlyout}").And.Contain("Localize Custom_SectionBehaviour}",
            "Flyout and Behaviour are NOT retired - they are reused as in-tab sub-headings (spec §5)");
    }

    [Fact]
    public void TheNullLayoutGuardSurvives()
    {
        Markup().Should().Contain("Converter={x:Static ObjectConverters.IsNotNull}",
            "a null Layout (some tests construct CustomizationViewModel without one) must render nothing");
    }

    [Fact]
    public void NoInlineFontSizeBeyondWhatThisTabInherited() => AssertInlineFontSizeCountIs(Markup(), 4);

    [Fact]
    public void NoLiteralColour() => AssertNoLiteralColour(Markup());

    [Fact]
    public void EveryVisibleStringRoutesThroughLocalizeOrABinding() => AssertEveryVisibleStringRoutesThroughLocalizeOrABinding(Markup());

    [Fact]
    public void RootsInAScrollViewer() => AssertRootsInScrollViewer(Markup());

    private static string Markup() => TabMarkup(FileName);
}
