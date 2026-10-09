using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;
using static Remex.Desktop.Tests.Views.Personalize.PersonalizeTabTestSupport;

namespace Remex.Desktop.Tests.Views.Personalize;

/// <summary>
/// The five Personalize tabs (spec 2026-09-13-personalize-tabs §2, RemEx-4kv0g.4.3): Colour, Layout,
/// Palettes, Surfaces and Text. Source-text, like <see cref="PersonalizationFlyoutSectionTests"/> and its
/// siblings — <c>remex.desktop.tests</c> has no headless render. The checks every tab shares run as
/// theories over <see cref="Tabs"/>; the Fact methods below carry what is specific to one tab.
/// </summary>
public class PersonalizeTabsTests
{
    private const string Colour = "PersonalizeColourTab.axaml";
    private const string Layout = "PersonalizeLayoutTab.axaml";
    private const string Palettes = "PersonalizePalettesTab.axaml";
    private const string Surfaces = "PersonalizeSurfacesTab.axaml";
    private const string Text = "PersonalizeTextTab.axaml";

    private static readonly Dictionary<string, (string[] Keys, string Order, int InlineFontSizes)> Spec = new()
    {
        [Colour] = (
            new[]
            {
                "Custom_BaseMode", "Custom_ColorSource", "Custom_SeedWheel", "Custom_RecentSeeds",
                "Custom_Vibrancy", "Custom_ContrastLevel", "SchemeVariantStrips", "Custom_Preview",
            },
            "spec §2: Mode, then Source, seed wheel, recent seeds, Vibrancy, Contrast, the variant chips, then Preview",
            9),
        [Layout] = (
            new[]
            {
                "Settings_SnapToGrid", "Settings_GridSize", "Settings_PinnedSensors", "Custom_UiScale",
                "Custom_SectionFlyout", "Custom_FlyoutOpacity", "Custom_FlyoutCards", "Custom_FlyoutButtons",
                "Custom_FlyoutApps", "Custom_SectionBehaviour", "Custom_SplashScreen", "Custom_ReducedMotionTitle",
            },
            "spec §2: the Layout rows, then UI scale, then the Flyout sub-heading + block, then the Behaviour sub-heading + block",
            4),
        [Palettes] = (
            new[]
            {
                "Custom_BuiltInPresets", "Custom_UserPalettes", "Custom_SavePalette",
                "Custom_CopyPaletteAxaml", "Custom_ExportPaletteJson", "Custom_ImportPaletteJson",
            },
            "spec §2: built-in presets, then user palettes, the name field + Save, then Copy/Export/Import",
            0),
        [Surfaces] = (
            new[]
            {
                "Custom_BackgroundMode", "Custom_WallpaperSource", "Custom_AppWindowOpacity",
                "Custom_GlassOpacity", "Custom_CornerRadius", "Custom_GlowStrength",
            },
            "spec §2 table: background/wallpaper/window-opacity (Look), then Card opacity, Corner radius, Glow strength (fix round 1)",
            6),
        [Text] = (
            new[]
            {
                "Custom_TextHeaders", "Custom_TextSubtitles", "Custom_TextBody", "Custom_TextSmall", "Custom_TextSensor",
                "Custom_TextSensorBackdrop", "Custom_TextShadow", "Custom_TextShadowStrength",
                "Custom_TextReset", "Custom_SectionFonts", "Custom_PageTitleFont", "Custom_SubtitleFont", "Custom_ContentFont",
            },
            "spec §2 (RemEx-n6csl): Headers, then Subtitles, then the rest of the TEXT block through " +
            "Reset, then the FONTS sub-heading, page-title font, subtitle font, content font",
            2),
    };

    public static IEnumerable<object[]> Tabs() => Spec.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Tabs))]
    public void TheControlsAppearInSpecOrder(string file)
    {
        var (keys, order, _) = Spec[file];
        var markup = TabMarkup(file);
        var positions = keys.Select(k => markup.IndexOf(k, System.StringComparison.Ordinal)).ToArray();

        positions.Should().OnlyContain(p => p >= 0, "every control this tab carries must be on it");
        positions.Should().BeInAscendingOrder(order);
    }

    [Theory]
    [MemberData(nameof(Tabs))]
    public void NoInlineFontSizeBeyondWhatThisTabInherited(string file) =>
        AssertInlineFontSizeCountIs(TabMarkup(file), Spec[file].InlineFontSizes);

    [Theory]
    [MemberData(nameof(Tabs))]
    public void NoLiteralColour(string file) => AssertNoLiteralColour(TabMarkup(file));

    [Theory]
    [MemberData(nameof(Tabs))]
    public void EveryVisibleStringRoutesThroughLocalizeOrABinding(string file) =>
        AssertEveryVisibleStringRoutesThroughLocalizeOrABinding(TabMarkup(file));

    [Theory]
    [MemberData(nameof(Tabs))]
    public void RootsInAScrollViewer(string file) => AssertRootsInScrollViewer(TabMarkup(file));

    [Fact]
    public void Colour_TheRetiredSectionHeadersAreGone()
    {
        TabMarkup(Colour).Should().NotContain("Custom_SectionMode").And.NotContain("Custom_SectionColor",
            "the tab strip is the header now - both section headers this tab used to carry are retired");
    }

    [Fact]
    public void Layout_TheLayoutSectionsOwnHeaderIsGoneButTheSubHeadingsSurvive()
    {
        var markup = TabMarkup(Layout);
        markup.Should().NotContain("Settings_Layout",
            "the tab strip is the header now - the temporary LAYOUT section's own header is retired");
        markup.Should().Contain("Localize Custom_SectionFlyout}").And.Contain("Localize Custom_SectionBehaviour}",
            "Flyout and Behaviour are NOT retired - they are reused as in-tab sub-headings (spec §5)");
    }

    [Fact]
    public void Layout_TheNullLayoutGuardSurvives()
    {
        TabMarkup(Layout).Should().Contain("Converter={x:Static ObjectConverters.IsNotNull}",
            "a null Layout (some tests construct CustomizationViewModel without one) must render nothing");
    }

    [Fact]
    public void Layout_SnapToGridDescription_Wraps()
    {
        // Fix round 2 (RemEx-4kv0g.4.4): on the 440px sheet the description clipped behind the
        // ToggleSwitch; the enclosing Grid already gives the text column the star, so wrapping was
        // the missing piece.
        var desc = Regex.Match(TabMarkup(Layout),
            @"<TextBlock[^>]*Text=""\{local:Localize Settings_SnapToGridDesc\}""[^>]*/>");

        desc.Success.Should().BeTrue();
        desc.Value.Should().Contain(@"TextWrapping=""Wrap""");
    }

    [Fact]
    public void Palettes_TheResetButtonAndSectionHeaderAreGone()
    {
        // RemEx-4kv0g.4.3 fix round 1: the global Reset link moved off this tab onto the host's own
        // Grid footer (PersonalizationPanelView.axaml), reachable from every tab.
        var markup = TabMarkup(Palettes);
        markup.Should().NotContain("Custom_Reset", "Reset is a host footer now, not part of any tab");
        markup.Should().NotContain("Custom_SectionSavedPalettes",
            "the tab strip is the header now - the section header this tab used to carry is retired");
    }

    [Fact]
    public void Surfaces_TheRetiredSectionHeaderAndDisclosureAreGone()
    {
        var markup = TabMarkup(Surfaces);
        markup.Should().NotContain("Custom_SectionLook",
            "the tab strip is the header now - the section header this tab used to carry is retired");
        markup.Should().NotContain("<Expander",
            "the Advanced-tuning disclosure this geometry came out of is dissolved, not relocated");
    }

    [Fact]
    public void Text_TheFontsSubHeadingIsTheOnlyHeaderThisTabCarries()
    {
        var markup = TabMarkup(Text);
        markup.Should().NotContain("Custom_SectionText",
            "the tab strip is the header now - the TEXT block's own section header is retired");
        markup.Should().Contain("Localize Custom_SectionFonts}",
            "the FONTS sub-heading is new (spec §2/§5) and reuses today's header markup");
    }
}
