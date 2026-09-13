using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// RemEx-jt6w5.4. Source scan: the Sensor section of Personalize → Text reaches every sensor-card
/// surface's TITLE and dual-metric METRIC NAMES through the two sensor ControlThemes, the optional
/// backdrop is the value plate's own treatment bound to Typo.Sensor.TitleBackdrop on the two card
/// surfaces and nowhere else, and the value plates themselves are untouched.
/// </summary>
/// <remarks>
/// RemEx-4kv0g.18.1 moved the canvas card's content out of Views/CanvasView.axaml into
/// Controls/SensorCardContent.axaml, and dropped the "Sensor." binding prefix along with it (the
/// control's content binds the SensorViewModel directly). Assertions below that used to read
/// CanvasView.axaml for the card CONTENT now read SensorCardContent.axaml with that prefix
/// dropped; assertions about the canvas HOST (ctrl:DraggableCard class bindings, which stayed in
/// CanvasView.axaml) are unaffected and not present in this file.
/// </remarks>
public class SensorTextThemeTests
{
    [Fact]
    public void SensorCardTitle_TakesTheSensorTitleTheme_WithNoInlineWeight()
    {
        var content = Control("SensorCardContent.axaml");

        var title = Regex.Match(content, @"<TextBlock Text=""\{Binding DisplayName, FallbackValue=Sensor\}""[^>]*/>");
        title.Success.Should().BeTrue("the sensor card title binds DisplayName with the Sensor fallback");
        title.Value.Should().Contain(@"Theme=""{StaticResource SensorTitleTextBlock}""");
        title.Value.Should().NotContain("FontWeight=", "an inline weight outranks the theme and would make the Bold switch a no-op here");
        content.Should().NotContain(@"Theme=""{StaticResource CaptionTextBlock}"" FontWeight=""Bold"" Foreground=""{Binding Theme.LabelColor",
            "the old Caption+Bold title must be gone, not duplicated");
    }

    [Fact]
    public void DualMetricLegend_TakesTheMetricNameTheme_ForBothNames()
    {
        var content = Control("SensorCardContent.axaml");

        Regex.Matches(content, @"Text=""\{Binding DisplayName\}"" Theme=""\{StaticResource SensorMetricNameTextBlock\}""").Count.Should().Be(1);
        Regex.Matches(content, @"Text=""\{Binding SecondarySensor\.DisplayName\}"" Theme=""\{StaticResource SensorMetricNameTextBlock\}""").Count.Should().Be(1);
    }

    [Fact]
    public void TheValuePlates_AreUntouched()
    {
        // Spec B (RemEx-4kv0g.3.2) moved the value plates' colour off these elements and onto
        // "card-value"/"card-unit" style classes (Styles/SensorCardFamilies.axaml), so the pinned
        // shape here is the STRUCTURE — theme, text bindings, layout — not the colour attribute,
        // which SensorCardBindingsTests now guards from the other direction (must NOT be inline).
        var content = Control("SensorCardContent.axaml");

        content.Should().Contain(@"IsVisible=""{Binding ShowValueOverlay}""");
        content.Should().Contain(@"<TextBlock Theme=""{StaticResource Headline6TextBlock}"" FontWeight=""Black"" Classes=""card-value"">");
        content.Should().Contain(@"<TextBlock Text=""{Binding Unit}"" Theme=""{StaticResource CaptionTextBlock}"" Classes=""card-unit""");
        content.Should().Contain(@"<TextBlock Theme=""{StaticResource Body1TextBlock}"" FontWeight=""Black"" Classes=""card-value-2"">",
            "the secondary value's colour lives in the card-value-2 style class (RemEx-4kv0g.3.2) — SecondaryAccentHex is bound only inside SensorCardFamilies.axaml");
        content.Should().Contain(@"<TextBlock Text=""{Binding SecondarySensor.Unit}"" Theme=""{StaticResource OverlineTextBlock}"" Classes=""card-unit""");
    }

    [Fact]
    public void TheBackdrop_IsASiblingBorderBehindTheTitle_OnTheCardContentAndHome()
    {
        // The backdrop's colour moved to the "card-pill" style class (Spec B, RemEx-4kv0g.3.2) —
        // same "family-{F} Border.card-pill" rule the value plate's own Background used to be
        // (CardPlate{F}Brush at 85%), so this still pins the structural claim the test is named
        // for: a SIBLING Border behind the title, never a wrapper. Canvas reads from
        // SensorCardContent.axaml now (RemEx-4kv0g.18.1); Home is untouched by that move.
        foreach (var (folder, file) in new[] { ("Controls", "SensorCardContent.axaml"), ("Views", "HomeView.axaml") })
        {
            var markup = Read(folder, file);
            var panel = Regex.Match(markup,
                @"<Panel[^>]*>\s*(?:<!--.*?-->\s*)?<Border IsVisible=""\{DynamicResource Typo\.Sensor\.TitleBackdrop\}""(?<border>[^>]*)/>\s*<TextBlock[^>]*Theme=""\{StaticResource SensorTitleTextBlock\}""[^>]*/>\s*</Panel>",
                RegexOptions.Singleline);

            panel.Success.Should().BeTrue($"{file}: the backdrop is a SIBLING drawn behind the title, never a wrapper that would hide the title with it");
            panel.Groups["border"].Value.Should().Contain(@"CornerRadius=""6""")
                .And.Contain(@"Classes=""card-pill""",
                    "the backdrop still carries the value plate's own treatment, now via the card-pill style class instead of an inline binding");
        }
    }

    [Fact]
    public void HomeTileTitle_TakesTheSensorTitleTheme_AndKeepsItsMonoFont()
    {
        View("HomeView.axaml").Should().Contain(
            @"<TextBlock Text=""{Binding Name}"" Theme=""{StaticResource SensorTitleTextBlock}"" FontFamily=""{StaticResource JetBrainsMono}""");
    }

    [Fact]
    public void TrayStripName_TakesTheMetricNameTheme_KeepingItsStripStyling()
    {
        View("TrayFlyoutWindow.axaml").Should().Contain(
            @"Theme=""{StaticResource SensorMetricNameTextBlock}"" FontWeight=""Black"" LetterSpacing=""1""");
    }

    [Fact]
    public void TheBackdrop_AppearsOnExactlyTheTwoCardSurfaces_AndNowhereElse()
    {
        var desktop = Path.Combine(RepoRoot(), "remex.desktop");
        var files = Directory.EnumerateFiles(desktop, "*.axaml", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("Typo.Sensor.TitleBackdrop"))
            .Select(f => Path.GetFileName(f))
            .OrderBy(f => f)
            .ToArray();

        // RemEx-4kv0g.18.1: CanvasView.axaml no longer carries this markup — it moved to
        // Controls/SensorCardContent.axaml — so that's the file name expected here now.
        files.Should().Equal("HomeView.axaml", "SensorCardContent.axaml");
    }

    private static string View(string file) => Read("Views", file);

    private static string Control(string file) => Read("Controls", file);

    private static string Read(string folder, string file) => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", folder, file));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
