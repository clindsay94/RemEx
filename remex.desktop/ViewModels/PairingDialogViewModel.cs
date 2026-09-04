using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// View-model for the pairing PIN dialog (RemEx-x6a70.1). The dialog used to hand the PIN back and
/// close immediately, before it was even checked — the outcome only ever showed up on
/// <c>ConnectionViewModel</c>'s status line, one control away from where the user was looking. This
/// version stays open across the whole verification: it owns the call to <c>CompletePairingAsync</c>
/// itself (via <see cref="_completePairing"/>), shows busy / failure / success inline, and lets the
/// user correct a mistyped PIN and resubmit without the caller re-running the handshake.
/// </summary>
/// <remarks>
/// A retry is safe because <c>PairingClient.CompletePairingAsync</c> verifies the PIN LOCALLY first —
/// it HMACs the PIN with the already-derived session key and compares against the response's
/// <c>PinHmacBase64</c> — and returns false with no network traffic on a mismatch. Only a correct PIN
/// causes it to send the ack. The client keypair persists across calls, so calling it again with the
/// same <c>response</c> and a corrected PIN is valid (see <c>PairingClient.cs</c>, not touched here).
/// </remarks>
public partial class PairingDialogViewModel : ObservableObject
{
    private static readonly TimeSpan DefaultSuccessHold = TimeSpan.FromMilliseconds(700);

    private readonly Func<string, CancellationToken, Task<bool>> _completePairing;
    private readonly CancellationToken _cancellation;
    private readonly TimeSpan _successHold;
    private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    /// <summary>Whether the PIN field and Pair button should accept input right now — false while a
    /// verification is in flight, and false again after success (there is nothing left to edit).</summary>
    public bool CanEdit => !IsBusy && !IsSucceeded;

    /// <summary>True = paired. False = cancelled, aborted by an error the user gave up correcting, or
    /// completed because the connection attempt that owns this dialog timed out / was cancelled.</summary>
    public Task<bool> ResultTask => _tcs.Task;

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
        // longer matters.
        _cancellationRegistration = cancellation.Register(() => _tcs.TrySetResult(false));
        _tcs.Task.ContinueWith(
            _ => _cancellationRegistration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SubmitAsync()
    {
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
            _tcs.TrySetResult(false);
            return;
        }
        catch (Exception)
        {
            IsBusy = false;
            ErrorText = LocalizationService.Instance["Status_PairingFailed"]
                ?? "Pairing failed. Check the PIN on the PC";
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
            _tcs.TrySetResult(true);
        }
        else
        {
            IsBusy = false;
            ErrorText = LocalizationService.Instance["Status_PairingFailed"]
                ?? "Pairing failed. Check the PIN on the PC";
            // PinInput is left in place so the user can correct it and press Pair again — the next
            // Submit calls _completePairing again rather than restarting the connection.
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _tcs.TrySetResult(false);
    }
}
