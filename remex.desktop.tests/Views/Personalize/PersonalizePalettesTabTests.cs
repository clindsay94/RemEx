using System.Linq;
using FluentAssertions;
using Xunit;
using static Remex.Desktop.Tests.Views.Personalize.PersonalizeTabTestSupport;

namespace Remex.Desktop.Tests.Views.Personalize;

/// <summary>
/// The Palettes tab (spec 2026-09-13-personalize-tabs §2, RemEx-4kv0g.4.3): the Saved palettes
/// section's contents, moved verbatim off PersonalizationPanelView with only the retired section
/// header gone. Source-text, like <see cref="PersonalizationFlyoutSectionTests"/> and its siblings —
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
            "Custom_CopyPaletteAxaml", "Custom_ExportPaletteJson", "Custom_ImportPaletteJson", "Custom_Reset",
        };
        var positions = keys.Select(k => markup.IndexOf(k, System.StringComparison.Ordinal)).ToArray();

        positions.Should().OnlyContain(p => p >= 0, "every control this tab carries must be on it");
        positions.Should().BeInAscendingOrder(
            "spec §2: built-in presets, then user palettes, the name field + Save, then Copy/Export/Import, then the global Reset link (RemEx-4kv0g.4.3: kept in its original document position)");
    }

    [Fact]
    public void NoInlineFontSizeBeyondWhatThisTabInherited() => AssertInlineFontSizeCountIs(Markup(), 1);

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
