using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Flyout D2 .2 (RemEx-4kv0g.18.6): the Flyout card sits between Look and Text, its four controls
/// bind FlyoutOpacity/FlyoutCards/FlyoutTiles/FlyoutApps, every visible string routes through
/// <c>local:Localize</c>, and it adds exactly one inline size (the header, matching its six
/// siblings) so <c>TypographyVocabularyTests</c>' ratchet keeps its arithmetic. Source-text, like
/// its siblings — <c>remex.desktop.tests</c> has no headless render.
/// </summary>
public class PersonalizationFlyoutSectionTests
{
    [Fact]
    public void TheFlyoutCard_SitsBetweenLookAndText()
    {
        var markup = PanelMarkup();
        var look = markup.IndexOf("Localize Custom_SectionLook}", System.StringComparison.Ordinal);
        var flyout = markup.IndexOf("Localize Custom_SectionFlyout}", System.StringComparison.Ordinal);
        var text = markup.IndexOf("Localize Custom_SectionText}", System.StringComparison.Ordinal);

        flyout.Should().BeGreaterThan(look).And.BeLessThan(text,
            "spec §4: the Flyout section sits after Look, before Text");
    }

    [Fact]
    public void TheOpacitySlider_BindsFlyoutOpacity_WithTheClearFrostedEndLabels()
    {
        var card = FlyoutCard();

        card.Should().MatchRegex(@"<Slider Minimum=""0"" Maximum=""1""[^>]*Value=""\{Binding FlyoutOpacity[,}]");
        card.Should().Contain("Localize Custom_Clear").And.Contain("Localize Custom_Frosted");
        card.Should().Contain("Localize Custom_FlyoutOpacityTooltip");
    }

    [Fact]
    public void TheThreeChecklists_BindTheirCollections()
    {
        var card = FlyoutCard();

        card.Should().Contain(@"ItemsSource=""{Binding FlyoutCards}""");
        card.Should().Contain(@"ItemsSource=""{Binding FlyoutTiles}""");
        card.Should().Contain(@"ItemsSource=""{Binding FlyoutApps}""");

        // Every item template is a CheckBox bound two-way to IsShown.
        Regex.Matches(card, @"<CheckBox IsChecked=""\{Binding IsShown\}""").Count.Should().Be(3,
            "one checklist row template each for Cards, Buttons and Apps");
    }

    [Fact]
    public void TheEmptyStateCaptions_AreLocalizedAndGatedOnCount()
    {
        var card = FlyoutCard();

        card.Should().Contain(@"Text=""{local:Localize Custom_FlyoutCards_NoPins}""");
        card.Should().Contain(@"IsVisible=""{Binding !FlyoutCards.Count}""");
        card.Should().Contain(@"Text=""{local:Localize Custom_FlyoutApps_None}""");
        card.Should().Contain(@"IsVisible=""{Binding !FlyoutApps.Count}""");
    }

    [Fact]
    public void TheAppsChecklist_ShowsTheIconThroughBase64ToImageConverter()
    {
        var card = FlyoutCard();

        card.Should().MatchRegex(
            @"<Image[^>]*Source=""\{Binding IconBase64, Converter=\{x:Static local:Base64ToImageConverter\.Instance\}\}""");
    }

    [Fact]
    public void EveryVisibleString_RoutesThroughLocalize()
    {
        var card = FlyoutCard();

        // Every localizable string in this card is one of: local:Localize (static text/tooltips) or
        // a Binding (per-item DisplayName/Label, which is themselves built from Localize keys in the
        // view model — RebuildFlyoutTiles reads LocalizationService.Instance[...]). No bare English
        // literal Text="..." should appear.
        Regex.Matches(card, @"Text=""(?!\{)[^""]+""").Should().BeEmpty(
            "every static string in the Flyout card must be a {local:Localize ...} markup extension");
    }

    [Fact]
    public void TheFlyoutCard_AddsExactlyOneInlineSize()
    {
        Regex.Matches(FlyoutCard(), @"FontSize=""\d").Count.Should().Be(1,
            "only the section header, like its siblings — every other size on this card comes from a Theme");
    }

    [Fact]
    public void ConfigureViewModel_ReloadsTheAppsChecklistOnAttach()
    {
        // Fix round 1 (RemEx-4kv0g.18.6 review, HIGH 2): ShellViewModel caches CustomizationViewModel
        // with ??=, and the constructor's own RefreshFlyoutApps runs exactly once - without a call
        // here too, an app added to the launcher after the sheet first opened never appears.
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "PersonalizationPanelView.axaml.cs"));

        var method = Regex.Match(code,
            @"private void ConfigureViewModel\(\)\s*\{(?<body>.*?)\n    \}",
            RegexOptions.Singleline);

        method.Success.Should().BeTrue("ConfigureViewModel moved or was reshaped - re-point this test rather than deleting it");
        method.Groups["body"].Value.Should().Contain("vm.RefreshFlyoutApps()",
            "the Apps checklist must reload every time the sheet attaches, not only the first time it is constructed");
    }

    /// <summary>Markup from the Flyout header's card open tag to that card's closing tag.</summary>
    private static string FlyoutCard()
    {
        var markup = PanelMarkup();
        var header = markup.IndexOf("Localize Custom_SectionFlyout}", System.StringComparison.Ordinal);
        header.Should().BeGreaterOrEqualTo(0, "the Flyout section must be on the sheet");
        var open = markup.LastIndexOf("<material:Card", header, System.StringComparison.Ordinal);
        var close = markup.IndexOf("</material:Card>", header, System.StringComparison.Ordinal);
        return markup.Substring(open, close - open);
    }

    private static string PanelMarkup() => File.ReadAllText(
        Path.Combine(RepoRoot(), "remex.desktop", "Views", "PersonalizationPanelView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
