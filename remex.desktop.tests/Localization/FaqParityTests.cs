using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Localization;

/// <summary>
/// <c>docs/FAQ-PARITY.md</c> is the canonical FAQ list for both apps (R-UX-44, routines spec §5.5).
/// Every row has to resolve to real keys in all nine locale files on both platforms, and the PC's
/// <c>AboutViewModel</c> has to enumerate exactly as many entries as the table has rows.
/// </summary>
/// <remarks>
/// An Android cell marked <c>(pending)</c> is a row that has shipped on the PC ahead of the phone
/// (RemEx-pp0rt.11: FAQ 17-22 land on Android in the phone batch). Such a cell is skipped here, and
/// the marker is removed from the table when the Android keys exist, at which point this test checks
/// them like every other row.
/// </remarks>
public class FaqParityTests
{
    private static readonly string[] PcLocales = ["", "es", "fr", "hi", "id", "pl", "pt-BR", "tr", "uk"];

    private static readonly string[] AndroidValueDirs =
        ["values", "values-es", "values-fr", "values-hi", "values-in", "values-pl", "values-pt-rBR", "values-tr", "values-uk"];

    private sealed record Row(int Number, string Question, string PcKey, string AndroidCell)
    {
        public bool AndroidPending => AndroidCell.Contains("(pending)", StringComparison.Ordinal);
        public string AndroidKey => AndroidCell.Replace("(pending)", "", StringComparison.Ordinal).Trim();
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static List<Row> ParityRows()
    {
        var rows = new List<Row>();
        var rowPattern = new Regex(@"^\|\s*(\d+)\s*\|\s*(.+?)\s*\|\s*(Faq_Q\d+)\s*\|\s*(.+?)\s*\|\s*$");
        foreach (var line in File.ReadAllLines(Path.Combine(RepoRoot(), "docs", "FAQ-PARITY.md")))
        {
            var m = rowPattern.Match(line);
            if (m.Success)
            {
                rows.Add(new Row(int.Parse(m.Groups[1].Value), m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value));
            }
        }

        return rows;
    }

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

    [Fact]
    public void TheTableHasOneRowPerPcFaqEntryInOrder()
    {
        var rows = ParityRows();

        rows.Should().HaveCount(AboutViewModel.FaqCount,
            "AboutViewModel enumerates Faq_Q1..FaqCount, and every one of them must be a canonical row");
        rows.Select(r => r.Number).Should().Equal(Enumerable.Range(1, AboutViewModel.FaqCount));
        rows.Select(r => r.PcKey).Should().OnlyHaveUniqueItems()
            .And.BeEquivalentTo(Enumerable.Range(1, AboutViewModel.FaqCount).Select(n => $"Faq_Q{n}"));
    }

    [Fact]
    public void EveryPcKeyExistsAndIsNonEmptyInAllNineLocales()
    {
        var missing = new List<string>();
        foreach (var locale in PcLocales)
        {
            var values = PcValues(locale);
            foreach (var row in ParityRows())
            foreach (var suffix in new[] { "_Question", "_Answer" })
            {
                var key = row.PcKey + suffix;
                if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    missing.Add($"{(locale.Length == 0 ? "en" : locale)}: {key}");
                }
            }
        }

        missing.Should().BeEmpty();
    }

    [Fact]
    public void EveryAndroidKeyThatIsNotPendingExistsInAllNineLocales()
    {
        var missing = new List<string>();
        foreach (var dir in AndroidValueDirs)
        {
            var values = AndroidValues(dir);
            foreach (var row in ParityRows().Where(r => !r.AndroidPending))
            {
                var question = row.AndroidKey;
                var answer = Regex.Replace(question, @"^faq_q", "faq_a");
                foreach (var key in new[] { question, answer })
                {
                    if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                    {
                        missing.Add($"{dir}: {key}");
                    }
                }
            }
        }

        missing.Should().BeEmpty();
    }

    [Fact]
    public void TheRoutinesEntriesUsePlainCopyWithNoDashes()
    {
        // Spec §5.4 copy rules: plain sentences, no em or en dashes, in every language.
        var offenders = new List<string>();
        foreach (var locale in PcLocales)
        {
            var values = PcValues(locale);
            for (var n = 17; n <= 22; n++)
            foreach (var suffix in new[] { "_Question", "_Answer" })
            {
                var key = $"Faq_Q{n}{suffix}";
                if (values.TryGetValue(key, out var value) && (value.Contains('—') || value.Contains('–')))
                {
                    offenders.Add($"{(locale.Length == 0 ? "en" : locale)}: {key}");
                }
            }
        }

        offenders.Should().BeEmpty();
    }
}
