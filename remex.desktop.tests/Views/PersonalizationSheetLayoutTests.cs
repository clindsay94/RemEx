using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// The Personalization sheet's shape after RemEx-4kv0g.4.3 (spec 2026-09-13-personalize-tabs §1/§2):
/// the host is a <c>TabControl</c> with five tabs, one <c>UserControl</c> per tab under
/// <c>Views/Personalize/</c>. Source-text, because <c>remex.desktop.tests</c> has no headless render.
/// </summary>
public class PersonalizationSheetLayoutTests
{
    /// <summary>
    /// Every <c>Custom_*</c> key that was reachable from <c>PersonalizationPanelView.axaml</c>
    /// immediately before this task, computed ONCE via
    /// <c>git show 1b147cc6:remex.desktop/Views/PersonalizationPanelView.axaml | grep -oE 'Custom_[A-Za-z0-9_]*' | sort -u</c>
    /// and pasted here verbatim (RemEx-4kv0g.4.3 base commit, .4.2 + its review fixes).
    /// </summary>
    private static readonly string[] BeforeCustomKeys =
    {
        "Custom_AdvancedTuning", "Custom_AppWindowOpacity", "Custom_BackgroundMode", "Custom_BaseMode",
        "Custom_BaseModeDesc", "Custom_BgType_", "Custom_BorderCornerRadiusPixelsTooltip",
        "Custom_BuiltInPresets", "Custom_ChooseWallpaperImage", "Custom_Clear", "Custom_ColorSource",
        "Custom_ContentFont", "Custom_ContentFontTip", "Custom_ContrastLevel", "Custom_ContrastSharper",
        "Custom_ContrastSofter", "Custom_ContrastTooltip", "Custom_CopyPaletteAxaml", "Custom_CornerRadius",
        "Custom_CornerRadiusGeneralTooltip", "Custom_CurrentWindowsAccent", "Custom_DeletePalette",
        "Custom_ExportPaletteJson", "Custom_FlyoutApps", "Custom_FlyoutApps_None", "Custom_FlyoutButtons",
        "Custom_FlyoutCards", "Custom_FlyoutCards_NoPins", "Custom_FlyoutOpacity", "Custom_FlyoutOpacityTooltip",
        "Custom_Frosted", "Custom_GlassFrostingOpacityPercentTooltip", "Custom_GlassOpacity",
        "Custom_GlassOpacityTooltip", "Custom_GlowIntense", "Custom_GlowNone", "Custom_GlowStrength",
        "Custom_ImportPaletteJson", "Custom_MatchPhoneTheme", "Custom_ModeDark", "Custom_ModeLight",
        "Custom_ModeSystem", "Custom_NeonGlowThicknessPixelsTooltip", "Custom_NeonGlowTooltip",
        "Custom_PageTitleFont", "Custom_PageTitleFontTip", "Custom_PaletteNamePlaceholder", "Custom_Preview",
        "Custom_RecentSeeds", "Custom_ReducedMotionDesc", "Custom_ReducedMotionTitle",
        "Custom_RefreshWallpaperSeeds", "Custom_Reset", "Custom_Round", "Custom_SampleCardBody",
        "Custom_SampleCardTitle", "Custom_SavePalette", "Custom_SectionBehaviour", "Custom_SectionColor",
        "Custom_SectionFlyout", "Custom_SectionLook", "Custom_SectionMode", "Custom_SectionSavedPalettes",
        "Custom_SectionText", "Custom_SeedHex", "Custom_SeedHue", "Custom_SeedWheel", "Custom_SeedWheelTooltip",
        "Custom_Source_", "Custom_SplashScreen", "Custom_Square", "Custom_Strategy", "Custom_TextBody",
        "Custom_TextBodyBold", "Custom_TextBodyTip", "Custom_TextHeaders", "Custom_TextHeadersBold",
        "Custom_TextHeadersTip", "Custom_TextReset", "Custom_TextSensor", "Custom_TextSensorBackdrop",
        "Custom_TextSensorBackdropTip", "Custom_TextSensorBold", "Custom_TextSensorTip", "Custom_TextShadow",
        "Custom_TextShadowStrength", "Custom_TextShadowTip", "Custom_TextSizeLabelTip", "Custom_TextSmall",
        "Custom_TextSmallBold", "Custom_TextSmallTip", "Custom_TonalRamp_ErrorPair", "Custom_TonalRamp_Neutral",
        "Custom_TonalRamp_Primary", "Custom_TonalRamp_PrimaryPair", "Custom_TonalRamp_Secondary",
        "Custom_TonalRamp_SurfacePair", "Custom_TonalRamp_Tertiary", "Custom_UiScale", "Custom_UiScaleTip",
        "Custom_UserPalettes", "Custom_Vibrancy", "Custom_WallpaperBlur", "Custom_WallpaperSource",
        "Custom_WallpaperSource_", "Custom_WelcomeSplashTooltip",
    };

    private static readonly string[] RetiredKeys =
    {
        "Custom_SectionColor", "Custom_SectionMode", "Custom_SectionLook", "Custom_SectionText",
        "Custom_SectionSavedPalettes", "Custom_AdvancedTuning",
    };

    /// <summary>
    /// Keys legitimately reused across two tabs — generic vocabulary, not a copy-paste duplicate.
    /// Pre-existing in the single-file sheet, unrelated to this task's move: "Preview" labels both
    /// the Colour tab's ramp preview AND the Layout tab's splash-screen Preview button;
    /// "Clear"/"Frosted" are the end-of-slider labels shared by GlassOpacity (Surfaces) and
    /// FlyoutOpacity (Layout) — both are 0..1 opacity sliders with the same two-word range caption.
    /// </summary>
    private static readonly string[] LegitimatelySharedKeys = { "Custom_Preview", "Custom_Clear", "Custom_Frosted" };

    private static readonly (string File, string Header)[] Tabs =
    {
        ("PersonalizeColourTab.axaml", "Custom_Tab_Colour"),
        ("PersonalizePalettesTab.axaml", "Custom_Tab_Palettes"),
        ("PersonalizeSurfacesTab.axaml", "Custom_Tab_Surfaces"),
        ("PersonalizeTextTab.axaml", "Custom_Tab_Text"),
        ("PersonalizeLayoutTab.axaml", "Custom_Tab_Layout"),
    };

    [Fact]
    public void TheHostIsATabControlWithFiveHeadersInSpecOrder_EachContainingItsMatchingTabControl()
    {
        var host = HostMarkup();

        host.Should().Contain("<TabControl", "the host is a TabControl (spec §1)");
        host.Should().Contain("SelectedIndex=\"{Binding SelectedTabIndex}\"");

        var headerPositions = Tabs.Select(t => host.IndexOf($"Localize {t.Header}}}", System.StringComparison.Ordinal)).ToArray();
        headerPositions.Should().OnlyContain(p => p >= 0, "every tab header must be on the host");
        headerPositions.Should().BeInAscendingOrder("spec §2: Colour, Palettes, Surfaces, Text, Layout");

        // Each TabItem's content is its matching personalize: control - not just present anywhere,
        // but the item immediately following that header's own TabItem open tag.
        foreach (var (file, header) in Tabs)
        {
            var controlName = Path.GetFileNameWithoutExtension(file);
            var headerPos = host.IndexOf($"Localize {header}}}", System.StringComparison.Ordinal);
            var tabItemOpen = host.LastIndexOf("<TabItem", headerPos, System.StringComparison.Ordinal);
            var tabItemClose = host.IndexOf("</TabItem>", headerPos, System.StringComparison.Ordinal);
            var body = host.Substring(tabItemOpen, tabItemClose - tabItemOpen);

            body.Should().Contain($"<personalize:{controlName}",
                $"the {header} TabItem must host {controlName}");
        }
    }

    [Fact]
    public void TheHostFooter_HoldsTheResetButtonAfterTheTabControl()
    {
        // RemEx-4kv0g.4.3 fix round 1: Reset moved off the Palettes tab onto the host itself, as a
        // Grid RowDefinitions="*,Auto" footer under the TabControl - reachable from every tab instead
        // of undiscoverable from the other four.
        var host = HostMarkup();

        host.Should().Contain("<Grid RowDefinitions=\"*,Auto\"", "the host's root has to be the Grid that gives the TabControl the star row and the Reset footer the Auto row");

        var tabControlClose = host.IndexOf("</TabControl>", System.StringComparison.Ordinal);
        var resetButton = host.IndexOf("Command=\"{Binding ResetToDefaultCommand}\"", System.StringComparison.Ordinal);

        tabControlClose.Should().BeGreaterThan(-1, "the host must still contain the TabControl");
        resetButton.Should().BeGreaterThan(tabControlClose, "the Reset button has to come after the TabControl, in the Grid's Auto row");
    }

    [Fact]
    public void TheHostHasNoScrollViewerAroundThePersonalizeView()
    {
        // The strip must stay pinned while each tab scrolls its own content (spec §1) - a
        // ScrollViewer wrapping the whole TabControl would scroll the strip away with it, and (spec
        // §5/REGRESSION-GUARDS) would swallow the MaxHeight bound the same way the old ShellView
        // ScrollViewer did one level up.
        HostMarkup().Should().NotContain("<ScrollViewer", "no ScrollViewer belongs around the Personalize view any more - each tab owns its own");
    }

    [Fact]
    public void TheTabHeadersAreLeftAlignedInTheirTabs_WithoutTouchingTheirMinWidth()
    {
        // RemEx-vkkcq fix round 1 (Connor): the header text sits at the left of its tab, not centred.
        // Material centres it twice - HorizontalContentAlignment=Center on the TabItem and
        // HorizontalAlignment=Center on the template's PART_ContentPresenter - so both have to be
        // overridden or the presenter keeps floating in the middle of its 90px slot. The MinWidth
        // and the App.axaml 16px inset are deliberately not this view's business.
        var host = HostMarkup();

        var tabItemStyle = Regex.Match(host, @"<Style Selector=""TabItem"">[\s\S]*?</Style>");
        tabItemStyle.Success.Should().BeTrue("the host styles its own TabItems");
        tabItemStyle.Value.Should().MatchRegex(
            @"<Setter Property=""HorizontalContentAlignment"" Value=""Left""\s*/>",
            "the header content has to be left-aligned inside the presenter");

        var presenterStyle = Regex.Match(host,
            @"<Style Selector=""TabItem /template/ ContentPresenter#PART_ContentPresenter"">[\s\S]*?</Style>");
        presenterStyle.Success.Should().BeTrue("the template presenter's own centring has to be overridden too");
        presenterStyle.Value.Should().MatchRegex(
            @"<Setter Property=""HorizontalAlignment"" Value=""Left""\s*/>",
            "the presenter itself has to sit at the left of the tab, not centred in it");

        host.Should().NotContain("Property=\"MinWidth\"", "the header slot width is Material's, not this view's");
        host.Should().NotContain("MinWidth=\"", "the header slot width is Material's, not this view's");
        host.Should().NotContain("Property=\"Padding\"", "the 16px inset lives in App.axaml, not here");
        Regex.IsMatch(host, @"Property=""Template""").Should().BeFalse("alignment is a setter, never a template override");
    }

    [Theory]
    [InlineData("PersonalizeColourTab.axaml")]
    [InlineData("PersonalizePalettesTab.axaml")]
    [InlineData("PersonalizeSurfacesTab.axaml")]
    [InlineData("PersonalizeTextTab.axaml")]
    [InlineData("PersonalizeLayoutTab.axaml")]
    public void EveryTabFileRootsInItsOwnScrollViewer(string file)
    {
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "Personalize", file));
        Regex.IsMatch(markup, @"<UserControl\b[\s\S]*?>\s*<ScrollViewer\b[^>]*HorizontalScrollBarVisibility=""Disabled""")
            .Should().BeTrue($"{file} must root in a ScrollViewer that owns its own scrolling (spec §1/REGRESSION-GUARDS)");
    }

    [Fact]
    public void TheUnionOfTabKeys_CoversEveryPreMoveKeyExceptTheSixRetiredOnes()
    {
        // Union includes the host, not just the five tab files: RemEx-4kv0g.4.3 fix round 1 moved
        // Custom_Reset off the Palettes tab onto the host's own Grid footer, so it is reachable from
        // the sheet as a whole even though no single tab file carries it any more.
        var expected = BeforeCustomKeys.Except(RetiredKeys).ToArray();
        var union = AllTabCustomKeys().Concat(TabCustomKeysIn(HostMarkup())).Distinct().ToArray();

        var missing = expected.Except(union).ToArray();
        missing.Should().BeEmpty(
            "every Custom_* key the sheet carried before this task, other than the six retired section/disclosure headers, must still be reachable from one of the five tabs or the host");
    }

    [Fact]
    public void NoKeyAppearsInTwoTabFilesExceptTheDocumentedSharedVocabulary()
    {
        var perFile = Tabs.Select(t => (t.File, Keys: TabCustomKeys(t.File))).ToArray();
        var offenders = new List<string>();
        var sharedSeen = new HashSet<string>();

        for (var i = 0; i < perFile.Length; i++)
        for (var j = i + 1; j < perFile.Length; j++)
        {
            foreach (var key in perFile[i].Keys.Intersect(perFile[j].Keys))
            {
                if (LegitimatelySharedKeys.Contains(key))
                {
                    sharedSeen.Add(key);
                    continue;
                }

                offenders.Add($"{key} in both {perFile[i].File} and {perFile[j].File}");
            }
        }

        offenders.Should().BeEmpty("a key duplicated across tabs (beyond the documented shared vocabulary) means content was copied instead of moved");

        // Anti-vacuity: every documented exception has to actually be shared by two tabs today.
        LegitimatelySharedKeys.Except(sharedSeen).Should().BeEmpty(
            "a LegitimatelySharedKeys entry that no longer collides is stale and should be removed");
    }

    [Fact]
    public void TheRetiredKeysAppearInNoAxamlFile()
    {
        var offenders = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "remex.desktop"), "*.axaml", SearchOption.AllDirectories)
            .Where(f => RetiredKeys.Any(k => Regex.IsMatch(File.ReadAllText(f), $@"\b{Regex.Escape(k)}\b")))
            .Select(Path.GetFileName)
            .ToArray();

        offenders.Should().BeEmpty("the six retired section/disclosure headers must appear in no .axaml - the tab strip is the header now");
    }

    private static string[] AllTabCustomKeys() => Tabs.SelectMany(t => TabCustomKeys(t.File)).Distinct().ToArray();

    private static string[] TabCustomKeys(string file) =>
        TabCustomKeysIn(File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "Personalize", file)));

    private static string[] TabCustomKeysIn(string markup) => Regex.Matches(markup, @"Custom_[A-Za-z0-9_]*")
        .Select(m => m.Value)
        .Distinct()
        .ToArray();

    private static string HostMarkup() => File.ReadAllText(
        Path.Combine(RepoRoot(), "remex.desktop", "Views", "PersonalizationPanelView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
