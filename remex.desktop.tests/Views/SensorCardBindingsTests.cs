using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Spec B (RemEx-4kv0g.3.2): every per-theme colour a sensor card, Home tile or tray strip element
/// carries must come from a "family-*"/"family-custom" style rule in
/// <c>Styles/SensorCardFamilies.axaml</c>, never from a value set directly on the element — a local
/// attribute always beats a Style setter in Avalonia regardless of selector specificity, so a single
/// re-added inline binding would silently repaint that one part back to its pre-family colour and
/// never move again with the palette. This is the regression this task is most likely to suffer.
/// </summary>
public class SensorCardBindingsTests
{
    private static readonly string[] BannedPatterns =
        { "Sensor.Theme.", "Theme.AccentColor", "Theme.CardBackground", "SecondaryAccentHex" };

    private static readonly string[] Views = { "CanvasView.axaml", "HomeView.axaml", "TrayFlyoutWindow.axaml" };

    [Theory]
    [MemberData(nameof(ViewFiles))]
    public void NoSensorCardViewBindsATileColourInline(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", relativePath));

        foreach (var pattern in BannedPatterns)
            text.Should().NotContain(pattern,
                $"{relativePath} must not bind {pattern} inline — that binding belongs in " +
                "Styles/SensorCardFamilies.axaml, scoped under a family-* or family-custom selector");
    }

    public static TheoryData<string> ViewFiles()
    {
        var data = new TheoryData<string>();
        foreach (var view in Views)
            data.Add(view);
        return data;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
