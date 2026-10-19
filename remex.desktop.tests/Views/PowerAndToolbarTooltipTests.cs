using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Plain-English tooltips on the Commands power tiles and the Sensors toolbar buttons.
/// </summary>
public class PowerAndToolbarTooltipTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    [Fact]
    public void EveryPowerTileCarriesARemoteTipTooltip()
    {
        var tiles = XDocument.Parse(Source("RemoteView.axaml")).Descendants(Avalonia + "Button")
            .Where(b => (b.Attribute("Classes")?.Value ?? "").Split(' ').Contains("tile")
                        && b.Attribute("AutomationProperties.Name") is not null)
            .ToList();

        tiles.Should().HaveCount(9, "six graceful and three forced power tiles");
        foreach (var tile in tiles)
        {
            var name = Regex.Match(tile.Attribute("AutomationProperties.Name")!.Value, @"Localize (Remote_\w+)\}").Groups[1].Value;
            (tile.Attribute("ToolTip.Tip")?.Value).Should().Be("{local:Localize " + name + "Tip}",
                $"the {name} tile explains what it does before a user taps it");
        }
    }

    [Fact]
    public void EveryCanvasToolbarButtonHasATooltip()
    {
        var xml = Source("CanvasView.axaml");
        var toolbar = XDocument.Parse(xml).Descendants(Avalonia + "Button")
            .Where(b => b.Attribute("Grid.Column") is not null && b.Parent?.Name.LocalName == "Grid"
                        && b.Parent.Descendants(Avalonia + "Button").Any(x => x.Attribute("Command")?.Value == "{Binding SaveLayoutCommand}")
                        && b.Parent.Elements(Avalonia + "Button").Contains(b))
            .ToList();

        toolbar.Should().NotBeEmpty();
        toolbar.Should().OnlyContain(b => b.Attribute("ToolTip.Tip") != null,
            "every button on the Sensors toolbar says what it does");

        xml.Should().Contain("Canvas_SaveTooltip").And.Contain("Canvas_SensorLibraryTooltip").And.Contain("Canvas_ResetViewTooltip");
    }

    private static string Source(string file, [CallerFilePath] string thisFile = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "remex.desktop", "Views", file));
}
