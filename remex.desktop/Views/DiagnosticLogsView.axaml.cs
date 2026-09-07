using System.Linq;
using Avalonia.Controls;
// Avalonia 12 moved SetTextAsync off IClipboard onto ClipboardExtensions in this namespace
// (RemEx-jcma3). The interface now speaks IDataTransfer; the text convenience is an extension.
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class DiagnosticLogsView : UserControl
{
    // Rewired on every ConfigureViewModel call so a navigation that swaps DataContext does not
    // leave the old ViewModel's ScrollToEndRequested subscribed forever.
    private DiagnosticLogsViewModel? _wiredVm;

    public DiagnosticLogsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ConfigureViewModel();
    }

    private void ConfigureViewModel()
    {
        if (DataContext is not DiagnosticLogsViewModel vm)
            return;

        if (_wiredVm is not null)
            _wiredVm.ScrollToEndRequested -= OnScrollToEndRequested;
        _wiredVm = vm;
        vm.ScrollToEndRequested += OnScrollToEndRequested;

        // Guards Clear Logs (RemEx-6p1f).
        vm.OnConfirmationRequested = ConfirmationDialogHost.For(this);

        // The clipboard lives on the view, so the view model asks for it rather than reaching for it
        // (RemEx-7xhln) — the same seam RemoteViewModel uses, and the reason FormatForClipboard can
        // be tested without a running Avalonia.
        vm.CopyToClipboardAsync = async text =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(text);
        };

        // Provide the save-file picker so log export can prompt the user to name the file.
        vm.PickSaveFileAsync = async options =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
                return null;
            return await topLevel.StorageProvider.SaveFilePickerAsync(options);
        };

        // Land on the freshest entry rather than wherever the ListBox happens to render initially —
        // follow tail starts ON, so the first paint should already agree with it.
        if (vm.IsFollowingTail)
            Dispatcher.UIThread.Post(ScrollLogListToEnd);
    }

    private void OnScrollToEndRequested() => Dispatcher.UIThread.Post(ScrollLogListToEnd);

    private void ScrollLogListToEnd()
    {
        if (DataContext is not DiagnosticLogsViewModel vm)
            return;

        var last = vm.VisibleEntries.LastOrDefault();
        if (last is not null)
            LogListBox.ScrollIntoView(last);
    }

    /// <summary>
    /// ScrollViewer.ScrollChanged bubbles up from the ListBox's own internal ScrollViewer, so this
    /// fires without wrapping the ListBox in a second one — RemEx-3oy7x needs it hosted directly in
    /// the Card to keep virtualizing against a real viewport. "At end" tolerates a fraction of a
    /// pixel of rounding rather than requiring an exact match.
    /// </summary>
    private void OnLogListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not DiagnosticLogsViewModel vm || sender is not ScrollViewer scrollViewer)
            return;

        var atEnd = scrollViewer.Offset.Y >= scrollViewer.Extent.Height - scrollViewer.Viewport.Height - 1.0;
        vm.OnLogListScrolled(atEnd);
    }
}
