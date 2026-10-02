using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests;

/// <summary>
/// Page titles pick a font that covers the UI language's script (RemEx-kq10x.4). The bundled display
/// fonts are Latin-only, so Ukrainian gets Victor Mono and Hindi the platform default sans.
/// </summary>
public class PageDisplayFontRuleTests
{
    private const string Orbitron = "avares://Remex.Desktop/Assets/Fonts/Orbitron-*.ttf#Orbitron";
    private const string BungeeShade = "avares://Remex.Desktop/Assets/Fonts/BungeeShade-Regular.ttf#Bungee Shade";

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("id")]
    [InlineData("pl")]
    [InlineData("pt-BR")]
    [InlineData("tr")]
    [InlineData("")]
    [InlineData(null)]
    public void LatinScriptLanguagesKeepTheConfiguredFont(string? culture)
    {
        PageDisplayFontRule.ForLanguage(culture, Orbitron).Should().Be(Orbitron);
        PageDisplayFontRule.ForLanguage(culture, BungeeShade).Should().Be(BungeeShade);
    }

    [Theory]
    [InlineData("uk")]
    [InlineData("uk-UA")]
    [InlineData("UK")]
    public void UkrainianSwapsALatinOnlyBundledFontForVictorMono(string culture)
    {
        PageDisplayFontRule.ForLanguage(culture, Orbitron).Should().Be(PageDisplayFontRule.VictorMonoUri);
        PageDisplayFontRule.ForLanguage(culture, BungeeShade).Should().Be(PageDisplayFontRule.VictorMonoUri);
    }

    [Fact]
    public void UkrainianKeepsVictorMonoWhenItIsAlreadyChosen()
    {
        PageDisplayFontRule.ForLanguage("uk", PageDisplayFontRule.VictorMonoUri)
            .Should().Be(PageDisplayFontRule.VictorMonoUri);
    }

    [Theory]
    [InlineData("hi")]
    [InlineData("hi-IN")]
    public void HindiUsesThePlatformDefaultForAnyBundledFont(string culture)
    {
        PageDisplayFontRule.ForLanguage(culture, Orbitron).Should().Be(PageDisplayFontRule.PlatformDefault);
        PageDisplayFontRule.ForLanguage(culture, PageDisplayFontRule.VictorMonoUri)
            .Should().Be(PageDisplayFontRule.PlatformDefault, "Victor Mono has no Devanagari either");
    }

    [Theory]
    [InlineData("uk")]
    [InlineData("hi")]
    public void AFontInstalledOnTheSystemIsNeverSecondGuessed(string culture)
    {
        PageDisplayFontRule.ForLanguage(culture, "Segoe UI").Should().Be("Segoe UI");
    }
}
