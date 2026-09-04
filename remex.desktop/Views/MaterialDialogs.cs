using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
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
/// FIX ROUND 1 (RemEx-x6a70.3): the first pass left two regressions against the RemEx-2m7fr dialogs
/// this replaced. <see cref="ConfirmAsync"/> and <see cref="RestoreAsync"/> now build through
/// <see cref="DialogHelper.CreateCustomDialog"/> with <see cref="DialogContent"/> instead of
/// <see cref="DialogHelper.CreateAlertDialog"/>'s <c>ContentHeader</c>/<c>SupportingText</c>, because
/// <c>AlertDialog</c>'s <c>SupportingText</c> TextBlock has no <c>TextWrapping</c> set and clips a
/// long message instead of wrapping it - moving to a custom content control sidesteps that TextBlock
/// entirely rather than trying to patch it. The second regression - the confirm/deny/restore actions
/// rendering as indistinguishable flat text instead of this app's primary/secondary/danger button
/// vocabulary - is NOT fixed here. It was investigated by decompiling
/// Material.Avalonia.Dialogs 3.19.0 (net8.0, ilspycmd): <c>DialogWindowBase&lt;TWindow,TResult&gt;.Procedure</c>
/// resolves <c>ShowDialog</c>'s result from <c>_window.GetResult()</c> on the window's <c>Closed</c>
/// event, which reads <c>(DataContext as ...ViewModel)?.DialogResult</c> - a property with an
/// <c>internal</c> setter, only ever written by the library's own <c>ObsoleteDialogButtonViewModel</c>
/// command handler. The object passed to <c>Window.Close(object)</c> is never read by that path, and
/// external code cannot set <c>DialogResult</c> itself, so a custom button in this dialog's content
/// has no way to produce a result <c>ShowDialog</c> would see. <c>DialogButton</c> also carries no
/// <c>Classes</c> property - only <c>IsPositive</c>/<c>IsNegative</c> - so there is no builder-level
/// way to hand a library-rendered button this app's <c>primary</c>/<c>secondary</c>/<c>danger</c>
/// Classes either. Every builder below still hands Material.Avalonia its own <c>DialogButtons</c> for
/// the actions, exactly as before, now with <c>IsNegative</c> set on the cancel/skip/deny button too -
/// <c>DialogHelper.CreateObsoleteButtonArray</c> does not currently read it (only <c>IsPositive</c> is
/// copied onto the button view model), so it has no visible effect against 3.19.0, but it records the
/// intent for whenever that changes rather than silently relying on an undocumented gap. Only the
/// wrapping is fixed in this round.
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

        var dialog = DialogHelper.CreateCustomDialog(new CustomDialogBuilderParams
        {
            WindowTitle = loc["Dialog_ConfirmTitle"],
            Content = new DialogContent(title, message),
            Width = 440,
            StartupLocation = WindowStartupLocation.CenterOwner,
            NegativeResult = new DialogResult(CancelResult),
            DialogButtons = new[]
            {
                new DialogButton { Result = CancelResult, Content = loc["Btn_Cancel"], IsNegative = true },
                new DialogButton { Result = ConfirmResult, Content = confirmText, IsPositive = true },
            },
        });

        AttachEscapeDismiss(dialog.GetWindow());
        var result = await dialog.ShowDialog(owner);
        return result?.GetResult == ConfirmResult;
    }

    /// <summary>
    /// Builds and shows the file-sharing consent prompt that replaced <c>FileConsentDialog</c>.
    /// Content is <see cref="FileConsentContent"/>, a plain UserControl bound to
    /// <paramref name="vm"/> so its message, detail and "remember" checkbox keep their exact
    /// bindings from before. The dialog's own Deny/Allow buttons resolve, on close, into the same
    /// <see cref="FileConsentDialogViewModel.AllowCommand"/> / <see cref="FileConsentDialogViewModel.DenyCommand"/>
    /// the view model already exposed, so the decision logic itself never moved.
    /// </summary>
    /// <param name="owner">
    /// The dialog's parent window, or <c>null</c> to show it unparented — mirrors the
    /// non-desktop-lifetime branch <c>App.axaml.cs</c> already had, where no <see cref="Window"/>
    /// exists to own it.
    /// </param>
    /// <remarks>
    /// Deny/Allow are still Material.Avalonia's own <c>DialogButtons</c>, not this app's
    /// primary/secondary vocabulary - see the type remarks' fix-round-1 note on why a builder-level
    /// button restyle is not possible against 3.19.0.
    /// </remarks>
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
            DialogButtons = new[]
            {
                new DialogButton { Result = ConsentDenyResult, Content = loc["FileConsent_Deny"], IsNegative = true },
                new DialogButton { Result = ConsentAllowResult, Content = loc["FileConsent_Allow"], IsPositive = true },
            },
        });

        AttachEscapeDismiss(dialog.GetWindow());
        var result = owner is not null ? await dialog.ShowDialog(owner) : await dialog.Show();

        // ROUTE THROUGH THE VM'S OWN COMMANDS, NOT A FRESH DECISION HERE. AllowCommand/DenyCommand
        // are the one place "Allow + the Remember checkbox becomes a FileConsentDecision" is decided
        // (RemEx-2m7fr pinned those lines unchanged) - this only picks which of the two to run.
        if (MapConsent(result?.GetResult, vm.Remember).Granted)
            vm.AllowCommand.Execute(null);
        else
            vm.DenyCommand.Execute(null);

        return await vm.ResultTask;
    }

    /// <summary>
    /// Pure allow/deny routing for the consent dialog's result string. <c>"allow"</c> is the only
    /// value that grants; everything else - <c>"deny"</c>, <c>null</c>, <c>"none"</c>, a stray
    /// <c>"cancel"</c> - denies. Fail-closed by construction rather than by enumerating every bad
    /// value.
    /// </summary>
    internal static FileConsentDecision MapConsent(string? result, bool remember) =>
        result == ConsentAllowResult
            ? new FileConsentDecision(Granted: true, Remember: remember)
            : new FileConsentDecision(Granted: false, Remember: false);

    /// <summary>
    /// Builds and shows the first-run restore prompt that replaced <c>RestorePromptWindow</c>, deriving
    /// the displayed date from <paramref name="snapshotPath"/>'s filename (falling back to its
    /// last-write time) exactly as the window it replaces did.
    /// </summary>
    internal static async Task<bool> RestoreAsync(Window owner, string snapshotPath)
    {
        var loc = LocalizationService.Instance;
        var timestamp = TryParseSnapshotTimestamp(snapshotPath) ?? SafeGetLastWriteTimeUtc(snapshotPath);

        var dialog = DialogHelper.CreateCustomDialog(new CustomDialogBuilderParams
        {
            WindowTitle = loc["Restore_PromptTitle"],
            Content = new DialogContent(
                loc["Restore_PromptTitle"],
                string.Format(
                    CultureInfo.CurrentCulture,
                    loc["Restore_PromptMessage"],
                    timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))),
            Width = 460,
            StartupLocation = WindowStartupLocation.CenterOwner,
            NegativeResult = new DialogResult(SkipResult),
            DialogButtons = new[]
            {
                new DialogButton { Result = SkipResult, Content = loc["Restore_Skip"], IsNegative = true },
                new DialogButton { Result = RestoreResult, Content = loc["Restore_Accept"], IsPositive = true },
            },
        });

        AttachEscapeDismiss(dialog.GetWindow());
        var result = await dialog.ShowDialog(owner);
        return result?.GetResult == RestoreResult;
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
    /// Every other dismissal path (Alt+F4, the title-bar close button) already resolves to the
    /// negative result set at build time - see the type remarks. Closing the window here does not
    /// set a result itself; it relies on that negative result never having been overwritten, which
    /// holds because overwriting it requires a button click.
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
}
