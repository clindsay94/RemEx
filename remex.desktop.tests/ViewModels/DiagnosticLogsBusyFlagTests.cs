using System;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The shared busy affordance's spinner-in-button mode needs a flag to bind to (RemEx-kjdi §2) —
/// before this bead, <c>FetchServiceLogsCommand</c> gave no feedback at all while it shelled out to
/// PowerShell/journalctl.
/// </summary>
/// <remarks>
/// Both tests inject <see cref="DiagnosticLogsViewModel.CommandRunner"/> rather than letting
/// <see cref="DiagnosticLogsViewModel.FetchServiceLogsAsync"/> spawn a real
/// powershell.exe/journalctl child process (review finding on the original version of this file):
/// an unbounded, load-sensitive external process has no place in a unit test.
/// </remarks>
public class DiagnosticLogsBusyFlagTests
{
    /// <summary>
    /// Gated on a <see cref="TaskCompletionSource{TResult}"/> rather than relying on the async
    /// method's synchronous prefix: this proves the flag stays true for as long as the command is
    /// genuinely still running, not just "true at the instant of the call".
    /// </summary>
    [Fact]
    public async Task IsFetchingServiceLogs_IsTrueDuringTheReadAndFalseAfter()
    {
        var vm = new DiagnosticLogsViewModel(null!);
        var gate = new TaskCompletionSource<(bool Success, string Output)>();
        vm.CommandRunner = (_, _) => gate.Task;

        var task = vm.FetchServiceLogsAsync();
        vm.IsFetchingServiceLogs.Should().BeTrue("the command has not returned yet");

        gate.SetResult((true, "irrelevant"));
        await task;

        vm.IsFetchingServiceLogs.Should().BeFalse("the read finished");
    }

    /// <summary>
    /// <see cref="DiagnosticLogsViewModel.FetchServiceLogsAsync"/> catches every exception
    /// internally (it turns one into <c>Logs_Service_ReadError</c> text rather than letting it
    /// escape), and the flag's reset lives in a <c>finally</c> that covers that catch as well as the
    /// happy path. Forcing <see cref="DiagnosticLogsViewModel.CommandRunner"/> to throw proves the
    /// reset actually runs on that path too, not just structurally.
    /// </summary>
    [Fact]
    public async Task IsFetchingServiceLogs_IsFalseAfterTheCommandThrows()
    {
        var vm = new DiagnosticLogsViewModel(null!);
        vm.CommandRunner = (_, _) => throw new InvalidOperationException("simulated command failure");

        await vm.FetchServiceLogsAsync();

        vm.IsFetchingServiceLogs.Should().BeFalse("a thrown exception must still clear the busy flag");
    }
}
