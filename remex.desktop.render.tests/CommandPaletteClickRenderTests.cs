using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Remex.Desktop.ViewModels;
using Remex.Desktop.Views;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// Clicking a command palette row runs that row's command and closes the palette, the same path as
/// Enter (PC-4.1), and the first match is highlighted as soon as the window opens.
/// </summary>
public sealed class CommandPaletteClickRenderTests
{
    [AvaloniaFact]
    public async Task ClickingTheSecondRowRunsItAndClosesThePalette()
    {
        using var shell = await ShellRenderFixture.CreateAsync();
        var palette = new CommandPaletteViewModel(shell.ViewModel);
        var closed = 0;
        palette.CloseRequested += () => closed++;
        var window = new CommandPaletteWindow(palette);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var list = window.FindControl<ListBox>("ResultsList")!;
        list.SelectedItem.Should().BeSameAs(palette.FilteredResults[0], "the row Enter would run is highlighted on open");

        var second = list.GetVisualDescendants().OfType<ListBoxItem>().ElementAt(1);
        var point = second.TranslatePoint(new Point(20, 8), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        shell.ViewModel.ActiveNavIndex.Should().Be(5,
            "the second entry is Remote desktop since the palette follows the RemEx-172ba sidebar order, and the click must run it");
        closed.Should().BeGreaterThan(0, "running an entry closes the palette");
    }
}
