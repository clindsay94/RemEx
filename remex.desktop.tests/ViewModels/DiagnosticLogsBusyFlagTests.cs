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
public class DiagnosticLogsBusyFlagTests
{
    /// <summary>
    /// <see cref="DiagnosticLogsViewModel.IsFetchingServiceLogs"/> is set to <see langword="true"/>
    /// as the very first statement in <see cref="DiagnosticLogsViewModel.FetchServiceLogsAsync"/>, so
    /// it is already observable the instant the call returns its Task — before the first await runs
    /// a C# async method's synchronous prefix executes inline. No fake/seam needed to catch "during".
    /// </summary>
    [Fact]
    public async Task IsFetchingServiceLogs_IsTrueDuringTheReadAndFalseAfter()
    {
        var vm = new DiagnosticLogsViewModel(null!);

        var task = vm.FetchServiceLogsAsync();
        vm.IsFetchingServiceLogs.Should().BeTrue("the flag flips before the first await, not after it");

        await task;

        vm.IsFetchingServiceLogs.Should().BeFalse("the read finished (successfully or not)");
    }

    // NOTE ON THE EXCEPTION PATH: FetchServiceLogsAsync already catches every exception internally
    // (it turns one into Logs_Service_ReadError text rather than letting it escape), and the flag
    // reset lives in a `finally` that covers that catch as well as the happy path — so "false on
    // exception" is structurally guaranteed by the same code the test above exercises. There is no
    // seam here to force the underlying PowerShell/journalctl call to fail without actually breaking
    // one of those tools on the test machine, so this is not independently forced by a second test.
}
