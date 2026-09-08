using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Localization;

/// <summary>
/// Key parity, across all 9 locale files, for <c>Tutorial_PageIndicator</c> - the
/// <c>AutomationProperties.Name</c> on the tutorial overlay's <c>PipsPager</c> (RemEx-9iz00.1).
/// </summary>
/// <remarks>
/// Same pattern as <c>BusyPlaceholderAndLogsFolderLocalizationTests</c>:
/// <c>scripts/check-localization.ps1</c> is the authoritative, repo-wide parity gate and already
/// covers this key, so this is a narrower, always-in-the-test-run regression pin for the specific
/// key this bead added rather than a reimplementation of that sweep.
/// </remarks>
public class TutorialPageIndicatorLocalizationTests
{
    private static readonly string[] LocaleSuffixes =
        ["", ".es", ".fr", ".hi", ".id", ".pl", ".pt-BR", ".tr", ".uk"];

    [Theory]
    [MemberData(nameof(Locales))]
    public void KeyHasANonEmptyValue_InEveryLocale(string suffix)
    {
        var doc = XDocument.Load(ResxPath(suffix));
        var value = doc.Root!
            .Elements("data")
            .FirstOrDefault(d => (string?)d.Attribute("name") == "Tutorial_PageIndicator")?
            .Element("value")?.Value;

        value.Should().NotBeNullOrWhiteSpace(
            $"Tutorial_PageIndicator must be declared with a real translation in Strings{suffix}.resx");
    }

    public static IEnumerable<object[]> Locales() =>
        LocaleSuffixes.Select(suffix => new object[] { suffix });

    private static string ResxPath(string suffix) =>
        Path.Combine(RepoRoot(), "remex.desktop", "Localization", $"Strings{suffix}.resx");

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
