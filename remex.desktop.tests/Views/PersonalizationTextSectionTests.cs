using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// RemEx-jt6w5.5: the TEXT card sits between Look and Advanced fine-tuning, every control in it has an
/// accessible name, its Reset button speaks the button vocabulary, and it adds exactly one inline size
/// (the header, matching its five siblings) so the ratchet keeps its arithmetic.
/// </summary>
public class PersonalizationTextSectionTests
{
    [Fact]
    public void TheTextCard_SitsAboveAdvancedFineTuning_BelowLook()
    {
        var markup = PanelMarkup();
        var look = markup.IndexOf("Localize Custom_SectionLook}", System.StringComparison.Ordinal);
        var text = markup.IndexOf("Localize Custom_SectionText}", System.StringComparison.Ordinal);
        var tuning = markup.IndexOf("Localize Custom_AdvancedTuning}", System.StringComparison.Ordinal);

        text.Should().BeGreaterThan(look).And.BeLessThan(tuning,
            "the readability control comes before the advanced overrides (spec § Personalize UI)");
    }

    [Fact]
    public void EveryControlInTheTextCard_HasAnAccessibleName()
    {
        var card = TextCard();
        var controls = Regex.Matches(card, @"<(Slider|ToggleSwitch|Button)\b[^>]*>").Select(m => m.Value).ToArray();

        controls.Length.Should().BeGreaterOrEqualTo(12, "5 sliders, 6 switches and the reset button");
        controls.Where(c => !c.Contains("AutomationProperties.Name=")).Should().BeEmpty();
    }

    [Fact]
    public void TheTextCard_BindsEveryViewModelProperty()
    {
        var card = TextCard();

        foreach (var binding in new[]
                 {
                     "HeadersScale", "BodyScale", "SmallScale", "SensorScale",
                     "HeadersBold", "BodyBold", "SmallBold", "SensorBold",
                     "SensorTitleBackdrop", "TextShadowEnabled", "TextShadowStrength",
                     "HeadersSizeLabel", "BodySizeLabel", "SmallSizeLabel", "SensorSizeLabel",
                     "ResetTextToDefaultsCommand",
                 })
        {
            card.Should().Contain($"{{Binding {binding}", binding);
        }
    }

    [Fact]
    public void TheSizeSliders_CoverTheSpecRange_InFiveHundredthSteps()
    {
        Regex.Matches(TextCard(), @"<Slider Minimum=""0\.8"" Maximum=""1\.6"" SmallChange=""0\.05"" LargeChange=""0\.1"" TickFrequency=""0\.05"" IsSnapToTickEnabled=""True""")
            .Count.Should().Be(4);
    }

    [Fact]
    public void TheResetButton_IsTertiary_AndAddsOneInlineSizeOnly()
    {
        var card = TextCard();

        card.Should().Contain(@"<Button Classes=""tertiary"" Content=""{local:Localize Custom_TextReset}"" Command=""{Binding ResetTextToDefaultsCommand}""");
        Regex.Matches(card, @"FontSize=""\d").Count.Should().Be(1, "only the section header, like its siblings — every other size comes from a Theme");
    }

    /// <summary>Markup from the TEXT header's card open tag to that card's closing tag.</summary>
    private static string TextCard()
    {
        var markup = PanelMarkup();
        var header = markup.IndexOf("Localize Custom_SectionText}", System.StringComparison.Ordinal);
        header.Should().BeGreaterOrEqualTo(0, "the TEXT section must be on the sheet");
        var open = markup.LastIndexOf("<material:Card", header, System.StringComparison.Ordinal);
        var close = markup.IndexOf("</material:Card>", header, System.StringComparison.Ordinal);
        return markup.Substring(open, close - open);
    }

    private static string PanelMarkup() => File.ReadAllText(
        Path.Combine(RepoRoot(), "remex.desktop", "Views", "PersonalizationPanelView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
