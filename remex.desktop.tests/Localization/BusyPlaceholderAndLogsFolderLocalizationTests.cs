using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Localization;

/// <summary>
/// Key parity, across all 9 locale files, for the two strings this bead added (RemEx-kjdi):
/// the tray's "Open logs folder" item and the shared busy placeholder's accessible name.
/// </summary>
/// <remarks>
/// <c>scripts/check-localization.ps1</c> is the authoritative, repo-wide parity gate (Axis 1) and
/// already covers every key including these two — this test is a narrower, always-in-the-test-run
/// regression pin for the specific keys this bead is responsible for, not a reimplementation of
/// that sweep.
/// </remarks>
public class BusyPlaceholderAndLogsFolderLocalizationTests
{
    private static readonly string[] LocaleSuffixes =
        ["", ".es", ".fr", ".hi", ".id", ".pl", ".pt-BR", ".tr", ".uk"];

    private static readonly string[] Keys = ["Tray_Menu_OpenLogsFolder", "Busy_Loading"];

    [Theory]
    [MemberData(nameof(KeyAndLocale))]
    public void KeyHasANonEmptyValue_InEveryLocale(string key, string suffix)
    {
        var doc = XDocument.Load(ResxPath(suffix));
        var value = doc.Root!
            .Elements("data")
            .FirstOrDefault(d => (string?)d.Attribute("name") == key)?
            .Element("value")?.Value;

        value.Should().NotBeNullOrWhiteSpace(
            $"{key} must be declared with a real translation in Strings{suffix}.resx");
    }

    public static IEnumerable<object[]> KeyAndLocale()
    {
        foreach (var key in Keys)
        foreach (var suffix in LocaleSuffixes)
            yield return new object[] { key, suffix };
    }

    private static string ResxPath(string suffix) =>
        Path.Combine(RepoRoot(), "remex.desktop", "Localization", $"Strings{suffix}.resx");

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
