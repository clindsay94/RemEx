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
public class SensorTextThemeTests
{
    [Fact]
    public void CanvasCardTitle_TakesTheSensorTitleTheme_WithNoInlineWeight()
    {
        var canvas = View("CanvasView.axaml");

        var title = Regex.Match(canvas, @"<TextBlock Text=""\{Binding Sensor\.DisplayName, FallbackValue=Sensor\}""[^>]*/>");
        title.Success.Should().BeTrue("the canvas card title binds Sensor.DisplayName with the Sensor fallback");
        title.Value.Should().Contain(@"Theme=""{StaticResource SensorTitleTextBlock}""");
        title.Value.Should().NotContain("FontWeight=", "an inline weight outranks the theme and would make the Bold switch a no-op here");
        canvas.Should().NotContain(@"Theme=""{StaticResource CaptionTextBlock}"" FontWeight=""Bold"" Foreground=""{Binding Sensor.Theme.LabelColor",
            "the old Caption+Bold title must be gone, not duplicated");
    }

    [Fact]
    public void CanvasDualMetricLegend_TakesTheMetricNameTheme_ForBothNames()
    {
        var canvas = View("CanvasView.axaml");

        Regex.Matches(canvas, @"Text=""\{Binding Sensor\.DisplayName\}"" Theme=""\{StaticResource SensorMetricNameTextBlock\}""").Count.Should().Be(1);
        Regex.Matches(canvas, @"Text=""\{Binding Sensor\.SecondarySensor\.DisplayName\}"" Theme=""\{StaticResource SensorMetricNameTextBlock\}""").Count.Should().Be(1);
    }

    [Fact]
    public void TheValuePlates_AreUntouched()
    {
        var canvas = View("CanvasView.axaml");

        canvas.Should().Contain(@"IsVisible=""{Binding Sensor.ShowValueOverlay}""");
        canvas.Should().Contain(@"<TextBlock Theme=""{StaticResource Headline6TextBlock}"" FontWeight=""Black"" Foreground=""{Binding Sensor.Theme.ValueColor");
        canvas.Should().Contain(@"<TextBlock Text=""{Binding Sensor.Unit}"" Theme=""{StaticResource CaptionTextBlock}""");
        canvas.Should().Contain(@"<TextBlock Theme=""{StaticResource Body1TextBlock}"" FontWeight=""Black"" Foreground=""{Binding Sensor.SecondaryAccentHex");
        canvas.Should().Contain(@"<TextBlock Text=""{Binding Sensor.SecondarySensor.Unit}"" Theme=""{StaticResource OverlineTextBlock}""");
    }

    [Fact]
    public void TheBackdrop_IsASiblingBorderBehindTheTitle_OnCanvasAndHome()
    {
        foreach (var (file, brushPath) in new[] { ("CanvasView.axaml", "Sensor.Theme.CardBackground"), ("HomeView.axaml", "Theme.CardBackground") })
        {
            var markup = View(file);
            var panel = Regex.Match(markup,
                @"<Panel[^>]*>\s*(?:<!--.*?-->\s*)?<Border IsVisible=""\{DynamicResource Typo\.Sensor\.TitleBackdrop\}""(?<border>[^>]*)/>\s*<TextBlock[^>]*Theme=""\{StaticResource SensorTitleTextBlock\}""[^>]*/>\s*</Panel>",
                RegexOptions.Singleline);

            panel.Success.Should().BeTrue($"{file}: the backdrop is a SIBLING drawn behind the title, never a wrapper that would hide the title with it");
            panel.Groups["border"].Value.Should().Contain(@"CornerRadius=""6""")
                .And.Contain($"{{Binding {brushPath}, Converter={{x:Static conv:HexToTranslucentBrushConverter.Instance}}, ConverterParameter=0.8}}",
                    "the backdrop is the value plate's own treatment (CanvasView.axaml value plate), not a new look");
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

        files.Should().Equal("CanvasView.axaml", "HomeView.axaml");
    }

    private static string View(string file) => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", file));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
