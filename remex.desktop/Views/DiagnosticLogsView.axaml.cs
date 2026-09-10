using System.Linq;
using Avalonia.Controls;
// Avalonia 12 moved SetTextAsync off IClipboard onto ClipboardExtensions in this namespace
// (RemEx-jcma3). The interface now speaks IDataTransfer; the text convenience is an extension.
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    /// <summary>
    /// Scrolls straight to the end via the ListBox's own internal ScrollViewer rather than
    /// <c>ScrollIntoView</c> on the last item. <c>ScrollIntoView</c> lands wherever that item's
    /// realized bounds put it, which can miss the true maximum offset by a few pixels (item padding,
    /// sub-pixel layout rounding) — enough that the very next <see cref="OnLogListScrollChanged"/>
    /// computed "not quite at end" and immediately paused following again, right after this method
    /// had just resumed it. <c>ScrollToEnd()</c> sets the offset to the actual maximum, which is
    /// exactly what the "at end" check compares against.
    /// </summary>
    private void ScrollLogListToEnd()
    {
        if (DataContext is not DiagnosticLogsViewModel)
            return;

        var scrollViewer = LogListBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer is not null)
        {
            scrollViewer.ScrollToEnd();
            return;
        }

        // Fallback for the (untested-in-practice) case where the template has not realized a
        // ScrollViewer yet, e.g. before the first layout pass.
        if (DataContext is DiagnosticLogsViewModel vm)
        {
            var last = vm.VisibleEntries.LastOrDefault();
            if (last is not null)
                LogListBox.ScrollIntoView(last);
        }
    }

    /// <summary>
    /// ScrollViewer.ScrollChanged is an attached routed event handled on the ListBox, so it bubbles
    /// up from the ListBox's own internal ScrollViewer without a second one wrapping it — RemEx-3oy7x
    /// needs it hosted directly in the Card to keep virtualizing against a real viewport. Because the
    /// handler is attached to the ListBox, Avalonia sets <c>sender</c> to the ListBox itself; the
    /// ScrollViewer that actually raised it is <c>e.Source</c>. "At end"
    /// tolerates a fraction of a pixel of rounding rather than requiring an exact match.
    /// </summary>
    private void OnLogListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not DiagnosticLogsViewModel vm || e.Source is not ScrollViewer scrollViewer)
            return;

        // A live arrival grows the extent without moving the offset — from the ScrollViewer's own
        // perspective that reads as "scrolled away from the end" on the very first entry after the
        // user was sitting at the bottom, which would wrongly pause following before the programmatic
        // scroll-to-end even runs. Evaluate "at end" against the extent as it was BEFORE this
        // notification's growth (Extent - ExtentDelta), not after, so a pure content-growth
        // notification with an unchanged offset still reads as "at end" when it was.
        //
        // ONLY FOR GROWTH, THOUGH (clamped at 0): the virtualizing panel also SHRINKS the extent as
        // it replaces estimated item heights with real ones while settling after a big jump — a
        // negative ExtentDelta. Subtracting a negative delta there would WIDEN the threshold and
        // read a jump that landed exactly at the end as "not quite there", immediately re-pausing
        // following right after ScrollLogListToEnd had just resumed it. A shrink is not evidence the
        // user was further from the end before it, so it contributes nothing to the pre-growth figure.
        var extentGrowth = Math.Max(0, e.ExtentDelta.Y);
        var extentBeforeGrowth = scrollViewer.Extent.Height - extentGrowth;
        var atEnd = scrollViewer.Offset.Y >= extentBeforeGrowth - scrollViewer.Viewport.Height - 4.0;
        vm.OnLogListScrolled(atEnd);
    }
}
