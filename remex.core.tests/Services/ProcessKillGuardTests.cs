using Remex.Core.Services;
using Xunit;

namespace Remex.Core.Tests.Services;

/// <summary>
/// Covers the identity rule a host applies before ending a process.
/// </summary>
/// <remarks>
/// A PID is not an identity — operating systems recycle them, and between a client rendering its
/// process list and the user confirming, that number can belong to something else. Ending the wrong
/// program discards whatever it had not saved, with no undo (RemEx-druh).
/// </remarks>
public class ProcessKillGuardTests
{
    [Fact]
    public void MatchingName_IsAllowed()
        => Assert.True(ProcessKillGuard.IsExpectedProcess("chrome", "chrome"));

    [Fact]
    public void DifferentProgramOnTheSamePid_IsRefused()
        => Assert.False(ProcessKillGuard.IsExpectedProcess("chrome", "sshd"),
            "this is the whole point: the PID has been recycled by an unrelated program");

    /// <summary>
    /// The comparison is ordinal, case included.
    /// </summary>
    /// <remarks>
    /// Not an oversight. On Linux <c>Chrome</c> and <c>chrome</c> are genuinely different programs,
    /// and the name being compared round-trips from the host's own <c>Process.ProcessName</c> in the
    /// same session, so an exact match is the normal case. The failure directions are also not
    /// symmetric: a wrong refusal costs a retry, a wrong kill costs unsaved work.
    /// </remarks>
    [Theory]
    [InlineData("Chrome", "chrome")]
    [InlineData("chrome", "Chrome")]
    [InlineData("CHROME", "chrome")]
    public void CaseDifferences_AreRefused(string expected, string actual)
        => Assert.False(ProcessKillGuard.IsExpectedProcess(expected, actual));

    /// <summary>
    /// A client that does not send a name must still be able to end a process.
    /// </summary>
    /// <remarks>
    /// This is the backward-compatibility hinge of the whole change. <c>ExpectedName</c> is an
    /// optional wire parameter, so a phone older than this feature omits it — and if that were
    /// treated as "expected nothing, so match nothing", updating the PC would silently break Kill
    /// Process on every phone that had not updated yet. Blank is treated the same as absent for the
    /// same reason.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoNameSupplied_IsAllowedThrough(string? expected)
        => Assert.True(ProcessKillGuard.IsExpectedProcess(expected, "chrome"));

    /// <summary>
    /// A kernel-truncated name does not match its untruncated form — which is why the Linux host
    /// must not compare a client's echoed name against <c>Process.ProcessName</c>.
    /// </summary>
    /// <remarks>
    /// This looks like a test of the obvious, and it is here because the obvious bit was missed.
    /// Linux exposes a process's name twice: <c>/proc/&lt;pid&gt;/stat</c>'s <c>comm</c> field, which
    /// the kernel truncates to <c>TASK_COMM_LEN - 1</c> = 15 characters and which is what
    /// <c>LinuxProcessMonitorService.GetProcessesAsync</c> sends to clients, and
    /// <c>Process.ProcessName</c>, which is not truncated. The first version of this guard compared
    /// the client's echoed (truncated) name against the untruncated one, which refused EVERY process
    /// whose real name exceeds 15 characters — <c>gnome-terminal-server</c>,
    /// <c>xdg-desktop-portal</c>, most Electron helpers — every time, while telling the user their
    /// process list was stale when it was not. Found in review of RemEx-druh, empirically, on a real
    /// Linux process; no unit test of this rule could have caught it, because the rule was right and
    /// the INPUTS were mismatched. The Linux host now accepts either spelling.
    /// </remarks>
    [Fact]
    public void ATruncatedNameDoesNotMatchItsFullForm()
    {
        const string FullName = "gnome-terminal-server";
        var kernelTruncated = FullName[..15];

        Assert.Equal("gnome-terminal-", kernelTruncated);
        Assert.False(ProcessKillGuard.IsExpectedProcess(kernelTruncated, FullName),
            "the guard is working as designed here — it is the CALLER's job to compare like with " +
            "like, which is why LinuxProcessMonitorService checks the comm spelling too");
    }

    /// <summary>The refusal has to say enough that the user knows refreshing is the way forward.</summary>
    [Fact]
    public void MismatchMessage_NamesBothProgramsAndSaysNothingWasEnded()
    {
        var message = ProcessKillGuard.MismatchMessage(1234, "chrome", "sshd");

        Assert.Contains("1234", message);
        Assert.Contains("chrome", message);
        Assert.Contains("sshd", message);
        Assert.Contains("Nothing was ended", message);
    }
}
