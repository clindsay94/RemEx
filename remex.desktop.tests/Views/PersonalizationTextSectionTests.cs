using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// RemEx-jt6w5.5: the TEXT card sits above the FONTS sub-heading on the Text tab (spec
/// 2026-09-13-personalize-tabs §2), every control in it has an accessible name, its Reset button
/// speaks the button vocabulary, and it adds exactly one inline size (the header, matching its five
/// siblings) so the ratchet keeps its arithmetic. Re-pointed at PersonalizeTextTab.axaml
/// (RemEx-4kv0g.4.3); assertions on the card's own contents unchanged.
/// </summary>
public class PersonalizationTextSectionTests
{
    [Fact]
    public void TheTextCard_SitsAboveTheFontsSubHeading()
    {
        var markup = PanelMarkup();
        var reset = markup.IndexOf("Localize Custom_TextReset}", System.StringComparison.Ordinal);
        var fonts = markup.IndexOf("Localize Custom_SectionFonts}", System.StringComparison.Ordinal);

        reset.Should().BeGreaterThan(-1, "the TEXT block's Reset button must be on the tab");
        reset.Should().BeLessThan(fonts,
            "the readability block (through Reset) comes before the Fonts sub-heading (spec §2)");
    }

    [Fact]
    public void EveryControlInTheTextCard_HasAnAccessibleName()
    {
        var card = TextCard();
        var controls = Regex.Matches(card, @"<(Slider|ToggleSwitch|Button)\b[^>]*>").Select(m => m.Value).ToArray();

        controls.Length.Should().BeGreaterOrEqualTo(13, "6 sliders (RemEx-n6csl adds Subtitles), 6 switches and the reset button");
        controls.Where(c => !c.Contains("AutomationProperties.Name=")).Should().BeEmpty();
    }

    [Fact]
    public void TheTextCard_BindsEveryViewModelProperty()
    {
        var card = TextCard();

        foreach (var binding in new[]
                 {
                     "HeadersScale", "SubtitlesScale", "BodyScale", "SmallScale", "SensorScale",
                     "HeadersBold", "SubtitlesBold", "BodyBold", "SmallBold", "SensorBold",
                     "SensorTitleBackdrop", "TextShadowEnabled", "TextShadowStrength",
                     "HeadersSizeLabel", "SubtitlesSizeLabel", "BodySizeLabel", "SmallSizeLabel", "SensorSizeLabel",
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
            .Count.Should().Be(5, "Headers, Subtitles (RemEx-n6csl), Body, Small, Sensor");
    }

    [Fact]
    public void TheResetButton_IsTertiary_AndAddsNoInlineSize()
    {
        var card = TextCard();

        card.Should().Contain(@"<Button Classes=""tertiary"" Content=""{local:Localize Custom_TextReset}"" Command=""{Binding ResetTextToDefaultsCommand}""");
        // The tab strip is the header now (RemEx-4kv0g.4.3) - this block's own Custom_SectionText
        // header is retired, so unlike its card-era siblings this scope adds ZERO inline sizes;
        // every other size still comes from a Theme.
        Regex.Matches(card, @"FontSize=""\d").Count.Should().Be(0, "the section header that used to add one is gone - the tab strip is the header now");
    }

    /// <summary>Markup from the TEXT block's card open tag through the Reset button's close - the
    /// original TEXT card's own scope, now the first of two blocks sharing one material:Card with
    /// the Fonts sub-heading appended after it (spec §2).</summary>
    private static string TextCard()
    {
        var markup = PanelMarkup();
        var anchor = markup.IndexOf("Localize Custom_TextHeaders}", System.StringComparison.Ordinal);
        anchor.Should().BeGreaterOrEqualTo(0, "the TEXT block must be on the Text tab");
        var open = markup.LastIndexOf("<material:Card", anchor, System.StringComparison.Ordinal);
        var resetCommand = markup.IndexOf("ResetTextToDefaultsCommand", anchor, System.StringComparison.Ordinal);
        resetCommand.Should().BeGreaterThan(0, "the Reset button closes out the TEXT block");
        var close = markup.IndexOf("/>", resetCommand, System.StringComparison.Ordinal) + 2;
        return markup.Substring(open, close - open);
    }

    private static string PanelMarkup() => File.ReadAllText(
        Path.Combine(RepoRoot(), "remex.desktop", "Views", "Personalize", "PersonalizeTextTab.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
