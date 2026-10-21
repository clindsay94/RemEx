using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Xml.Linq;
using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Pins MainWindow's 18 <c>KeyBinding</c> gestures and their bound commands (RemEx-l2yqy), and
/// guards the two things the window-surface fix in this bead depends on: no <c>RenderTransform</c>
/// snuck onto the root (it would desync the shadow from the LayoutTransform-scaled content), and
/// <c>TransparencyBackgroundFallback</c> is a <c>DynamicResource</c> that follows the active
/// palette rather than a hardcoded literal that breaks on a light seed.
/// </summary>
public class MainWindowKeyBindingTests
{
    private static readonly string[] ExpectedGestures =
    {
        "Ctrl+D1", "Ctrl+D2", "Ctrl+D3", "Ctrl+D4", "Ctrl+D5", "Ctrl+D6", "Ctrl+D7", "Ctrl+D8", "Ctrl+D9",
        "Ctrl+OemComma", "Ctrl+Shift+OemComma", "Escape", "F5", "Ctrl+K", "Ctrl+Shift+P", "Ctrl+Z", "Ctrl+Y", "Ctrl+S",
    };

    [Fact]
    public void MainWindow_HasExactlyTheEighteenExpectedKeyBindings()
    {
        var doc = XDocument.Parse(MainWindowMarkup());
        XNamespace ns = "https://github.com/avaloniaui";

        var bindings = doc.Descendants(ns + "KeyBinding").ToList();

        bindings.Should().HaveCount(18, "MainWindow's 18 documented gestures are load-bearing");

        var gestures = bindings.Select(b => b.Attribute("Gesture")?.Value).ToList();
        gestures.Should().BeEquivalentTo(ExpectedGestures,
            "the gesture set must match exactly - a silently dropped or renamed gesture is a keyboard regression nobody notices");
    }

    [Fact]
    public void MainWindow_EveryKeyBindingCommand_ExistsOnShellViewModelAsAnICommand()
    {
        var doc = XDocument.Parse(MainWindowMarkup());
        XNamespace ns = "https://github.com/avaloniaui";

        var bindings = doc.Descendants(ns + "KeyBinding").ToList();
        var vmType = typeof(ShellViewModel);

        foreach (var binding in bindings)
        {
            var commandAttr = binding.Attribute("Command")?.Value ?? "";
            var match = System.Text.RegularExpressions.Regex.Match(commandAttr, @"^\{Binding\s+([A-Za-z0-9_]+)\}$");
            match.Success.Should().BeTrue(
                $"KeyBinding Command='{commandAttr}' should be a simple {{Binding X}} expression");

            var propertyName = match.Groups[1].Value;
            var property = vmType.GetProperty(propertyName);

            property.Should().NotBeNull(
                $"ShellViewModel should expose an '{propertyName}' property bound by a KeyBinding");
            typeof(ICommand).IsAssignableFrom(property!.PropertyType).Should().BeTrue(
                $"'{propertyName}' is bound as a KeyBinding Command, so it must be an ICommand");
        }
    }

    /// <summary>
    /// Ctrl+1..8 follow the sidebar top to bottom (3.0 comb, ctrl-digit-order; re-laid for the
    /// task-grouped drawer in RemEx-172ba). The expected order is derived from
    /// <see cref="ShellViewModel.DrawerNavOrder"/>, which is itself pinned to the drawer XAML, so a
    /// new sidebar item fails here until the shortcuts are re-laid.
    /// </summary>
    [Fact]
    public void MainWindow_CtrlDigitShortcuts_FollowTheSidebarOrder()
    {
        var doc = XDocument.Parse(MainWindowMarkup());
        XNamespace ns = "https://github.com/avaloniaui";
        var byGesture = doc.Descendants(ns + "KeyBinding")
            .ToDictionary(b => b.Attribute("Gesture")!.Value, b => b.Attribute("Command")!.Value);

        // Tag -> command, for the first eight drawer items (Home..Routines; Settings is Ctrl+9).
        var commandForTag = new Dictionary<int, string>
        {
            [0] = "NavigateToHomeCommand", [5] = "NavigateToRemoteDesktopCommand",
            [7] = "NavigateToFileTransferCommand", [2] = "NavigateToRemoteCommand",
            [3] = "NavigateToAppLauncherCommand", [4] = "NavigateToTaskManagerCommand",
            [1] = "NavigateToCanvasCommand", [10] = "NavigateToRoutinesCommand",
        };
        var firstEight = ShellViewModel.DrawerNavOrder.Take(8).ToList();
        firstEight.Should().HaveCount(8);

        for (var i = 0; i < 8; i++)
        {
            byGesture.Should().ContainKey($"Ctrl+D{i + 1}");
            byGesture[$"Ctrl+D{i + 1}"].Should().Be("{Binding " + commandForTag[firstEight[i]] + "}",
                $"Ctrl+{i + 1} must open sidebar item {i + 1}");
        }
    }

    /// <summary>Ctrl+9 is Settings, the ninth drawer item (PC-4.2), and Ctrl+Z/Y/S act only while Sensors shows (PC-4.3).</summary>
    [Fact]
    public void MainWindow_Ctrl9OpensSettings_AndLayoutShortcutsUseTheSensorsOnlyCommands()
    {
        var doc = XDocument.Parse(MainWindowMarkup());
        XNamespace ns = "https://github.com/avaloniaui";
        var byGesture = doc.Descendants(ns + "KeyBinding")
            .ToDictionary(b => b.Attribute("Gesture")!.Value, b => b.Attribute("Command")!.Value);

        ShellViewModel.DrawerNavOrder[8].Should().Be(9, "Settings is the ninth sidebar item");
        byGesture["Ctrl+D9"].Should().Be("{Binding NavigateToSettingsCommand}");
        byGesture["Ctrl+OemComma"].Should().Be("{Binding NavigateToSettingsCommand}",
            "Ctrl+, opens Settings (RemEx-172ba)");
        byGesture["Ctrl+Shift+OemComma"].Should().Be("{Binding ToggleSettingsPanelCommand}",
            "Personalize moved to Ctrl+Shift+, when Ctrl+, became Settings");
        byGesture["Ctrl+Z"].Should().Be("{Binding CanvasUndoShortcutCommand}");
        byGesture["Ctrl+Y"].Should().Be("{Binding CanvasRedoShortcutCommand}");
        byGesture["Ctrl+S"].Should().Be("{Binding CanvasSaveShortcutCommand}");
    }

    [Fact]
    public void MainWindow_HasNoRenderTransform()
    {
        // The LayoutTransformControl re-lays out vector content (including Material's BoxShadow)
        // under UiScale; a RenderTransform on the root would scale the rasterized window instead
        // and desync from that, per the bead's "shadows unaffected by UiScale" acceptance note.
        MainWindowMarkup().Should().NotMatchRegex(@"<(?:Window\.)?RenderTransform\b|\bRenderTransform\s*=",
            "MainWindow scales content only through the LayoutTransformControl, never a RenderTransform");
    }

    [Fact]
    public void MainWindow_TransparencyBackgroundFallback_IsAPaletteResourceNotALiteral()
    {
        var xaml = MainWindowMarkup();

        xaml.Should().Contain(@"TransparencyBackgroundFallback=""{DynamicResource GlassBaseDarkBrush}""",
            "the fallback must follow ThemeService's palette.Surface override, not a fixed colour that breaks on a light seed");
        xaml.Should().NotMatchRegex(@"TransparencyBackgroundFallback=""#[0-9A-Fa-f]+""",
            "a hardcoded colour literal here is exactly the near-black-on-light-palette bug this bead fixes");
    }

    private static string MainWindowMarkup([CallerFilePath] string f = "")
        => File.ReadAllText(Path.Combine(RepoRoot(f), "remex.desktop", "MainWindow.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
