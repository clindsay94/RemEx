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
    {
        "Sensor.Theme.", "Theme.AccentColor", "Theme.CardBackground", "SecondaryAccentHex",
        // Home/tray bind SensorViewModel directly (unprefixed) — "Binding Theme." catches an
        // inline "{Binding Theme.X}" leaking back into HomeView.axaml/TrayFlyoutWindow.axaml the
        // same way "Sensor.Theme." catches the canvas card's prefixed form (whole-branch review).
        // Deliberately "Binding Theme." not bare "Theme.": CanvasView.axaml's XAML namespace/type
        // names never collide with this, and the SensorCardFamilies.axaml exemption below still
        // applies since these tests never scan that file.
        "Binding Theme.",
    };

    // (folder under remex.desktop, file name). SensorCardContent.axaml lives under Controls/, not
    // Views/ (RemEx-4kv0g.18.1 extracted the card content out of CanvasView.axaml into it), so each
    // entry carries its own folder rather than assuming "Views" for every file.
    private static readonly (string Folder, string File)[] Views =
    {
        ("Views", "CanvasView.axaml"),
        ("Views", "HomeView.axaml"),
        ("Views", "TrayFlyoutWindow.axaml"),
        ("Controls", "SensorCardContent.axaml"),
    };

    [Theory]
    [MemberData(nameof(ViewFiles))]
    public void NoSensorCardViewBindsATileColourInline(string folder, string fileName)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", folder, fileName));

        foreach (var pattern in BannedPatterns)
            text.Should().NotContain(pattern,
                $"{fileName} must not bind {pattern} inline — that binding belongs in " +
                "Styles/SensorCardFamilies.axaml, scoped under a family-* or family-custom selector");
    }

    public static TheoryData<string, string> ViewFiles()
    {
        var data = new TheoryData<string, string>();
        foreach (var (folder, file) in Views)
            data.Add(folder, file);
        return data;
    }

    /// <summary>
    /// RemEx-4kv0g.18.1: the canvas-only "Sensor.Theme." binding path is gone entirely now that
    /// SensorCardContent's DataContext is the SensorViewModel on every host — the family-custom
    /// rules in SensorCardFamilies.axaml bind "Theme." directly (no prefix), never "Sensor.Theme.".
    /// </summary>
    [Fact]
    public void SensorCardFamiliesHasNoSensorThemePrefix()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Styles", "SensorCardFamilies.axaml"));
        text.Should().NotContain("Sensor.Theme.",
            "the family-custom rules should bind Theme.* directly now that every host's card " +
            "content is a SensorViewModel — no host still needs the canvas's old \"Sensor.\" prefix");
    }

    /// <summary>
    /// RemEx-4kv0g.18.2: the flyout's own card host (<c>Border.flyout-card</c>) gets the same
    /// per-family treatment <c>ctrl|DraggableCard</c> already has, and the old strip host class
    /// (<c>tray-sensor</c>) is gone along with the strip it painted.
    /// </summary>
    [Fact]
    public void SensorCardFamiliesHasAFlyoutCardRuleForEveryFamilyAndNoTraySensorRule()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Styles", "SensorCardFamilies.axaml"));

        foreach (var family in new[] { "primary", "secondary", "tertiary", "neutral" })
        {
            text.Should().Contain($"Border.flyout-card.family-{family}",
                $"the flyout card host should paint from the same {family} family brush as the canvas card");
        }

        text.Should().NotContain("tray-sensor",
            "the old tray strip's host class should be gone entirely now that the flyout renders cards");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
