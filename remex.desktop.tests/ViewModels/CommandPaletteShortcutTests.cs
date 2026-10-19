using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The command palette's highlighted row, its shortcut hints, and the window shortcuts that act on
/// the Sensors layout (PC-4.1, 4.3, 4.4).
/// </summary>
public sealed class CommandPaletteShortcutTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly ThemeService _theme;
    private readonly DashboardLayoutService _layout;
    private ShellViewModel _shell = null!;

    public CommandPaletteShortcutTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-palette-shortcuts-").FullName;
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layout = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
    }

    public async Task InitializeAsync()
    {
        await _layout.LoadAsync();
        _shell = new ShellViewModel(
            _layout,
            _theme,
            new ConnectionViewModel(),
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .BuildServiceProvider());
        _shell.ProfileReplacedDispatch = run => run();
    }

    public Task DisposeAsync()
    {
        _shell.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    // ── PC-4.1: the row Enter runs is the highlighted one ──

    [Fact]
    public void SelectedEntry_FollowsTheFirstMatch_WhenTheQueryChanges()
    {
        var palette = new CommandPaletteViewModel(_shell);
        palette.SelectedEntry.Should().BeSameAs(palette.FilteredResults[0]);

        palette.SearchText = "sleep";

        palette.FilteredResults.Should().NotBeEmpty();
        palette.SelectedEntry.Should().BeSameAs(palette.FilteredResults[0]);
    }

    [Fact]
    public void SelectedEntry_IsNull_WhenNothingMatches()
    {
        var palette = new CommandPaletteViewModel(_shell);

        palette.SearchText = "zzzzqqqq-no-such-command";

        palette.IsEmpty.Should().BeTrue();
        palette.SelectedEntry.Should().BeNull();
    }

    // ── PC-4.3: undo/redo/save only act while Sensors is on screen ──

    [Fact]
    public void CanvasShortcuts_AreDisabledOffSensors_AndEnabledOnIt()
    {
        _shell.ActiveNavIndex.Should().Be(0);
        _shell.CanvasUndoShortcutCommand.CanExecute(null).Should().BeFalse();
        _shell.CanvasRedoShortcutCommand.CanExecute(null).Should().BeFalse();
        _shell.CanvasSaveShortcutCommand.CanExecute(null).Should().BeFalse();

        _shell.NavigateToCanvas();

        _shell.ActiveNavIndex.Should().Be(1);
        _shell.CanvasUndoShortcutCommand.CanExecute(null).Should().BeTrue();
        _shell.CanvasRedoShortcutCommand.CanExecute(null).Should().BeTrue();
        _shell.CanvasSaveShortcutCommand.CanExecute(null).Should().BeTrue();

        _shell.ActiveNavIndex = 0;

        _shell.CanvasUndoShortcutCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void PaletteUndoAndRedo_BringSensorsForwardFirst()
    {
        var palette = new CommandPaletteViewModel(_shell);
        _shell.ActiveNavIndex.Should().Be(0);

        palette.FilteredResults.Single(e => e.Shortcut == "Ctrl+Z").Command.Execute(null);
        _shell.ActiveNavIndex.Should().Be(1);

        _shell.ActiveNavIndex = 0;
        palette.FilteredResults.Single(e => e.Shortcut == "Ctrl+Y").Command.Execute(null);
        _shell.ActiveNavIndex.Should().Be(1);
    }

    // ── PC-4.4: every hint on a row is a real window gesture for the same command ──

    [Fact]
    public void EveryPaletteShortcutHint_IsARealMainWindowGestureForTheSameCommand()
    {
        var palette = new CommandPaletteViewModel(_shell);
        var withHint = palette.FilteredResults.Where(e => e.Shortcut is not null).ToList();
        withHint.Should().NotBeEmpty("an empty set would make this guard vacuous");

        var gestures = MainWindowGestures();
        var commandsByName = typeof(ShellViewModel).GetProperties()
            .Where(p => typeof(System.Windows.Input.ICommand).IsAssignableFrom(p.PropertyType))
            .ToDictionary(p => p.Name, p => (System.Windows.Input.ICommand)p.GetValue(_shell)!);

        foreach (var entry in withHint)
        {
            gestures.Should().ContainKey(entry.Shortcut!, $"'{entry.Label}' advertises {entry.Shortcut}");
            var bound = commandsByName[gestures[entry.Shortcut!]];

            var expected = entry.Shortcut switch
            {
                "Ctrl+Z" => _shell.CanvasUndoShortcutCommand,
                "Ctrl+Y" => _shell.CanvasRedoShortcutCommand,
                _ => entry.Command,
            };
            bound.Should().BeSameAs(expected, $"'{entry.Label}' and its {entry.Shortcut} gesture must run the same thing");
        }
    }

    [Fact]
    public void NavigationEntries_AdvertiseCtrl1ThroughCtrl9InSidebarOrder()
    {
        var palette = new CommandPaletteViewModel(_shell);
        var nav = palette.FilteredResults
            .Where(e => e.Shortcut is { } s && s.StartsWith("Ctrl+") && char.IsDigit(s[5]))
            .Select(e => e.Shortcut)
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        nav.Should().Equal("Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5", "Ctrl+6", "Ctrl+7", "Ctrl+8", "Ctrl+9");
    }

    // MainWindow gesture ("Ctrl+D2", "Ctrl+OemComma") in palette spelling ("Ctrl+2", "Ctrl+,") -> bound command name.
    private static System.Collections.Generic.Dictionary<string, string> MainWindowGestures([CallerFilePath] string f = "")
    {
        XNamespace ns = "https://github.com/avaloniaui";
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(f)!, "..", ".."));
        var doc = XDocument.Load(Path.Combine(root, "remex.desktop", "MainWindow.axaml"));
        return doc.Descendants(ns + "KeyBinding")
            .GroupBy(b => b.Attribute("Gesture")!.Value)
            .Select(g => g.First())
            .ToDictionary(
            b => b.Attribute("Gesture")!.Value
                .Replace("Ctrl+OemComma", "Ctrl+,")
                .Replace("Ctrl+D", "Ctrl+"),
            b => System.Text.RegularExpressions.Regex.Match(b.Attribute("Command")!.Value, @"\{Binding\s+(\w+)\}").Groups[1].Value);
    }
}
