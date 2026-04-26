using System;
using Avalonia.Controls;
using Avalonia.Input;
using Remex.Client.Models;
using Remex.Client.ViewModels;

namespace Remex.Client.Views;

public partial class CommandPaletteWindow : Window
{
    // Design-time / XAML loader constructor
    public CommandPaletteWindow()
    {
        InitializeComponent();
    }

    public CommandPaletteWindow(CommandPaletteViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.CloseRequested += () => Close();

        // Dismiss when the window loses focus (click outside)
        Deactivated += OnDeactivated;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Focus the search box immediately so the user can start typing
        if (this.FindControl<TextBox>("SearchBox") is { } tb)
        {
            tb.Focus();
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Return && DataContext is CommandPaletteViewModel vm)
        {
            // Execute the first/selected result on Enter
            if (this.FindControl<ListBox>("ResultsList") is { SelectedItem: CommandPaletteEntry entry })
            {
                vm.ExecuteEntryCommand.Execute(entry);
            }
            else if (vm.FilteredResults.Count > 0)
            {
                vm.ExecuteEntryCommand.Execute(vm.FilteredResults[0]);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Down && this.FindControl<ListBox>("ResultsList") is { } list)
        {
            // Move selection down
            var next = list.SelectedIndex + 1;
            if (next < list.ItemCount) list.SelectedIndex = next;
            e.Handled = true;
        }
        else if (e.Key == Key.Up && this.FindControl<ListBox>("ResultsList") is { } list2)
        {
            // Move selection up
            var prev = list2.SelectedIndex - 1;
            if (prev >= 0) list2.SelectedIndex = prev;
            e.Handled = true;
        }
    }
}
