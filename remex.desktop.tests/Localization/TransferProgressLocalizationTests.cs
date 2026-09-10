using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Localization;

/// <summary>
/// Every rate/ETA key RemEx-4lcq added is present, non-empty, and carries the same placeholders in
/// all nine <c>.resx</c> files.
/// </summary>
/// <remarks>
/// <c>scripts/check-localization.ps1</c> already runs this exact parity check across every key in
/// the project (and is the actual commit gate — see AGENTS.md). This test exists anyway because
/// <c>TransferProgressText.FormatRemaining</c> builds its resx key by string interpolation
/// (<c>$"{baseKey}_{category}"</c>), which is invisible to
/// <see cref="LocalizationKeyReferenceTests"/>'s regex-based reference scan — the one case that scan
/// documents as a known blind spot. Without a dedicated check, a locale that lost one of the four
/// plural-suffixed keys would only ever be caught by the PowerShell script, never by `dotnet test`.
/// </remarks>
public class TransferProgressLocalizationTests
{
    private static readonly string[] Locales = ["", "es", "fr", "hi", "id", "pl", "pt-BR", "tr", "uk"];

    /// <summary>The exact keys this bead added, independent of any locale's actual plural rule.</summary>
    private static IEnumerable<string> ExpectedKeys()
    {
        yield return "FileTransfer_RateFormat";
        yield return "FileTransfer_RateEtaFormat";
        yield return "FileTransfer_EtaFinishing";

        foreach (var unit in new[] { "Seconds", "Minutes", "Hours" })
        foreach (var category in new[] { "One", "Few", "Many", "Other" })
            yield return $"FileTransfer_Eta{unit}Format_{category}";
    }

    private static string PathFor(string locale) =>
        Path.Combine(RepoRoot(), "remex.desktop", "Localization",
            locale.Length == 0 ? "Strings.resx" : $"Strings.{locale}.resx");

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static Dictionary<string, string> LoadValues(string locale) =>
        XDocument.Load(PathFor(locale))
            .Root!
            .Elements("data")
            .Where(d => d.Element("value") is not null && (string?)d.Attribute("name") is not null)
            .ToDictionary(d => (string)d.Attribute("name")!, d => d.Element("value")!.Value, StringComparer.Ordinal);

    [Fact]
    public void EveryExpectedKey_ExistsAndIsNonEmpty_InEveryLocale()
    {
        var missing = new List<string>();

        foreach (var locale in Locales)
        {
            var values = LoadValues(locale);
            foreach (var key in ExpectedKeys())
            {
                if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                    missing.Add($"{(locale.Length == 0 ? "en" : locale)}/{key}");
            }
        }

        missing.Should().BeEmpty("every rate/ETA key must be present and translated in all nine files");
    }

    /// <summary>
    /// The plural-suffixed keys all carry <c>{0}</c>; the two non-plural format keys carry <c>{0}</c>
    /// (rate) or <c>{0}</c> and <c>{1}</c> (the joiner) — and every locale must agree, or a translator
    /// silently drops the argument.
    /// </summary>
    [Fact]
    public void PlaceholdersAgreeAcrossEveryLocale()
    {
        var english = LoadValues("");
        var offenders = new List<string>();

        foreach (var locale in Locales.Where(l => l.Length > 0))
        {
            var values = LoadValues(locale);
            foreach (var key in ExpectedKeys())
            {
                // "Finishing…" is the one key in this set with nothing to format - it is a plain
                // sentence, not a template.
                if (key == "FileTransfer_EtaFinishing")
                    continue;

                var expectedHasSecondArg = key == "FileTransfer_RateEtaFormat";
                var translated = values[key];

                var hasZero = translated.Contains("{0}", StringComparison.Ordinal);
                var hasOne = translated.Contains("{1}", StringComparison.Ordinal);

                if (!hasZero || (expectedHasSecondArg != hasOne))
                    offenders.Add($"{locale}/{key} = \"{translated}\"");
            }
        }

        english.Should().NotBeEmpty();
        offenders.Should().BeEmpty("a translation missing a placeholder silently drops that argument");
    }
}
