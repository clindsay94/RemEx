using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Localization;

/// <summary>
/// The PC's About page and the phone's About screen show the same What's New list for a release:
/// the same number of items, in the same order, translated in all nine languages on both sides.
/// </summary>
/// <remarks>
/// Before 3.0 the two had drifted apart completely (RemEx-pp4cm). The PC read English developer
/// notes out of the bundled CHANGELOG, cut to two lines and untranslated, with a 2.0 list as its
/// fallback; the phone listed 2.5 items and advertised sensor alerts, a feature only the PC has. These
/// tests pin the join: <see cref="AboutViewModel.WhatsNewCount"/> drives the PC list, the phone's
/// AboutScreen.kt has to reference exactly that many entries, and every key has to exist everywhere.
/// </remarks>
public sealed class WhatsNewParityTests : IDisposable
{
    private static readonly string[] PcLocales = ["", "es", "fr", "hi", "id", "pl", "pt-BR", "tr", "uk"];

    private static readonly string[] AndroidValueDirs =
        ["values", "values-es", "values-fr", "values-hi", "values-in", "values-pl", "values-pt-rBR", "values-tr", "values-uk"];

    private readonly List<AboutViewModel> _created = [];

    public void Dispose()
    {
        foreach (var about in _created) about.Dispose();
        GC.SuppressFinalize(this);
    }

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

    private static Dictionary<string, string> AndroidValues(string dir)
    {
        var path = Path.Combine(RepoRoot(), "remex.android", "app", "src", "main", "res", dir, "strings.xml");
        return XDocument.Load(path).Root!
            .Elements("string")
            .Where(s => (string?)s.Attribute("name") is not null)
            .GroupBy(s => (string)s.Attribute("name")!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
    }

    private static IEnumerable<string> PcKeys(int n) => [$"About_WhatsNew_{n}_Title", $"About_WhatsNew_{n}_Body"];

    private static IEnumerable<string> AndroidKeys(int n) => [$"about_whats_new_{n}_label", $"about_whats_new_{n}_body"];

    private static int Highest(IEnumerable<string> keys, string pattern) =>
        keys.Select(k => Regex.Match(k, pattern))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .DefaultIfEmpty(0)
            .Max();

    [Fact]
    public void ThePcAboutPageListsTheLocalizedHighlightsInOrder()
    {
        // Driven through the view model the page binds to, so a return to the changelog (or to any
        // other source) goes red even though every key below would still exist.
        var about = new AboutViewModel(new ConnectionViewModel(), null!);
        _created.Add(about);

        var expected = Enumerable.Range(1, AboutViewModel.WhatsNewCount)
            .Select(n => new WhatsNewItem(
                LocalizationService.Instance[$"About_WhatsNew_{n}_Title"],
                LocalizationService.Instance[$"About_WhatsNew_{n}_Body"]))
            .ToList();

        about.WhatsNewItems.Should().Equal(expected);
        about.WhatsNewItems.Should().NotContain(i => i.Version.StartsWith("About_WhatsNew_", StringComparison.Ordinal),
            "a key that resolves to itself means the string is missing and the raw key is on screen");
    }

    [Fact]
    public void ALanguageSwitchRebuildsTheList()
    {
        var previous = LocalizationService.Instance.CultureTag;
        try
        {
            LocalizationService.Instance.SetCulture("en");
            var about = new AboutViewModel(new ConnectionViewModel(), null!);
            _created.Add(about);
            var english = about.WhatsNewItems.Select(i => i.Version).ToList();

            LocalizationService.Instance.SetCulture("fr");
            about.WhatsNewItems.Should().HaveCount(AboutViewModel.WhatsNewCount);
            about.WhatsNewItems.Select(i => i.Version).Should().NotEqual(english,
                "the highlights are translated, so French has to replace the English titles");
            about.WhatsNewItems[1].Version.Should().Be(PcValues("fr")["About_WhatsNew_2_Title"]);
        }
        finally
        {
            LocalizationService.Instance.SetCulture(previous);
        }
    }

    [Fact]
    public void BothPlatformsDefineExactlyWhatsNewCountItems()
    {
        var pcEnglish = PcValues("");
        var androidEnglish = AndroidValues("values");

        Highest(pcEnglish.Keys, @"^About_WhatsNew_(\d+)_(?:Title|Body)$").Should().Be(AboutViewModel.WhatsNewCount,
            "an About_WhatsNew_N key past WhatsNewCount is a highlight the PC never shows");
        Highest(androidEnglish.Keys, @"^about_whats_new_(\d+)_(?:label|body)$").Should().Be(AboutViewModel.WhatsNewCount,
            "the phone has to list the same number of highlights as the PC");
    }

    [Fact]
    public void EveryKeyExistsAndIsNonEmptyInAllNineLocalesOnBothPlatforms()
    {
        var missing = new List<string>();
        foreach (var locale in PcLocales)
        {
            var values = PcValues(locale);
            foreach (var key in Enumerable.Range(1, AboutViewModel.WhatsNewCount).SelectMany(PcKeys))
            {
                if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                    missing.Add($"pc {(locale.Length == 0 ? "en" : locale)}: {key}");
            }
        }

        foreach (var dir in AndroidValueDirs)
        {
            var values = AndroidValues(dir);
            foreach (var key in Enumerable.Range(1, AboutViewModel.WhatsNewCount).SelectMany(AndroidKeys))
            {
                if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                    missing.Add($"android {dir}: {key}");
            }
        }

        missing.Should().BeEmpty();
    }

    [Fact]
    public void ThePhoneAboutScreenShowsEveryItem()
    {
        // The strings existing is not the phone showing them: AboutScreen.kt used to hard-code five
        // entries. Each label and body has to be referenced, and nothing past the count.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.android", "app", "src", "main", "java",
            "com", "clindsay94", "remex", "ui", "screens", "AboutScreen.kt"));

        foreach (var key in Enumerable.Range(1, AboutViewModel.WhatsNewCount).SelectMany(AndroidKeys))
            source.Should().Contain($"R.string.{key}", "AboutScreen.kt has to show every highlight");

        Highest(Regex.Matches(source, @"R\.string\.(about_whats_new_\d+_(?:label|body))").Select(m => m.Groups[1].Value),
                @"^about_whats_new_(\d+)_").Should().Be(AboutViewModel.WhatsNewCount);
    }

    [Fact]
    public void TheCopyIsPlain()
    {
        var offenders = new List<string>();
        foreach (var locale in PcLocales)
        {
            var values = PcValues(locale);
            foreach (var key in Enumerable.Range(1, AboutViewModel.WhatsNewCount).SelectMany(PcKeys))
            {
                if (values.TryGetValue(key, out var v) && (v.Contains('—') || v.Contains('–')))
                    offenders.Add($"pc {(locale.Length == 0 ? "en" : locale)}: {key} has a dash");
            }
        }

        foreach (var dir in AndroidValueDirs)
        {
            var values = AndroidValues(dir);
            foreach (var key in Enumerable.Range(1, AboutViewModel.WhatsNewCount).SelectMany(AndroidKeys))
            {
                if (values.TryGetValue(key, out var v) && (v.Contains('—') || v.Contains('–')))
                    offenders.Add($"android {dir}: {key} has a dash");
            }
        }

        offenders.Should().BeEmpty();
    }
}
