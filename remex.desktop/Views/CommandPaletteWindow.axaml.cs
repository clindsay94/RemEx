using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Remex.Desktop.Models;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class CommandPaletteWindow : Window
{
    /// <summary>
    /// Set once anything has begun closing this window, so the POSTED deactivation dismiss
    /// (<see cref="OnDeactivated"/>, RemEx-27a0s) cannot run against a window that closed while its
    /// callback sat on the dispatcher queue. The two orderings that get here: executing an entry
    /// raises <c>CloseRequested</c> and also deactivates the window, and a destructive entry's
    /// confirmation dialog takes activation before the palette is gone.
    /// </summary>
    private bool _closing;

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

        // Dismiss when the window loses focus (click outside, or another window — e.g. a
        // destructive-entry confirmation dialog, see CommandPaletteViewModel.ExecuteEntryAsync —
        // taking activation). Routed through DismissCommand rather than Close() directly so this
        // stays on the same path as the Esc keybinding; whatever Dismiss() grows in the future
        // (clearing query state, unsubscribing) then covers both ways out instead of only one.
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
        // POSTED, never synchronous (RemEx-27a0s). Deactivated is raised from inside
        // WM_ACTIVATE(WA_INACTIVE) — Windows is part-way through handing activation to whatever the
        // user clicked. Destroying the foreground window during that transfer aborts it, and
        // Windows falls back to the next top-level window in the GLOBAL z-order instead of the
        // intended target. Topmost="True" makes that fallback worse, not better: the owner is not
        // the natural successor to a destroyed topmost window.
        //
        // Measured before the fix: click on empty RemEx background with the palette open ->
        // WindowFromPoint says the point belongs to RemEx's main window, the palette closes, and
        // GetForegroundWindow comes back as WindowsTerminal. The click did not fall through in the
        // hit-testing sense; the activation did.
        //
        // Posting lets Windows finish the transfer first, then closes the palette on the next
        // dispatcher pass. Background priority, not Normal: it has to run after the input and
        // layout work the activation itself queues.
        if (_closing)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_closing)
                return;

            if (DataContext is CommandPaletteViewModel vm)
                vm.DismissCommand.Execute(null);
            else
                Close();
        }, DispatcherPriority.Background);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
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
