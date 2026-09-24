using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// Perf audit P0-2: the Aurora layers and the gradient-breathing rectangle in
/// <c>DashboardBackgroundControl.axaml</c> must stay gated on <c>ShellViewModel.IsWindowVisible</c>
/// (ANDed with the pre-existing reduced-motion gate, never replacing it), so a tray-hidden or
/// minimized window freezes the animation instead of ticking it for the life of the process.
/// Source-text, because this test project has no headless render.
/// </summary>
public class DashboardBackgroundGatingTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    private static XDocument Document() => XDocument.Load(ControlPath());

    private static readonly XName XNameAttribute = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");

    private static XElement RectangleNamed(string name) => Document()
        .Descendants(XName.Get("Rectangle", Avalonia))
        .Single(r => r.Attribute("Name")?.Value == name || r.Attribute(XNameAttribute)?.Value == name);

    [Theory]
    [InlineData("AuroraLayer1")]
    [InlineData("AuroraLayer2")]
    [InlineData("AuroraLayer3")]
    [InlineData("GradientAnimated")]
    public void EachAnimatedLayerCarriesTheWindowVisibleClassBinding(string name)
    {
        var rectangle = RectangleNamed(name);
        rectangle.Attribute("Classes.window-visible")?.Value
            .Should().Be("{Binding IsWindowVisible}",
                $"{name} must be gated on ShellViewModel.IsWindowVisible (perf audit P0-2)");
    }

    [Theory]
    [InlineData("AuroraLayer1")]
    [InlineData("AuroraLayer2")]
    [InlineData("AuroraLayer3")]
    [InlineData("GradientAnimated")]
    public void EachAnimatedLayersStyleSelectorRequiresWindowVisible(string name)
    {
        var rectangle = RectangleNamed(name);
        var selectors = rectangle
            .Descendants(XName.Get("Style", Avalonia))
            .Select(s => s.Attribute("Selector")?.Value ?? "")
            .ToArray();

        selectors.Should().NotBeEmpty($"{name} must declare an animation Style");
        selectors.Should().OnlyContain(selector => selector.Contains(".window-visible"),
            $"{name}'s animation selector must require .window-visible so a tray-hidden or " +
            "minimized window stops ticking it (perf audit P0-2)");
    }

    [Fact]
    public void AuroraLayersStillRequireReducedMotionAlongsideWindowVisible()
    {
        foreach (var name in new[] { "AuroraLayer1", "AuroraLayer2", "AuroraLayer3" })
        {
            var rectangle = RectangleNamed(name);
            rectangle.Attribute("Classes.aurora-animated")?.Value
                .Should().Be("{Binding !IsReducedMotion}",
                    $"{name} must keep the reduced-motion gate; window-visible is an AND on top of it, not a replacement");

            var selectors = rectangle
                .Descendants(XName.Get("Style", Avalonia))
                .Select(s => s.Attribute("Selector")?.Value ?? "");
            selectors.Should().OnlyContain(selector => selector.Contains(".aurora-animated"),
                $"{name}'s animation selector must still require .aurora-animated");
        }
    }

    private static string ControlPath() =>
        Path.Combine(RepoRoot(), "remex.desktop", "Controls", "DashboardBackgroundControl.axaml");

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
