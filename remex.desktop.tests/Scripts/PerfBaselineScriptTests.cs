using System.IO;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Scripts;

/// <summary>
/// scripts/perf-baseline.ps1 (RemEx-gtwk8) is PowerShell, so nothing here can compile-check it —
/// these are text assertions over the source, the same shape .NET already uses elsewhere in this
/// repo for a script a build cannot verify (see <see cref="PaletteSweepScriptTests"/>). They exist
/// to catch the ways this script could quietly stop keeping its safety contract: leaving a stray
/// worktree behind, leaving the machine deployed on a throwaway worktree build instead of HEAD, or
/// starting to fake input instead of only reading the UIA tree.
/// </summary>
public class PerfBaselineScriptTests
{
    private static string ScriptPath() =>
        Path.Combine(RepoRoot(), "scripts", "perf-baseline.ps1");

    private static string ScriptText() => File.ReadAllText(ScriptPath());

    [Fact]
    public void ScriptExistsAndIsNotEmpty()
    {
        File.Exists(ScriptPath()).Should().BeTrue("the perf baseline script must be tracked at scripts/perf-baseline.ps1");
        ScriptText().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AddsAGitWorktreePerRef()
    {
        ScriptText().Should().MatchRegex(@"git\s+-C\s+\$RepoRoot\s+worktree\s+add",
            "each measured ref must be built in its own isolated worktree rather than mutating the caller's checkout");
    }

    [Fact]
    public void RemovesEveryWorktreeItCreated()
    {
        ScriptText().Should().MatchRegex(@"git\s+-C\s+\$RepoRoot\s+worktree\s+remove\s+--force",
            "every worktree this script adds must be force-removed - a leaked worktree directory under $env:TEMP is exactly the kind of mess this script must not leave behind");
        ScriptText().Should().MatchRegex(@"git\s+-C\s+\$RepoRoot\s+worktree\s+prune",
            "worktree remove must be followed by a prune so git's own bookkeeping doesn't drift from disk");
    }

    [Fact]
    public void HasATopLevelFinallyBlock()
    {
        Regex.IsMatch(ScriptText(), @"(?m)^\s*finally\s*\{").Should().BeTrue(
            "cleanup (redeploy HEAD, remove worktrees, conditionally restart) must run in a finally block so a mid-run failure - including a launch that never shows a window - still leaves the machine in a known state");
    }

    [Fact]
    public void FinallyBlockRedeploysHeadFromThisCheckoutNotAWorktree()
    {
        var finallyBody = FinallyBlockBody();
        finallyBody.Should().MatchRegex(@"Invoke-UpdateLocalInstall\s+-ScriptPath\s+\(Join-Path\s+\$RepoRoot\s+'scripts\\update-local-install\.ps1'\)",
            "the finally block must redeploy using THIS checkout's own update-local-install.ps1 ($RepoRoot), not a worktree path that is about to be deleted by the same block");
    }

    [Fact]
    public void FinallyBlockRemovesWorktreesAndOnlyConditionallyRestarts()
    {
        var finallyBody = FinallyBlockBody();
        finallyBody.Should().Contain("worktree remove",
            "worktree cleanup belongs inside finally, not only on the success path");
        finallyBody.Should().Contain("$wasRunningBeforeScript",
            "the host must only be restarted at the end if it was already running when the script started - restarting unconditionally would turn a no-op baseline run into an unwanted launch");
    }

    [Fact]
    public void PollsUiaForTheMainWindowByProcessIdAndName()
    {
        var text = ScriptText();
        text.Should().Contain("AutomationElement]::RootElement",
            "window detection must use the same UIA RootElement pattern as scripts/ui-snapshot.ps1, not a heuristic sleep");
        text.Should().MatchRegex(@"ProcessIdProperty",
            "the poll must filter by the launched process's PID");
        text.Should().MatchRegex(@"-like\s+'RemEx\*'",
            "the poll must match the window by its RemEx* name, matching scripts/ui-snapshot.ps1's WindowTitle default");
    }

    [Fact]
    public void NeverInjectsKeystrokesOrTouchesTheProfile()
    {
        var text = ScriptText();
        text.Should().NotContain("SendKeys", "UIA usage here must stay read-only - this script measures, it never drives the UI");
        text.Should().NotMatch("*dashboard_layout*", "this script must not touch the user's real profile/layout file");
    }

    [Fact]
    public void NeverInvokesUiHotreload()
    {
        // The header prose is allowed to mention ui-hotreload.ps1 by name (to say this script does
        // NOT build inside it) - what must never appear is an actual invocation of that script.
        ScriptText().Should().NotMatch("*hotReloadScript*",
            "perf measurement must go through update-local-install.ps1's real publish, not the hot-reload dev loop");
        ScriptText().Should().NotMatch("*ui-hotreload.ps1'*",
            "the script must never invoke ui-hotreload.ps1 - only mention it in prose");
        ScriptText().Should().NotMatch("*ui-hotreload.ps1\"*",
            "the script must never invoke ui-hotreload.ps1 - only mention it in prose");
    }

    [Fact]
    public void ColdStartIsTheFirstLaunchAndTheRestAreWarm()
    {
        var text = ScriptText();
        text.Should().MatchRegex(@"\$samples\[0\]", "launch 1 must be treated as the cold-start sample");
        text.Should().Contain("WarmMedianMs", "the summary must report a warm median distinct from the cold start");
        text.Should().Contain("WarmP90Ms", "the summary must report a warm p90 distinct from the cold start");
    }

    [Fact]
    public void RejectsFewerThanTwoLaunches()
    {
        ScriptText().Should().MatchRegex(@"\$Launches\s+-lt\s+2",
            "with fewer than 2 launches there is no warm sample left to take a median/p90 of, so the script must refuse rather than silently produce a meaningless summary");
    }

    [Fact]
    public void TakesASteadyStateSampleInAdditionToTheSettleSample()
    {
        var text = ScriptText();
        text.Should().MatchRegex(@"\[int\]\$SteadySeconds\s*=\s*20",
            "a second, later memory sample must be configurable via -SteadySeconds, defaulting to 20 seconds");
        text.Should().MatchRegex(@"\$SteadySeconds\s+-lt\s+\$SettleSeconds",
            "the steady sample must be validated to occur no earlier than the settle sample");
        text.Should().Contain("Steady",
            "the per-launch sample must record a distinct steady-state reading alongside the original settle-time one");
    }

    [Fact]
    public void RecordsEstablishedTcpConnectionsPerSample()
    {
        var text = ScriptText();
        text.Should().MatchRegex(@"Get-NetTCPConnection\s+-OwningProcess\s+\$ProcessId\s+-State\s+Established",
            "each sample must record established TCP connections owned by the process as a proxy for a live phone session");
        text.Should().Contain("return -1",
            "a failure to query connections must degrade to a -1 sentinel, not fail the whole run");
    }

    [Fact]
    public void LabelsTheWarmColumnAsMaxWhenTheWarmSampleIsSmall()
    {
        var text = ScriptText();
        text.Should().MatchRegex(@"\$warm\.Count\s+-lt\s+10",
            "the summary must switch the warm P90 column's label when there are fewer than 10 warm samples, since a 90th percentile of a small sample is really just the max");
        text.Should().Contain("warm max",
            "the low-sample-count label must read 'warm max' rather than implying a percentile the sample size can't support");
    }

    /// <summary>The body of the top-level <c>finally { ... }</c> block.</summary>
    private static string FinallyBlockBody()
    {
        var match = Regex.Match(ScriptText(), @"(?m)^finally\s*\{\r?\n(.*?)\r?\n^\}\r?$",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("expected a top-level 'finally { ... }' block - re-point this test if the script's shape changed");
        return match.Groups[1].Value;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
