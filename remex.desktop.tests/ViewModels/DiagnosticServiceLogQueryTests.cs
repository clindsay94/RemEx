using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Pins the Linux half of the Diagnostics "system event logs" tab (RemEx-2vfx).
/// </summary>
/// <remarks>
/// <para>
/// The failure this guards against shipped and sat invisible: the tab queried
/// <c>journalctl -u remex-host</c>, a systemd unit <c>agent-install.sh</c> actively DELETES
/// (it is the file's <c>LEGACY_SERVICE_UNIT</c>), so the panel was permanently empty on every
/// current Linux install — and an empty diagnostics panel reads as "nothing has gone wrong",
/// which is the worst possible rendering of "this query can never match".
/// </para>
/// <para>
/// Windows-only caveat, stated per the cross-platform guardrail: these tests pin the query
/// string and the empty-case wording, which is everything that can be verified on this
/// machine. Whether the user journal actually carries entries on a real CachyOS install
/// depends on the desktop routing XDG autostart through systemd, and is a follow-up bead.
/// </para>
/// </remarks>
public class DiagnosticServiceLogQueryTests
{
    [Fact]
    public void TheJournalQueryNamesTheProcessNotTheRetiredUnit()
    {
        // _COMM is the kernel's 15-character process name; the Linux binary is Remex.Agent (11).
        Assert.Contains("_COMM=Remex.Agent", DiagnosticLogsViewModel.LinuxJournalArguments);
        Assert.Contains("--user", DiagnosticLogsViewModel.LinuxJournalArguments);

        // The regression this bead exists for: the unit the installer deletes must never come back.
        Assert.DoesNotContain("remex-host", DiagnosticLogsViewModel.LinuxJournalArguments);
        Assert.DoesNotContain("-u ", DiagnosticLogsViewModel.LinuxJournalArguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("-- No entries --")]
    [InlineData("  -- No entries --  \n")]
    public void AnEmptyJournalExplainsItselfInsteadOfReadingAsHealthy(string output)
    {
        var text = DiagnosticLogsViewModel.DescribeLinuxJournal(output);

        // The empty state must say WHY it can be empty and where the real record lives —
        // a bare blank is indistinguishable from "no problems".
        Assert.Contains("XDG autostart", text);
        Assert.Contains("Logs tab", text);
    }

    [Fact]
    public void RealJournalOutputIsShownVerbatim()
    {
        const string entries = "Aug 26 03:00:00 pc Remex.Agent[123]: started\n"
            + "Aug 26 03:00:01 pc Remex.Agent[123]: listening";

        Assert.Equal(entries, DiagnosticLogsViewModel.DescribeLinuxJournal(entries + "\n"));
    }

    [Fact]
    public void AnEntryThatEmbedsTheNoEntriesLiteralDoesNotSuppressTheOutput()
    {
        // The empty-detection must read the WHOLE output's start, not search inside it — one log
        // line quoting journalctl's own phrase must not make a full journal claim to be empty.
        const string entries = "Aug 26 03:00:00 pc Remex.Agent[123]: parser saw '-- No entries --'";

        Assert.Equal(entries, DiagnosticLogsViewModel.DescribeLinuxJournal(entries));
    }
}
