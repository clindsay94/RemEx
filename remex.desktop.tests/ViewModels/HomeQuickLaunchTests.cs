using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Home's pinned tiles match the Sensors cards (custom name, same number format, open Sensors), and
/// the Quick launch grid reaches every sidebar destination people actually jump to.
/// </summary>
public sealed class HomeQuickLaunchTests
{
    [Fact]
    public void PinnedTilesUseTheCustomNameAndTheSharedNumberFormat()
    {
        var xaml = HomeXaml();
        var template = xaml[xaml.IndexOf("x:DataType=\"vm:SensorViewModel\"", StringComparison.Ordinal)..];
        template = template[..template.IndexOf("</DataTemplate>", StringComparison.Ordinal)];

        template.Should().Contain("<TextBlock Text=\"{Binding DisplayName}\"");
        template.Should().NotContain("{0:F1}");
        template.Should().Contain("conv:SensorValueConverter.Instance");
    }

    [Fact]
    public void PinnedTilesOpenSensorsOnTapAndEnterNotOnSelection()
    {
        var xaml = HomeXaml();
        xaml.Should().Contain("Tapped=\"OnPinnedSensorTapped\" KeyDown=\"OnPinnedSensorKeyDown\"");
        xaml.Should().NotContain("SelectionChanged=");
    }

    [Theory]
    [InlineData("NavigateToCanvasCommand", "ChartLineVariant")]
    [InlineData("NavigateToRemoteCommand", "RemoteTv")]
    [InlineData("NavigateToRoutinesCommand", "Routes")]
    [InlineData("NavigateToAppLauncherCommand", "Apps")]
    [InlineData("NavigateToTaskManagerCommand", "MonitorDashboard")]
    [InlineData("NavigateToFileTransferCommand", "FolderOutline")]
    [InlineData("NavigateToDiagnosticLogsCommand", "FileDocument")]
    [InlineData("NavigateToSettingsCommand", "Tune")]
    [InlineData("NavigateToCustomizationCommand", "Palette")]
    [InlineData("NavigateToAboutCommand", "InformationOutline")]
    public void EachQuickLaunchTileUsesTheSidebarIcon(string command, string icon)
    {
        var home = HomeXaml();
        var grid = home[home.IndexOf("<UniformGrid Columns=\"2\">", StringComparison.Ordinal)..];
        var tile = Regex.Match(grid, @"Command=""\{Binding " + command + @"\}""[\s\S]*?<mi:MaterialIcon Kind=""(\w+)""");
        tile.Success.Should().BeTrue($"the quick-launch grid has a tile for {command}");
        tile.Groups[1].Value.Should().Be(icon);

        File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"))
            .Should().Contain($"Kind=\"{icon}\"", "the sidebar uses the same icon for this destination");
    }

    [Fact]
    public void TheQuickLaunchGridHasTenTiles()
    {
        var xaml = HomeXaml();
        var grid = xaml[xaml.IndexOf("<UniformGrid Columns=\"2\">", StringComparison.Ordinal)..];
        grid = grid[..grid.IndexOf("</UniformGrid>", StringComparison.Ordinal)];
        Regex.Matches(grid, "<Button ").Count.Should().Be(10);
    }

    [Fact]
    public async Task NavigateToRoutinesCommandLandsOnTheRoutinesPage()
    {
        var dir = Directory.CreateTempSubdirectory("remex-homeql-").FullName;
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = new DashboardLayoutService(Path.Combine(dir, "dashboard_layout.json"), theme);
        await layout.LoadAsync();
        var connection = new ConnectionViewModel();
        var shell = new ShellViewModel(
            layout, theme, connection,
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .BuildServiceProvider(),
            transferQueuePost: action => action());
        shell.ProfileReplacedDispatch = run => run();
        shell.DiagnosticsLogDispatch = run => run();
        try
        {
            var home = new HomeViewModel(connection, shell);

            home.NavigateToRoutinesCommand.Execute(null);

            shell.ActiveNavIndex.Should().Be(10);
        }
        finally
        {
            shell.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static string HomeXaml() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "HomeView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
