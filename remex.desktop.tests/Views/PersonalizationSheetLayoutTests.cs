using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// The Personalization sheet's shape (spec section 3): six cards in a pinned order, one path to a
/// colour, no Tone slider, no seed setter reachable from the Saved palettes card. Source-text,
/// because remex.desktop.tests has no headless render.
/// </summary>
public class PersonalizationSheetLayoutTests
{
    private static readonly string[] HeadersInOrder =
    {
        "Custom_SectionColor", "Custom_SectionMode", "Custom_SectionLook",
        "Custom_AdvancedTuning", "Custom_SectionBehaviour", "Custom_SectionSavedPalettes",
    };

    [Fact]
    public void TheSixSectionHeadersAppearInSpecOrder()
    {
        var markup = PanelMarkup();
        var positions = HeadersInOrder.Select(k => markup.IndexOf($"Localize {k}}}", System.StringComparison.Ordinal)).ToArray();

        positions.Should().OnlyContain(p => p >= 0, "every section header is on the sheet");
        positions.Should().BeInAscendingOrder("the order is the spec's: Colour, Mode, Look, Fine-tuning, Behaviour, Saved palettes");
    }

    [Fact]
    public void TheSavedPalettesCardBindsNoSeedSetter()
    {
        var card = SavedPalettesCard();

        card.Should().NotContain("SetAccentCommand", "a saved palette is a whole palette; the accent-swatch shortcut is gone (spec section 1)");
        card.Should().Contain("ThemePresets").And.Contain("SavedPalettes")
            .And.Contain("SaveCurrentPaletteCommand").And.Contain("ImportPaletteJsonCommand").And.Contain("ExportPaletteJsonCommand");
    }

    [Fact]
    public void TheToneSliderIsGoneButTheWheelStillCarriesTone()
    {
        var markup = PanelMarkup();

        Regex.IsMatch(markup, @"<Slider[^>]*Value=""\{Binding SeedTone").Should().BeFalse("Android has no tone slider (spec section 3)");
        markup.Should().Contain("Tone=\"{Binding SeedTone", "the wheel still needs tone to render the disc");
        markup.Should().NotContain("Custom_SeedTone");
    }

    [Fact]
    public void TheRetiredStrategyNamesAreNotOnTheSheet()
    {
        PanelMarkup().Should().NotContain("Custom_Scheme_Content").And.NotContain("Custom_Scheme_Spritz");
    }

    [Fact]
    public void TheColourCardOffersSourceThenVibrancyContrastStrategyThenPreview()
    {
        var card = ColourCard();
        var order = new[] { "AvailableColorSources", "Custom_Vibrancy", "Custom_ContrastLevel", "SchemeVariantStrips", "Custom_Preview" }
            .Select(k => card.IndexOf(k, System.StringComparison.Ordinal)).ToArray();

        order.Should().OnlyContain(p => p >= 0);
        order.Should().BeInAscendingOrder("source, then shape, then strategy, then preview — Android's flow");
        card.Should().MatchRegex(@"SelectedItem=""\{Binding ColorSource[,}]", "the source picker writes ColorSource");
        card.Should().MatchRegex(@"Value=""\{Binding SeedChroma[,}]", "Vibrancy keeps its SeedChroma backing field");
    }

    [Fact]
    public void EachSourceShowsOnlyItsOwnControls()
    {
        var card = ColourCard();

        card.Should().MatchRegex(@"IsVisible=""\{Binding IsWindowsAccentSource\}""[^>]*>[\s\S]*?SourceAccentHex", "the Windows-accent swatch shows the current accent");
        card.Should().MatchRegex(@"IsVisible=""\{Binding IsWallpaperSource\}""[\s\S]*?RefreshWallpaperSeedsCommand[\s\S]*?WallpaperSeedCandidates");
        var custom = Regex.Match(card, @"IsVisible=""\{Binding IsCustomSource\}""[\s\S]*?</StackPanel>\s*</StackPanel>");
        custom.Success.Should().BeTrue();
        custom.Value.Should().Contain("<controls:HctColorWheel").And.Contain("CustomAccentHex").And.Contain("CustomAccentColors",
            "the wheel, the hex box and the recents row live under Custom");
    }

    [Fact]
    public void TheSampleCardTakesTheLivePaletteCornersAndWindowOpacity()
    {
        var card = ColourCard();
        var sample = Regex.Match(card, @"<Border[^>]*Name=""SampleCard""[^>]*>");

        sample.Success.Should().BeTrue();
        sample.Value.Should().Contain("Background=\"{Binding TonalRamp.SurfaceBrush}\"")
            .And.Contain("CornerRadius=\"{Binding SampleCardCornerRadius}\"")
            .And.Contain("Opacity=\"{Binding AppWindowOpacity}\"");
    }

    [Fact]
    public void TheLookCardHidesWallpaperControlsUnlessWallpaperIsSelected()
    {
        var card = CardAfterHeader("Custom_SectionLook");

        card.Should().MatchRegex(@"SelectedItem=""\{Binding CanvasBackgroundType[,}]");
        card.Should().MatchRegex(@"IsVisible=""\{Binding IsWallpaperBackgroundSelected\}""[\s\S]*?WallpaperSource[\s\S]*?WallpaperBlur");
        card.Should().MatchRegex(@"IsVisible=""\{Binding IsWindowOpacityRelevant\}""[\s\S]*?AppWindowOpacity");
    }

    [Fact]
    public void FineTuningIsAnExpanderHoldingGeometryAndTypography()
    {
        var card = CardAfterHeader("Custom_AdvancedTuning");

        card.Should().StartWith("<Expander");
        foreach (var binding in new[] { "CornerRadius", "GlassOpacity", "GlowStrength", "SelectedPageTitleFont", "SelectedBodyFont", "UiScale" })
            card.Should().Contain($"{{Binding {binding}", $"{binding} moved into Fine-tuning");
    }

    [Fact]
    public void BehaviourHasSplashWithPreviewHardwareSyncAndReducedMotion()
    {
        var card = CardAfterHeader("Custom_SectionBehaviour");

        card.Should().MatchRegex(@"SelectedItem=""\{Binding SplashStyle[,}]");
        card.Should().Contain("PreviewSplashCommand");
        card.Should().MatchRegex(@"IsChecked=""\{Binding SyncWithHardware[,}]");
        card.Should().MatchRegex(@"IsChecked=""\{Binding IsReducedMotion[,}]");
    }

    [Fact]
    public void ResetSitsBelowTheLastCard()
    {
        var markup = PanelMarkup();
        markup.LastIndexOf("ResetToDefaultCommand", System.StringComparison.Ordinal)
            .Should().BeGreaterThan(markup.LastIndexOf("</material:Card>", System.StringComparison.Ordinal));
    }

    private static string ColourCard() => CardAfterHeader("Custom_SectionColor");

    private static string SavedPalettesCard() => CardAfterHeader("Custom_SectionSavedPalettes");

    /// <summary>The markup from a card's header key to that card's closing tag (or the Expander's).</summary>
    private static string CardAfterHeader(string headerKey)
    {
        var markup = PanelMarkup();
        var start = markup.IndexOf($"Localize {headerKey}}}", System.StringComparison.Ordinal);
        start.Should().BeGreaterOrEqualTo(0, $"{headerKey} must be on the sheet");
        // Back up to the card/expander that owns the header.
        var cardStart = markup.LastIndexOf("<material:Card", start, System.StringComparison.Ordinal);
        var expanderStart = markup.LastIndexOf("<Expander", start, System.StringComparison.Ordinal);
        var isExpander = expanderStart > cardStart;
        var open = isExpander ? expanderStart : cardStart;
        var close = markup.IndexOf(isExpander ? "</Expander>" : "</material:Card>", start, System.StringComparison.Ordinal);
        return markup.Substring(open, close - open);
    }

    private static string PanelMarkup() => File.ReadAllText(
        Path.Combine(RepoRoot(), "remex.desktop", "Views", "PersonalizationPanelView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
