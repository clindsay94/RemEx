using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Spec B §6 (RemEx-4kv0g.3.1/.3.2): ThemeService publishes 32 literal per-family card resources —
/// eight keys × four families — next to the existing role keys. The eight are the six from .3.1
/// (Body/Plate/Ink/InkDim brushes, plus the Series/SeriesTwo Colors) and the two Brush variants of
/// the series colours added in .3.2 for the legend swatches, which need a Brush rather than a Color.
/// Same source-scan approach as <c>MaterialRoleKeysTests</c>: the coverage regex needs literal keys,
/// so a loop that generated the names would be invisible to it too.
/// </summary>
public class SensorCardKeysTests
{
    private static readonly string[] Families = { "Primary", "Secondary", "Tertiary", "Neutral" };

    private static readonly string[] KeyShapes =
    {
        "CardBody{0}Brush", "CardPlate{0}Brush", "CardInk{0}Brush", "CardInkDim{0}Brush",
        "CardSeries{0}", "CardSeriesTwo{0}", "CardSeries{0}Brush", "CardSeriesTwo{0}Brush",
    };

    [Fact]
    public void AllThirtyTwoSensorCardKeysArePublished()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Services", "ThemeService.cs"));
        var published = Regex.Matches(source, @"SetResourceOverrideInternal\(""([^""]+)""").Select(m => m.Groups[1].Value).ToHashSet();
        published.Should().NotBeEmpty("the scan must see the literal keys — a loop with interpolated names is invisible to ThemeKeyCoverageTests too");

        var expectedKeys = Families.SelectMany(f => KeyShapes.Select(shape => string.Format(shape, f))).ToArray();
        expectedKeys.Should().HaveCount(32);

        foreach (var key in expectedKeys)
            published.Should().Contain(key, $"sensor cards need the {key} resource");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
