using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// RemEx-172ba's Settings order, and where the controls that left Personalize landed. Source-text:
/// this assembly has no headless render.
/// </summary>
public class SettingsSectionOrderTests
{
    [Fact]
    public void TheSectionsAppearInTaskOrder()
    {
        var markup = Read("Views", "SettingsView.axaml");
        string[] headings =
        [
            "Localize Settings_ConnectionPairing}",
            "Localize Settings_PairedDevices}",
            "Localize Settings_General}",
            "<views:TrayFlyoutSettingsView",
            "Localize Settings_FilesSharing}",
            "Localize Settings_Alerts_Title}",
            "Localize Settings_BackupSection}",
            "Localize Settings_Help}",
        ];

        var positions = headings.Select(h => markup.IndexOf(h, StringComparison.Ordinal)).ToArray();

        positions.Should().OnlyContain(p => p >= 0, "every section must be on the page");
        positions.Should().BeInAscendingOrder(
            "Connection & pairing, Paired devices, General, Tray pop-up, Files & sharing, Sensor alerts, Backup & restore, Help");
    }

    [Fact]
    public void FilesAndSharing_HoldsBothListsUnderOneHeading_AndPairedDevicesStaysItsOwnCard()
    {
        var markup = Read("Views", "SettingsView.axaml");
        var card = CardContaining(markup, "Localize Settings_FilesSharing}");

        card.Should().Contain("Localize Settings_FileTransferSection}").And.Contain("Localize Settings_TrustSection}");
        card.Should().Contain(@"ItemsSource=""{Binding SharedRoots}""").And.Contain(@"ItemsSource=""{Binding TrustedDevices}""");
        card.Should().NotContain("PairedDevices", "the pairing list must never share a card with the file grants");
    }

    [Fact]
    public void LookAndFeel_StaysInGeneral()
    {
        CardContaining(Read("Views", "SettingsView.axaml"), "Localize Settings_General}")
            .Should().Contain(@"Command=""{Binding OpenPersonalizeCommand}""");
    }

    [Fact]
    public void TheSensorsToolbar_CarriesSnapToGridAndGridSize_NextToSave()
    {
        var canvas = Read("Views", "CanvasView.axaml");
        var save = canvas.IndexOf(@"Command=""{Binding SaveLayoutCommand}""", StringComparison.Ordinal);
        var grid = canvas.IndexOf(@"x:Name=""GridSettingsButton""", StringComparison.Ordinal);

        save.Should().BeGreaterThan(0);
        grid.Should().BeGreaterThan(save, "the grid controls sit with the edit controls");
        var flyout = canvas.Substring(grid, canvas.IndexOf("</Button.Flyout>", grid, StringComparison.Ordinal) - grid);
        flyout.Should().Contain(@"IsChecked=""{Binding IsSnapToGridEnabled}""");
        flyout.Should().Contain(@"Value=""{Binding GridSize}""");
        Regex.Match(flyout, @"<TextBlock[^>]*Text=""\{local:Localize Settings_SnapToGridDesc\}""[^>]*/>").Value
            .Should().Contain(@"TextWrapping=""Wrap""", "the description must wrap beside the switch (RemEx-4kv0g.4.4)");
    }

    [Fact]
    public void TheToolbarColumns_StillLineUp()
    {
        var canvas = Read("Views", "CanvasView.axaml");
        var start = canvas.IndexOf("<!-- ═══ Toolbar ═══ -->", StringComparison.Ordinal);
        var toolbar = canvas.Substring(start, canvas.IndexOf("<!-- ═══ Main content ═══ -->", StringComparison.Ordinal) - start);
        var columns = Regex.Match(toolbar, @"<Grid ColumnDefinitions=""([^""]+)"">").Groups[1].Value.Split(',').Length;

        // Direct toolbar children only: the flyout's own Grids number their columns from 0 again.
        var used = Regex.Matches(toolbar, @"^ {16}<\w[^>]*Grid\.Column=""(\d+)""", RegexOptions.Multiline)
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();

        used.Should().OnlyHaveUniqueItems("two toolbar buttons in one column draw on top of each other");
        used.Max().Should().BeLessThan(columns);
    }

    private static string CardContaining(string markup, string token)
    {
        var at = markup.IndexOf(token, StringComparison.Ordinal);
        at.Should().BeGreaterThan(0, token);
        var open = markup.LastIndexOf("<material:Card", at, StringComparison.Ordinal);
        var close = markup.IndexOf("</material:Card>", at, StringComparison.Ordinal);
        return markup.Substring(open, close - open);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), "remex.desktop", .. parts]));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
