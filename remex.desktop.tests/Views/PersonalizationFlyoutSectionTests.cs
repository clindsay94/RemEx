using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Flyout D2 .2 (RemEx-4kv0g.18.6): the tray pop-up section's four controls bind
/// FlyoutOpacity/FlyoutCards/FlyoutTiles/FlyoutApps and every visible string routes through
/// <c>local:Localize</c>. Moved from Personalize › Layout to Settings in RemEx-172ba
/// (Views/TrayFlyoutSettingsView.axaml), with the Settings section-header style and so no inline size.
/// Source-text, like its siblings — <c>remex.desktop.tests</c> has no headless render.
/// </summary>
public class PersonalizationFlyoutSectionTests
{
    [Fact]
    public void TheTrayPopUpCard_IsInSettings_BoundToTheShellsCustomizationViewModel()
    {
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "SettingsView.axaml"));

        settings.Should().Contain(@"<views:TrayFlyoutSettingsView DataContext=""{Binding TrayFlyoutSettings}""/>");
        File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "Personalize", "PersonalizeLayoutTab.axaml"))
            .Should().NotContain("Custom_SectionFlyout", "Personalize is for looks only (RemEx-172ba)");
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
    public void TheFlyoutCard_HasNoInlineSize()
    {
        Regex.Matches(FlyoutCard(), @"FontSize=""\d").Count.Should().Be(0,
            "the header uses the Settings section-header Theme, like every other Settings card");
    }

    [Fact]
    public void ConfigureViewModel_ReloadsTheAppsChecklistOnAttach()
    {
        // Fix round 1 (RemEx-4kv0g.18.6 review, HIGH 2): ShellViewModel caches CustomizationViewModel
        // with ??=, and the constructor's own RefreshFlyoutApps runs exactly once - without a call
        // here too, an app added to the launcher after the section first showed never appears.
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "TrayFlyoutSettingsView.axaml.cs"));

        var method = Regex.Match(code,
            @"private void ConfigureViewModel\(\)\s*\{(?<body>.*?)\n    \}",
            RegexOptions.Singleline);

        method.Success.Should().BeTrue("ConfigureViewModel moved or was reshaped - re-point this test rather than deleting it");
        method.Groups["body"].Value.Should().Contain("vm.RefreshFlyoutApps()",
            "the Apps checklist must reload every time the section attaches, not only the first time it is constructed");
    }

    /// <summary>The tray pop-up section's whole markup.</summary>
    private static string FlyoutCard()
    {
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "TrayFlyoutSettingsView.axaml"));
        markup.Should().Contain("Localize Custom_SectionFlyout}", "the section keeps its heading");
        return markup;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
