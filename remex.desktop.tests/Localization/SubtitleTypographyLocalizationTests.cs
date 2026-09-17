using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Localization;

/// <summary>
/// RemEx-n6csl: the five new Personalize → Text strings the Subtitles row and Subtitle-font picker
/// need (<c>Custom_TextSubtitles</c>, its tooltip, its Bold switch's accessible name,
/// <c>Custom_SubtitleFont</c> and its tooltip) exist in all nine locale files, the same nine-file
/// parity <see cref="RetiredPersonalizationStringsTests"/> enforces for the strings that already
/// existed. <see cref="LocalizationKeyReferenceTests"/> separately proves every key referenced in
/// source is at least defined in the base <c>Strings.resx</c>; this is the cross-locale half.
/// </summary>
public class SubtitleTypographyLocalizationTests
{
    private static readonly string[] NewKeys =
    {
        "Custom_TextSubtitles", "Custom_TextSubtitlesTip", "Custom_TextSubtitlesBold",
        "Custom_SubtitleFont", "Custom_SubtitleFontTip",
    };

    private static readonly string[] Files =
    {
        "Strings.resx", "Strings.es.resx", "Strings.fr.resx", "Strings.hi.resx", "Strings.id.resx",
        "Strings.pl.resx", "Strings.pt-BR.resx", "Strings.tr.resx", "Strings.uk.resx",
    };

    [Fact]
    public void TheFiveNewKeysExistInAllNineLocaleFiles()
    {
        foreach (var file in Files)
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Localization", file));
            foreach (var key in NewKeys)
                Regex.IsMatch(text, $@"<data name=""{Regex.Escape(key)}""").Should().BeTrue(
                    $"{file} is missing {key} - the Subtitles row/font picker must be localizable in every shipped language, not just English");
        }
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
