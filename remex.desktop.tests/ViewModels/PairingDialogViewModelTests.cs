using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-x6a70.1: the dialog used to hand the PIN back and close immediately, before it was even
/// checked. It now owns the call to <c>CompletePairingAsync</c> (injected here as
/// <c>completePairing</c>) and stays open across busy / terminal-failure / success, closing itself
/// only when the user closes it (Cancel/Escape) or the owning connection attempt's cancellation
/// fires. Fix round 1: a failed PIN is TERMINAL, not retryable (see the view-model's class remarks
/// for why — <c>PairingClient.CompletePairingAsync</c>'s unconditional <c>finally</c> disposes the
/// keypair on every exit path, so a second call always fails regardless of the PIN typed).
/// </summary>
public class PairingDialogViewModelTests
{
    private static PairingDialogViewModel Vm(
        Func<string, CancellationToken, Task<bool>> completePairing,
        CancellationToken cancellation = default,
        TimeSpan? successHold = null) =>
        new(completePairing, cancellation, successHold ?? TimeSpan.Zero);

    [Fact]
    public void Submit_WithInvalidPin_SetsErrorAndDoesNotCallDelegate()
    {
        var called = false;
        var vm = Vm((_, _) =>
        {
            called = true;
            return Task.FromResult(true);
        });
        vm.PinInput = "123";

        vm.SubmitCommand.Execute(null);

        vm.ErrorText.Should().Be(LocalizationService.Instance["Pairing_InvalidPin"]);
        called.Should().BeFalse("a PIN that fails the length check should never reach the delegate");
        vm.ResultTask.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Submit_WithValidPin_CallsDelegateOnceWithThatPin()
    {
        string? seenPin = null;
        var vm = Vm((pin, _) =>
        {
            seenPin = pin;
            return Task.FromResult(true);
        });
        vm.PinInput = "123456";

        await vm.SubmitCommand.ExecuteAsync(null);

        seenPin.Should().Be("123456");
    }

    [Fact]
    public async Task Submit_WhenDelegateSucceeds_GoesThroughSucceededThenResolvesPaired()
    {
        var vm = Vm((_, _) => Task.FromResult(true));
        vm.PinInput = "123456";

        await vm.SubmitCommand.ExecuteAsync(null);

        vm.IsSucceeded.Should().BeTrue();
        vm.IsBusy.Should().BeFalse();
        vm.ResultTask.IsCompletedSuccessfully.Should().BeTrue();
        (await vm.ResultTask).Should().Be(PairingDialogResult.Paired);
    }

    [Fact]
    public async Task Submit_WhenDelegateFails_IsTerminal_SecondSubmitIsNoOpAndCancelResolvesFailed()
    {
        var callCount = 0;
        var vm = Vm((_, _) =>
        {
            callCount++;
            return Task.FromResult(false);
        });
        vm.PinInput = "111111";

        await vm.SubmitCommand.ExecuteAsync(null);

        vm.IsFailed.Should().BeTrue();
        vm.CanEdit.Should().BeFalse("a terminal failure has no retry — see the view-model's class remarks");
        vm.ErrorText.Should().Be(LocalizationService.Instance["Status_PairingFailed"]);
        vm.SubmitCommand.CanExecute(null).Should().BeFalse("the Pair button must not accept another attempt");
        vm.ResultTask.IsCompleted.Should().BeFalse("a failure alone doesn't close the dialog; the user closes it");

        // A second Submit is gated by CanExecute (RelayCommand.ExecuteAsync honors it), so this must
        // be a no-op rather than a second handshake attempt against an already-torn-down PairingClient.
        vm.PinInput = "222222";
        await vm.SubmitCommand.ExecuteAsync(null);

        callCount.Should().Be(1, "the failed PIN check is terminal; retrying calls a PairingClient " +
            "whose keypair was already disposed by CompletePairingAsync's finally block");

        vm.CancelCommand.Execute(null);
        (await vm.ResultTask).Should().Be(PairingDialogResult.Failed);
    }

    [Fact]
    public async Task Cancel_DuringSuccessHold_ResolvesPaired()
    {
        // A hold far longer than the test can wait, so the only way ResultTask completes here is
        // through the Cancel path under test; a short hold could expire first on a loaded runner and
        // let the normal path satisfy the assertion without the race ever being exercised.
        var vm = Vm((_, _) => Task.FromResult(true), successHold: TimeSpan.FromSeconds(30));
        vm.PinInput = "123456";

        _ = vm.SubmitCommand.ExecuteAsync(null);
        while (!vm.IsSucceeded)
        {
            await Task.Delay(5);
        }

        vm.ResultTask.IsCompleted.Should().BeFalse("the hold has not elapsed, so nothing has resolved yet");

        // Escape/Cancel during the success hold must not discard an already-completed pairing.
        vm.CancelCommand.Execute(null);

        (await vm.ResultTask).Should().Be(PairingDialogResult.Paired);
    }

    [Fact]
    public async Task TokenCancellation_DuringSuccessHold_ResolvesPaired()
    {
        using var cts = new CancellationTokenSource();
        // Same reasoning as the Cancel test above: only the token can end this hold in time.
        var vm = Vm((_, _) => Task.FromResult(true), cts.Token, TimeSpan.FromSeconds(30));
        vm.PinInput = "123456";

        var submitTask = vm.SubmitCommand.ExecuteAsync(null);
        while (!vm.IsSucceeded)
        {
            await Task.Delay(5);
        }

        vm.ResultTask.IsCompleted.Should().BeFalse("the hold has not elapsed, so nothing has resolved yet");

        cts.Cancel();
        await submitTask;

        (await vm.ResultTask).Should().Be(PairingDialogResult.Paired,
            "the pairing itself succeeded before the owning connection attempt's token fired");
    }

    [Fact]
    public async Task TokenCancellation_WhileBusy_ResolvesCancelled_WithIsBusyFalse()
    {
        using var cts = new CancellationTokenSource();
        var vm = Vm(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return false; }, cts.Token);
        vm.PinInput = "123456";

        var submitTask = vm.SubmitCommand.ExecuteAsync(null);
        while (!vm.IsBusy)
        {
            await Task.Delay(5);
        }

        cts.Cancel();
        await submitTask;

        vm.IsBusy.Should().BeFalse();
        (await vm.ResultTask).Should().Be(PairingDialogResult.Cancelled);
    }

    [Fact]
    public async Task Cancel_BeforeAnyAttempt_ResolvesCancelled()
    {
        var vm = Vm((_, _) => Task.FromResult(true));

        vm.CancelCommand.Execute(null);

        vm.ResultTask.IsCompletedSuccessfully.Should().BeTrue();
        (await vm.ResultTask).Should().Be(PairingDialogResult.Cancelled);
    }

    [Fact]
    public async Task AlreadyCancelledToken_ResolvesCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var vm = Vm((_, _) => Task.FromResult(true), cts.Token);

        vm.ResultTask.IsCompletedSuccessfully.Should().BeTrue();
        (await vm.ResultTask).Should().Be(PairingDialogResult.Cancelled);
    }

    [Fact]
    public async Task Submit_WhenDelegateThrows_IsTerminal()
    {
        var vm = Vm((_, _) => throw new InvalidOperationException("boom"));
        vm.PinInput = "123456";

        await vm.SubmitCommand.ExecuteAsync(null);

        vm.ErrorText.Should().Be(LocalizationService.Instance["Status_PairingFailed"]);
        vm.IsBusy.Should().BeFalse();
        vm.IsFailed.Should().BeTrue();
        vm.CanEdit.Should().BeFalse();
        vm.ResultTask.IsCompleted.Should().BeFalse();

        vm.CancelCommand.Execute(null);
        (await vm.ResultTask).Should().Be(PairingDialogResult.Failed);
    }

    [Fact]
    public async Task CanEdit_IsFalseWhileBusyAndAfterSuccess()
    {
        var gate = new TaskCompletionSource<bool>();
        var vm = Vm((_, _) => gate.Task);
        vm.PinInput = "123456";
        vm.CanEdit.Should().BeTrue();

        var submitTask = vm.SubmitCommand.ExecuteAsync(null);
        vm.IsBusy.Should().BeTrue();
        vm.CanEdit.Should().BeFalse("busy means the field and button must not accept more input");

        gate.SetResult(true);
        await submitTask;

        vm.CanEdit.Should().BeFalse("once succeeded there is nothing left to edit");
    }
}
