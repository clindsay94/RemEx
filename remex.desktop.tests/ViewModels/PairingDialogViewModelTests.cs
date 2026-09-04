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
/// <c>completePairing</c>) and stays open across busy / failure / retry / success, closing itself
/// only on success, Cancel, or the owning connection attempt's cancellation.
/// </summary>
public class PairingDialogViewModelTests
{
    private static PairingDialogViewModel Vm(
        Func<string, CancellationToken, Task<bool>> completePairing,
        CancellationToken cancellation = default) =>
        new(completePairing, cancellation, successHold: TimeSpan.Zero);

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
    public async Task Submit_WhenDelegateSucceeds_GoesThroughSucceededThenResolvesTrue()
    {
        var vm = Vm((_, _) => Task.FromResult(true));
        vm.PinInput = "123456";

        await vm.SubmitCommand.ExecuteAsync(null);

        vm.IsSucceeded.Should().BeTrue();
        vm.IsBusy.Should().BeFalse();
        vm.ResultTask.IsCompletedSuccessfully.Should().BeTrue();
        (await vm.ResultTask).Should().BeTrue();
    }

    [Fact]
    public async Task Submit_WhenDelegateFails_ShowsErrorAndLeavesResultPendingForRetry()
    {
        var callCount = 0;
        var vm = Vm((_, _) =>
        {
            callCount++;
            return Task.FromResult(callCount > 1); // fails first, succeeds on retry
        });
        vm.PinInput = "111111";

        await vm.SubmitCommand.ExecuteAsync(null);

        vm.ErrorText.Should().Be(LocalizationService.Instance["Status_PairingFailed"]);
        vm.IsBusy.Should().BeFalse();
        vm.ResultTask.IsCompleted.Should().BeFalse("a failed PIN lets the user correct it, it doesn't close the dialog");

        // The user corrects the PIN and presses Pair again — the delegate is called again, not the
        // whole connection attempt restarted.
        vm.PinInput = "222222";
        await vm.SubmitCommand.ExecuteAsync(null);

        callCount.Should().Be(2);
        vm.IsSucceeded.Should().BeTrue();
        (await vm.ResultTask).Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_ResolvesResultFalse()
    {
        var vm = Vm((_, _) => Task.FromResult(true));

        vm.CancelCommand.Execute(null);

        vm.ResultTask.IsCompletedSuccessfully.Should().BeTrue();
        (await vm.ResultTask).Should().BeFalse();
    }

    [Fact]
    public async Task AlreadyCancelledToken_ResolvesResultFalse()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var vm = Vm((_, _) => Task.FromResult(true), cts.Token);

        vm.ResultTask.IsCompletedSuccessfully.Should().BeTrue();
        (await vm.ResultTask).Should().BeFalse();
    }

    [Fact]
    public async Task Submit_WhenDelegateThrows_SetsErrorAndLeavesResultPending()
    {
        var vm = Vm((_, _) => throw new InvalidOperationException("boom"));
        vm.PinInput = "123456";

        await vm.SubmitCommand.ExecuteAsync(null);

        vm.ErrorText.Should().Be(LocalizationService.Instance["Status_PairingFailed"]);
        vm.IsBusy.Should().BeFalse();
        vm.ResultTask.IsCompleted.Should().BeFalse();
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
