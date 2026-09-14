using System.Linq;
using FluentAssertions;
using Xunit;
using static Remex.Desktop.Tests.Views.Personalize.PersonalizeTabTestSupport;

namespace Remex.Desktop.Tests.Views.Personalize;

/// <summary>
/// The Palettes tab (spec 2026-09-13-personalize-tabs §2, RemEx-4kv0g.4.3): the Saved palettes
/// section's contents, moved verbatim off PersonalizationPanelView with the retired section header
/// gone (fix round 1: the Reset link that briefly landed here also moved on, to the host's own
/// footer). Source-text, like <see cref="PersonalizationFlyoutSectionTests"/> and its siblings —
/// <c>remex.desktop.tests</c> has no headless render.
/// </summary>
public class PersonalizePalettesTabTests
{
    private const string FileName = "PersonalizePalettesTab.axaml";

    [Fact]
    public void TheControlsAppearInSpecOrder()
    {
        var markup = Markup();
        var keys = new[]
        {
            "Custom_BuiltInPresets", "Custom_UserPalettes", "Custom_SavePalette",
            "Custom_CopyPaletteAxaml", "Custom_ExportPaletteJson", "Custom_ImportPaletteJson",
        };
        var positions = keys.Select(k => markup.IndexOf(k, System.StringComparison.Ordinal)).ToArray();

        positions.Should().OnlyContain(p => p >= 0, "every control this tab carries must be on it");
        positions.Should().BeInAscendingOrder(
            "spec §2: built-in presets, then user palettes, the name field + Save, then Copy/Export/Import");
    }

    [Fact]
    public void TheResetButtonIsGone()
    {
        // RemEx-4kv0g.4.3 fix round 1: the global Reset link moved off this tab onto the host's own
        // Grid footer (PersonalizationPanelView.axaml), reachable from every tab instead of
        // undiscoverable from the other four.
        Markup().Should().NotContain("Custom_Reset",
            "Reset is a host footer now, not part of any tab");
    }

    [Fact]
    public void NoInlineFontSizeBeyondWhatThisTabInherited() => AssertInlineFontSizeCountIs(Markup(), 0);

    [Fact]
    public void NoLiteralColour() => AssertNoLiteralColour(Markup());

    [Fact]
    public void EveryVisibleStringRoutesThroughLocalizeOrABinding() => AssertEveryVisibleStringRoutesThroughLocalizeOrABinding(Markup());

    [Fact]
    public void RootsInAScrollViewer() => AssertRootsInScrollViewer(Markup());

    [Fact]
    public void TheRetiredSectionHeaderIsGone()
    {
        Markup().Should().NotContain("Custom_SectionSavedPalettes",
            "the tab strip is the header now - the section header this tab used to carry is retired");
    }

    private static string Markup() => TabMarkup(FileName);
}
