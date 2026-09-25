using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Source-level guard that every capture-loop backoff in <c>RemoteDesktopHandler</c> calls
/// <c>pacer.Reset()</c> AFTER its <c>Task.Delay</c>, not before it.
/// </summary>
/// <remarks>
/// <para>
/// WHY A SOURCE SCAN. The defect is call-site ORDERING, not pacer logic: <c>PrecisionPacer.Reset()</c>
/// is correct either way, so any behavioural test of the pacer in isolation passes identically before
/// and after the fix. Reproducing it through the handler means a live capture service, a WebSocket
/// pair and a 500 ms backoff window per assertion — and the observable symptom is a burst of frames
/// that a fake capture service produces just as happily when the ordering is right.
/// </para>
/// <para>
/// WHAT IT CATCHES is the realistic slip, which is the one that was actually shipped: writing
/// <c>Reset(); await Task.Delay(...)</c> because that reads as "reset, then pause". <c>Reset()</c>
/// anchors <c>_nextTickMs</c> to the CLOCK AT THE MOMENT OF THE CALL, so resetting first leaves the
/// timeline 500 ms in the past by the time the pause ends. <c>WaitForNextTickAsync</c> only steps
/// <c>_nextTickMs += interval</c> and never re-anchors, so recovery returns a zero wait once per
/// missed tick — ~60 of them at 120 FPS — bursting a full capture-and-encode backlog onto the wire.
/// That is precisely the failure the PrecisionPacer entry in <c>docs/REGRESSION-GUARDS.md</c> names
/// <c>Reset()</c> as the fix for, and the comment at the second site already claimed it was prevented
/// while the code did the opposite.
/// </para>
/// <para>
/// KNOWN LIMIT: it scans one file for one shape (a <c>Task.Delay(..., ct)</c> backoff paired with a
/// <c>pacer.Reset()</c>). A backoff written in a new file, or one that pauses by some other means, is
/// invisible to it. The anti-vacuity assertion below is what stops it from passing by finding
/// nothing.
/// </para>
/// </remarks>
public class PacerResetOrderingTests
{
    private static readonly string HandlerRelativePath =
        Path.Combine("remex.agent", "Handlers", "RemoteDesktopHandler.cs");

    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));

    private static string HandlerSourceWithoutComments()
    {
        var path = Path.Combine(RepoRoot(), HandlerRelativePath);
        Assert.True(File.Exists(path), $"expected the remote-desktop handler at {path}");

        // Comments are stripped first: both fixed sites now DOCUMENT the banned ordering in prose, so
        // a scan that reads its own explanation would fail on the very code it is guarding.
        var source = File.ReadAllText(path);
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        source = Regex.Replace(source, @"//[^\n]*", string.Empty);
        return source;
    }

    /// <summary>Every <c>pacer.Reset()</c> in the handler, paired with the code just above it.</summary>
    private const string ResetToken = "pacer.Reset();";

    [Fact]
    public void EveryBackoffResetsThePacerAfterItsDelay_NotBefore()
    {
        var source = HandlerSourceWithoutComments();

        // The banned shape: a Reset() whose very next statement is the backoff await. Nothing but
        // whitespace between them, because that is what the defect looked like — the pair was written
        // adjacent, in the wrong order, at both sites.
        var inverted = Regex.Matches(
            source,
            Regex.Escape(ResetToken) + @"\s*try\s*\{\s*await\s+Task\.Delay\(");

        Assert.True(
            inverted.Count == 0,
            $"{inverted.Count} capture-loop backoff(s) call pacer.Reset() BEFORE awaiting Task.Delay. "
            + "Reset() anchors the absolute timeline to the moment it is called, so the pacer ends the "
            + "pause anchored in the past and WaitForNextTickAsync returns a zero wait once per missed "
            + "tick — the recovery burst the PrecisionPacer regression guard exists to prevent. Move "
            + "the Reset() below the await.");
    }

    [Fact]
    public void TheBackoffsStillResetThePacerAtAll()
    {
        // The complement, and the anti-vacuity half: banning the inverted ordering is worthless if
        // someone satisfies it by deleting Reset() outright, which reinstates the SAME burst by a
        // different route — an un-reset absolute timeline is exactly a timeline anchored 500 ms in the
        // past. Asserted on the count so a site that loses its Reset() fails even while the other keeps
        // this green.
        var source = HandlerSourceWithoutComments();

        var backoffs = Regex.Matches(source, @"await\s+Task\.Delay\(500,\s*ct\)").Count;
        var resets = Regex.Matches(source, Regex.Escape(ResetToken)).Count;

        Assert.True(backoffs > 0, "found no 500 ms capture backoff at all — this scan has gone blind");
        Assert.True(
            resets >= backoffs,
            $"{backoffs} capture-loop backoff(s) but only {resets} pacer.Reset() call(s): a backoff "
            + "that never re-anchors the pacer bursts through every tick it slept past.");
    }

    [Fact]
    public void EachResetFollowsAnAwaitedDelay()
    {
        // Pins the positive shape rather than only forbidding the negative one, so a Reset() moved
        // somewhere unrelated (before the loop, after the send) does not pass by absence. Each
        // occurrence must have an awaited Task.Delay and its cancellation catch above it, with nothing
        // but the catch in between.
        var source = HandlerSourceWithoutComments();

        var correct = Regex.Matches(
            source,
            @"await\s+Task\.Delay\(500,\s*ct\);\s*\}\s*catch\s*\(OperationCanceledException\)\s*\{\s*break;\s*\}\s*"
            + Regex.Escape(ResetToken));

        var resets = Regex.Matches(source, Regex.Escape(ResetToken)).Count;

        Assert.True(resets > 0, "found no pacer.Reset() at all — this scan has gone blind");
        Assert.Equal(resets, correct.Count);
    }

    [Fact]
    public void TheCursorLoopSharesTheDisplayOffPause_WithResetAfterTheDelay()
    {
        // P1-11: the cursor loop used to keep spinning its own 90 Hz pacer while the capture loop sat
        // paused on a powered-off display. The file-wide counts above would still pass if that pause
        // were deleted again (both totals drop together), so pin it to this method specifically.
        var source = HandlerSourceWithoutComments();

        var start = source.IndexOf("Task StreamCursorPositionAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "found no StreamCursorPositionAsync — this scan has gone blind");
        var end = source.IndexOf("Task ReceiveInputLoopAsync(", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not find the end of StreamCursorPositionAsync");
        var cursorLoop = source[start..end];

        Assert.Matches(
            new Regex(
                @"if\s*\(\s*_screenCapture\.IsDisplayPoweredOff\s*\)\s*\{\s*"
                + @"try\s*\{\s*await\s+Task\.Delay\(500,\s*ct\);\s*\}\s*catch\s*\(OperationCanceledException\)\s*\{\s*break;\s*\}\s*"
                + Regex.Escape(ResetToken)),
            cursorLoop);
    }
}
