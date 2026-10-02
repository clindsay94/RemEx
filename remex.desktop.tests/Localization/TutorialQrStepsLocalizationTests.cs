using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Localization;

/// <summary>
/// The tutorial's QR pairing steps (<c>Tutorial_P8_QR_Steps</c>) have to name the button and the page
/// the PC actually shows. They told people to click "Show QR Code", a button that stopped existing when
/// the QR and the PIN became one act behind "Pair a phone" (RemEx-7ykyn); a user following the
/// tutorial looked for a label that was not there.
/// </summary>
/// <remarks>
/// Asserted against the real labels' own keys, per locale, rather than a pinned English sentence: if
/// the button is renamed again, this goes red in every language until the tutorial follows.
/// </remarks>
public class TutorialQrStepsLocalizationTests
{
    private static readonly string[] PcLocales = ["", "es", "fr", "hi", "id", "pl", "pt-BR", "tr", "uk"];

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static Dictionary<string, string> PcValues(string locale)
    {
        var path = Path.Combine(RepoRoot(), "remex.desktop", "Localization",
            locale.Length == 0 ? "Strings.resx" : $"Strings.{locale}.resx");
        return XDocument.Load(path).Root!
            .Elements("data")
            .Where(d => d.Element("value") is not null && (string?)d.Attribute("name") is not null)
            .ToDictionary(d => (string)d.Attribute("name")!, d => d.Element("value")!.Value, StringComparer.Ordinal);
    }

    [Fact]
    public void TheQrStepsNameThePairAPhoneButtonInEveryLocale()
    {
        var offenders = new List<string>();
        foreach (var locale in PcLocales)
        {
            var values = PcValues(locale);
            var steps = values["Tutorial_P8_QR_Steps"];
            var button = values["Home_PairPhoneButton"];
            if (!steps.Contains(button, StringComparison.Ordinal))
            {
                offenders.Add($"{(locale.Length == 0 ? "en" : locale)}: steps do not name \"{button}\"");
            }
        }

        offenders.Should().BeEmpty(
            "the Settings > Connection card's only pairing button is Home_PairPhoneButton, so the "
            + "tutorial has to send people to that label");
    }

    [Fact]
    public void TheQrStepsNameTheSettingsPageAndConnectionCardTheAppShows()
    {
        var offenders = new List<string>();
        foreach (var locale in PcLocales)
        {
            var values = PcValues(locale);
            var steps = values["Tutorial_P8_QR_Steps"];
            var path = $"{values["Nav_Settings"]} → {values["Settings_Connection"]}";
            if (!steps.Contains(path, StringComparison.Ordinal))
            {
                offenders.Add($"{(locale.Length == 0 ? "en" : locale)}: steps do not say \"{path}\"");
            }
        }

        offenders.Should().BeEmpty(
            "step 1 has to use the same words as the navigation item and the card heading");
    }
}
