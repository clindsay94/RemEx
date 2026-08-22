using Avalonia.Controls;
// Avalonia 12 moved SetTextAsync off IClipboard onto ClipboardExtensions in this namespace
// (RemEx-jcma3). The interface now speaks IDataTransfer; the text convenience is an extension.
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

public partial class DiagnosticLogsView : UserControl
{
    public DiagnosticLogsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ConfigureViewModel();
    }

    private void ConfigureViewModel()
    {
        if (DataContext is not DiagnosticLogsViewModel vm)
            return;

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
    }
}
