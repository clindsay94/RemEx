using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Material.Dialog;
using Remex.Core.Services.FileTransfer;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Views;

/// <summary>
/// One path for the three modal prompts that used to be hand-built <see cref="Window"/> subclasses
/// (<c>ConfirmationDialog</c>, <c>FileConsentDialog</c>, <c>RestorePromptWindow</c>), rebuilt on
/// Material.Avalonia.Dialogs' <see cref="DialogHelper"/> window builders (RemEx-x6a70.3).
/// </summary>
/// <remarks>
/// <para>
/// STILL WINDOWS, DELIBERATELY. Material.Avalonia.Dialogs 3.19.0 has no in-window
/// <c>DialogHost</c> — only builders (<see cref="DialogHelper.CreateAlertDialog"/>,
/// <see cref="DialogHelper.CreateCustomDialog"/>) that construct a separate <see cref="Window"/> per
/// dialog, same as before. What collapses is the duplication: three near-identical hand-rolled
/// Windows become one helper that builds through the library's own machinery instead.
/// </para>
/// <para>
/// FAIL-CLOSED BY CONSTRUCTION. <see cref="DialogHelper"/> sets a dialog's negative result on the
/// underlying view model at BUILD time (<c>DialogHelper.SetupWindowParameters</c> calls
/// <c>IHasNegativeResult.SetNegativeResult(@params.NegativeResult)</c> before the window is ever
/// shown). A button click is the only thing that overwrites it. So Alt+F4, the title-bar close
/// button, and any other way of dismissing the window without clicking a button already resolve to
/// the negative result with zero code here. The only gap is Escape: a vanilla Avalonia
/// <see cref="Window"/> does not bind it to Close, so <see cref="AttachEscapeDismiss"/> adds exactly
/// that — a plain <c>window.Close()</c> — from this helper, not by patching the library.
/// </para>
/// <para>
/// FIX ROUND 1: the first pass left two regressions against the RemEx-2m7fr dialogs this replaced.
/// <see cref="ConfirmAsync"/> and <see cref="RestoreAsync"/> now build through
/// <see cref="DialogHelper.CreateCustomDialog"/> with <see cref="DialogContent"/> instead of
/// <see cref="DialogHelper.CreateAlertDialog"/>'s <c>ContentHeader</c>/<c>SupportingText</c>, because
/// <c>AlertDialog</c>'s <c>SupportingText</c> TextBlock has no <c>TextWrapping</c> set and clips a
/// long message instead of wrapping it - moving to a custom content control sidesteps that TextBlock
/// entirely rather than trying to patch it. The second regression - the confirm/deny/restore actions
/// rendering as indistinguishable flat text instead of this app's primary/secondary/danger button
/// vocabulary - was investigated but not fixed in that round.
/// </para>
/// <para>
/// FIX ROUND 2 (RemEx-x6a70.3): that investigation (decompiling Material.Avalonia.Dialogs 3.19.0,
/// net8.0, ilspycmd) found <c>DialogWindowBase&lt;TWindow,TResult&gt;.Procedure</c> resolves
/// <c>ShowDialog</c>'s result from <c>_window.GetResult()</c> on the window's <c>Closed</c> event,
/// which reads <c>(DataContext as ...ViewModel)?.DialogResult</c> - a property with an <c>internal</c>
/// setter, only ever written by the library's own <c>ObsoleteDialogButtonViewModel</c> command
/// handler. <c>DialogButton</c> also carries no <c>Classes</c> property - only
/// <c>IsPositive</c>/<c>IsNegative</c> - so there is no builder-level way to hand a library-rendered
/// button this app's Classes vocabulary either. The route out is not to hand the library any buttons
/// at all: every builder below passes <c>DialogButtons = Array.Empty&lt;DialogButton&gt;()</c>, and the
/// real RemEx <c>Button</c>s wearing <c>primary</c>/<c>secondary</c>/<c>danger</c> live inside the
/// content control instead (<see cref="DialogContent"/>, <see cref="FileConsentContent"/>). Neither
/// <see cref="ConfirmAsync"/> nor <see cref="RestoreAsync"/> reads <c>ShowDialog</c>'s return value for
/// the outcome any more - it would still resolve through the unreachable library <c>DialogResult</c>
/// property above - they resolve through <see cref="DialogContent.ResultTask"/> instead, via
/// <see cref="Resolve"/>. <see cref="FileConsentAsync"/> already resolved through
/// <see cref="FileConsentDialogViewModel.ResultTask"/>, so that path needed no equivalent change -
/// only its Deny/Allow buttons moved into <see cref="FileConsentContent"/>.
/// </para>
/// </remarks>
internal static class MaterialDialogs
{
    private const string ConfirmResult = DialogHelper.DIALOG_RESULT_OK;
    private const string CancelResult = DialogHelper.DIALOG_RESULT_CANCEL;
    private const string ConsentAllowResult = "allow";
    private const string ConsentDenyResult = "deny";
    private const string RestoreResult = DialogHelper.DIALOG_RESULT_OK;
    private const string SkipResult = DialogHelper.DIALOG_RESULT_CANCEL;

    /// <summary>Builds and shows the confirm/cancel alert that replaced <c>ConfirmationDialog</c>.</summary>
    internal static async Task<bool> ConfirmAsync(Window owner, string title, string message, string confirmText)
    {
        var loc = LocalizationService.Instance;
        var content = new DialogContent(title, message, loc["Btn_Cancel"], confirmText, "primary danger");

        var dialog = DialogHelper.CreateCustomDialog(new CustomDialogBuilderParams
        {
            WindowTitle = loc["Dialog_ConfirmTitle"],
            Content = content,
            Width = 440,
            StartupLocation = WindowStartupLocation.CenterOwner,
            NegativeResult = new DialogResult(CancelResult),
            DialogButtons = Array.Empty<DialogButton>(),
        });

        AttachEscapeDismiss(dialog.GetWindow());
        await dialog.ShowDialog(owner);
        return Resolve(content.ResultTask);
    }

    /// <summary>
    /// Builds and shows the file-sharing consent prompt that replaced <c>FileConsentDialog</c>.
    /// Content is <see cref="FileConsentContent"/>, a plain UserControl bound to
    /// <paramref name="vm"/> so its message, detail, "remember" checkbox and Deny/Allow buttons keep
    /// their exact bindings from before (RemEx-x6a70.3 fix round 2 moved Deny/Allow into the content
    /// itself, wearing this app's secondary/primary vocabulary, still bound straight to
    /// <see cref="FileConsentDialogViewModel.AllowCommand"/> / <see cref="FileConsentDialogViewModel.DenyCommand"/> -
    /// the decision logic itself never moved). This helper does not build a decision from a result
    /// string any more; it only watches <see cref="FileConsentDialogViewModel.ResultTask"/> and closes
    /// the window once that resolves, the same pattern <c>PairingDialog.axaml.cs</c> uses.
    /// </summary>
    /// <param name="owner">
    /// The dialog's parent window, or <c>null</c> to show it unparented — mirrors the
    /// non-desktop-lifetime branch <c>App.axaml.cs</c> already had, where no <see cref="Window"/>
    /// exists to own it.
    /// </param>
    internal static async Task<FileConsentDecision> FileConsentAsync(Window? owner, FileConsentDialogViewModel vm)
    {
        var loc = LocalizationService.Instance;

        var dialog = DialogHelper.CreateCustomDialog(new CustomDialogBuilderParams
        {
            WindowTitle = loc["FileConsent_WindowTitle"],
            Content = new FileConsentContent { DataContext = vm },
            Width = 440,
            StartupLocation = WindowStartupLocation.CenterOwner,
            NegativeResult = new DialogResult(ConsentDenyResult),
            DialogButtons = Array.Empty<DialogButton>(),
        });

        var window = dialog.GetWindow();
        AttachEscapeDismiss(window);

        // Deny/Allow live in FileConsentContent now and resolve vm.ResultTask directly - this just
        // closes the window once that happens, instead of reading a library button's result string.
        _ = vm.ResultTask.ContinueWith(_ => Dispatcher.UIThread.Post(window.Close));

        if (owner is not null)
            await dialog.ShowDialog(owner);
        else
            await dialog.Show();

        // The window can also close without a decision (Escape/Alt+F4/the title-bar close button) -
        // ResolveAsDeny is a no-op if Allow/Deny already resolved it, and fail-closed otherwise.
        vm.ResolveAsDeny();

        return await vm.ResultTask;
    }

    /// <summary>
    /// Builds and shows the first-run restore prompt that replaced <c>RestorePromptWindow</c>, deriving
    /// the displayed date from <paramref name="snapshotPath"/>'s filename (falling back to its
    /// last-write time) exactly as the window it replaces did.
    /// </summary>
    internal static async Task<bool> RestoreAsync(Window owner, string snapshotPath)
    {
        var loc = LocalizationService.Instance;
        var timestamp = TryParseSnapshotTimestamp(snapshotPath) ?? SafeGetLastWriteTimeUtc(snapshotPath);

        var content = new DialogContent(
            loc["Restore_PromptTitle"],
            string.Format(
                CultureInfo.CurrentCulture,
                loc["Restore_PromptMessage"],
                timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
            loc["Restore_Skip"],
            loc["Restore_Accept"],
            "primary");

        var dialog = DialogHelper.CreateCustomDialog(new CustomDialogBuilderParams
        {
            WindowTitle = loc["Restore_PromptTitle"],
            Content = content,
            Width = 460,
            StartupLocation = WindowStartupLocation.CenterOwner,
            NegativeResult = new DialogResult(SkipResult),
            DialogButtons = Array.Empty<DialogButton>(),
        });

        AttachEscapeDismiss(dialog.GetWindow());
        await dialog.ShowDialog(owner);
        return Resolve(content.ResultTask);
    }

    private static DateTime SafeGetLastWriteTimeUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.UtcNow; }
    }

    private static DateTime? TryParseSnapshotTimestamp(string path)
    {
        const string prefix = "autosave-";
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name) || !name.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var stamp = name[prefix.Length..];
        return DateTime.TryParseExact(
            stamp,
            "yyyyMMdd-HHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Adds the one thing a builder-created dialog window does not already have: Escape closing it.
    /// Closing the window here does not itself decide an outcome - <see cref="Resolve"/> and
    /// <see cref="FileConsentDialogViewModel.ResolveAsDeny"/> already treat a window that closed
    /// without a button click as a decline, so this only has to make Escape actually close it.
    /// </summary>
    private static void AttachEscapeDismiss(Window window)
    {
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                window.Close();
            }
        };
    }

    /// <summary>
    /// Resolves <see cref="ConfirmAsync"/>/<see cref="RestoreAsync"/>'s outcome from
    /// <see cref="DialogContent.ResultTask"/>: <c>true</c> only if a button click completed it, and
    /// <c>false</c> for every other way the window can have closed (Escape, Alt+F4, the title-bar
    /// close button all leave it incomplete). A pure function over the task rather than inline in each
    /// caller so it can be unit-tested without an Avalonia window.
    /// </summary>
    internal static bool Resolve(Task<bool> completion) => completion.IsCompletedSuccessfully && completion.Result;
}
