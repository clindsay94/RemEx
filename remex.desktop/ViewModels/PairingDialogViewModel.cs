using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// Outcome of a <see cref="PairingDialogViewModel"/> run. <see cref="Paired"/> means
/// <c>CompletePairingAsync</c> returned true and the caller should persist the pin. <see cref="Failed"/>
/// means the PIN was checked and rejected (or the check threw) and the user closed the dialog on that
/// terminal state. <see cref="Cancelled"/> means the dialog closed before any definitive PIN result —
/// Cancel/Escape before or during an attempt, or the owning connection's token firing.
/// </summary>
public enum PairingDialogResult
{
    Paired,
    Failed,
    Cancelled
}

/// <summary>
/// View-model for the pairing PIN dialog (RemEx-x6a70.1). The dialog used to hand the PIN back and
/// close immediately, before it was even checked — the outcome only ever showed up on
/// <c>ConnectionViewModel</c>'s status line, one control away from where the user was looking. This
/// version stays open across the whole verification: it owns the call to <c>CompletePairingAsync</c>
/// itself (via <see cref="_completePairing"/>), and shows busy / terminal-failure / success inline.
/// </summary>
/// <remarks>
/// There is no in-dialog retry. <c>PairingClient.CompletePairingAsync</c> has an unconditional
/// <c>finally</c> (<c>PairingClient.cs:163-171</c>) that disposes the client's ECDH keypair and zeroes
/// the session key on every exit path, including a local PIN-mismatch <c>return false</c>. A second
/// call on the same <c>PairingClient</c> instance hits the <c>_clientEcdh == null</c> guard
/// (<c>PairingClient.cs:85</c>) and fails instantly, regardless of what PIN is typed. So a false (or
/// thrown) result here is terminal: the dialog reports the failure and waits for the user to close it.
/// A real retry needs a fresh <c>StartPairingAsync</c> handshake, which may re-mint the PIN — that is a
/// change to Remex.Core (shared, NativeAOT, also used by the Android client), not this bead.
/// </remarks>
public partial class PairingDialogViewModel : ObservableObject
{
    private static readonly TimeSpan DefaultSuccessHold = TimeSpan.FromMilliseconds(700);

    private readonly Func<string, CancellationToken, Task<bool>> _completePairing;
    private readonly CancellationToken _cancellation;
    private readonly TimeSpan _successHold;
    private readonly TaskCompletionSource<PairingDialogResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _cancellationRegistration;

    [ObservableProperty]
    private string _pinInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorText;

    /// <summary>True once <see cref="_completePairing"/> has returned true. The dialog holds itself
    /// open for <see cref="_successHold"/> so the outcome is actually readable before it closes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _isSucceeded;

    /// <summary>True once the PIN has been checked and rejected (or the check threw). Terminal: there
    /// is no retry (see the class remarks), so the dialog stays open showing the failure until the user
    /// closes it via Cancel/Escape.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _isFailed;

    /// <summary>Whether the PIN field and Pair button should accept input right now — false while a
    /// verification is in flight, false after success (nothing left to edit), and false after a
    /// terminal failure (no retry; see the class remarks).</summary>
    public bool CanEdit => !IsBusy && !IsSucceeded && !IsFailed;

    /// <summary>Terminal outcome of this dialog run. See <see cref="PairingDialogResult"/>.</summary>
    public Task<PairingDialogResult> ResultTask => _tcs.Task;

    public PairingDialogViewModel(
        Func<string, CancellationToken, Task<bool>> completePairing,
        CancellationToken cancellation,
        TimeSpan? successHold = null)
    {
        _completePairing = completePairing ?? throw new ArgumentNullException(nameof(completePairing));
        _cancellation = cancellation;
        _successHold = successHold ?? DefaultSuccessHold;

        // The dialog is owned by ConnectionViewModel.ConnectAsync's linked cancellation token. If that
        // connection attempt times out or is cancelled while the user is still looking at the PIN box,
        // the dialog has to close itself rather than sit open forever waiting for a Submit that no
        // longer matters. A token firing after the delegate already succeeded still reports Paired —
        // the pairing itself is done, only the caller's wait timed out.
        _cancellationRegistration = cancellation.Register(
            () => _tcs.TrySetResult(IsSucceeded ? PairingDialogResult.Paired : PairingDialogResult.Cancelled));
        _tcs.Task.ContinueWith(
            _ => _cancellationRegistration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SubmitAsync()
    {
        // CanExecute gates the bound button, but ExecuteAsync can be invoked directly (tests, or a
        // stale command reference), so re-check here: a terminal failure/success must never re-enter
        // and call _completePairing a second time — see the class remarks on why a retry can't work.
        if (!CanEdit)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(PinInput) || PinInput.Length != 6)
        {
            ErrorText = LocalizationService.Instance["Pairing_InvalidPin"] ?? "PIN must be 6 digits.";
            return;
        }

        ErrorText = null;
        IsBusy = true;

        bool succeeded;
        try
        {
            succeeded = await _completePairing(PinInput, _cancellation);
        }
        catch (OperationCanceledException)
        {
            IsBusy = false;
            _tcs.TrySetResult(PairingDialogResult.Cancelled);
            return;
        }
        catch (Exception)
        {
            IsBusy = false;
            IsFailed = true;
            ErrorText = LocalizationService.Instance["Status_PairingFailed"]
                ?? "Pairing failed. Check the PIN on the PC";
            // Terminal: see the class remarks. There is no retry, the PIN box stays read-only via
            // CanEdit, and the dialog waits for the user to close it (Cancel/Escape).
            return;
        }

        if (succeeded)
        {
            IsBusy = false;
            IsSucceeded = true;
            try
            {
                await Task.Delay(_successHold, _cancellation);
            }
            catch (OperationCanceledException)
            {
                // The caller is already tearing the connection down; the pairing itself still
                // succeeded, so the dialog still reports success rather than a cancellation.
            }
            _tcs.TrySetResult(PairingDialogResult.Paired);
        }
        else
        {
            IsBusy = false;
            IsFailed = true;
            ErrorText = LocalizationService.Instance["Status_PairingFailed"]
                ?? "Pairing failed. Check the PIN on the PC";
            // Terminal: see the class remarks. There is no retry, the PIN box stays read-only via
            // CanEdit, and the dialog waits for the user to close it (Cancel/Escape).
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _tcs.TrySetResult(IsSucceeded
            ? PairingDialogResult.Paired
            : IsFailed
                ? PairingDialogResult.Failed
                : PairingDialogResult.Cancelled);
    }
}
