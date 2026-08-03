using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins that the JNI exports which claim an outcome actually wait for one (RemEx-66rf, RemEx-52n0).
/// </summary>
/// <remarks>
/// <para>
/// Two exports independently grew the same shape: fire the real work into a discarded
/// <c>Task.Run</c>, then return a hard-coded success. <c>HandleDispatchCommand</c> answered
/// <c>"Command dispatched."</c> for every command ever sent, so a dropped socket and a command that
/// failed on the PC were indistinguishable from one that worked. <c>HandleSendWakeOnLan</c> did the
/// same with the magic packet.
/// </para>
/// <para>
/// BOTH CARRIED A COMMENT EXPLAINING WHY THAT WAS FINE, AND BOTH EXPLANATIONS WERE INVENTED. One
/// said outcomes reached Kotlin "via the RegisterCallbackNative callbacks"; there is no command
/// callback. The other said failures were surfaced by something observing
/// <c>ConnectionStateChanged</c>, which has nothing to do with a UDP send. Kotlin had meanwhile been
/// written to trust the flag: <c>TaskManagerViewModel</c> branched on it, and
/// <c>DashboardViewModel</c> carried a comment stating that a failed wake "now surfaces as a
/// failure". Those branches were correct code reading a constant.
/// </para>
/// <para>
/// This is a source guard because the failure is invisible at runtime — that is the entire defect.
/// Both exports return a well-formed, cheerful response whether or not anything worked, so no
/// behavioural test can tell the fixed version from the broken one without a real host, a real
/// socket, and a way to make them fail. What CAN be pinned is that the result is not thrown away.
/// </para>
/// <para>
/// The companion half is already covered behaviourally: <c>WakeOnLanServiceTests</c> pins that
/// <c>WakeAsync</c> THROWS on a malformed MAC and an unparseable broadcast address, which is what
/// gives the awaited call something to report. Together they close the chain — the service raises,
/// the export waits, the flag means something.
/// </para>
/// </remarks>
public class NativeExportOutcomeTests
{
    /// <summary>Handlers that must await their work rather than discarding it.</summary>
    /// <remarks>
    /// Deliberately NOT every <c>Task.Run</c> in the file. The two others are legitimate and a
    /// blanket ban would be wrong: <c>HandleInitialize</c> starts a connection whose outcome genuinely
    /// does arrive via <c>ConnectionStateChanged</c> and <c>ConnectionFailed</c>, both wired to JNI
    /// callbacks, and <c>EnsureOutboundSendLoopStarted</c> launches a long-running pump that has no
    /// per-call result to wait for. Naming the two handlers that make a CLAIM keeps the guard about
    /// the claim rather than about the syntax.
    /// </remarks>
    public static TheoryData<string> ClaimingHandlers =>
    [
        "HandleDispatchCommand",
        "HandleSendWakeOnLan",
    ];

    [Theory]
    [MemberData(nameof(ClaimingHandlers))]
    public void AHandlerThatReportsAnOutcomeDoesNotDiscardTheWorkThatProducesIt(string handler)
    {
        var body = HandlerBody(handler);

        Assert.False(
            Regex.IsMatch(body, @"_\s*=\s*Task\.Run"),
            $"{handler} discards the task that does its work, so whatever it returns cannot depend on "
            + "the outcome. Both exports shipped this way for a long time behind comments claiming the "
            + "result reached Kotlin by some other route; neither route existed, and Kotlin was "
            + "branching on a flag that was always true. Await it (see the GetAwaiter().GetResult() "
            + "shape already used by FetchPairingPinNative) and return what actually happened.");
    }

    [Theory]
    [MemberData(nameof(ClaimingHandlers))]
    public void AHandlerReportsFailureFromTheWorkItself_NotOnlyFromItsArgumentChecks(string handler)
    {
        // **REVIEW CAUGHT THE FIRST VERSION OF THIS BEING VACUOUS.** It asked only whether a failure
        // return appeared anywhere in the body — and both handlers have always had an argument guard
        // near the top ("Wake-on-LAN requires a MAC address.", "Command JSON is required.") that
        // satisfied that. It therefore passed on the exact pre-fix code it was written to reject,
        // leaving the discard check doing all the work.
        //
        // What distinguishes the fixed shape is that failure is reported from DOWNSTREAM OF THE
        // AWAITED WORK — i.e. out of a catch around it. An argument check tells the caller it asked
        // wrongly; only this tells the caller the thing it asked for did not happen.
        var body = HandlerBody(handler);

        Assert.True(
            Regex.IsMatch(
                body,
                @"catch\s*\(\s*\w*Exception[\s\S]*?(SerializeOperationFailure|CommandResponse\(\s*false|Success\s*=\s*false)"),
            $"{handler} has no failure report downstream of the work it awaits. A guard that rejects "
            + "bad arguments before starting is not the same as telling the caller the work failed — "
            + "and the version of this test that could not tell them apart passed on the fire-and-"
            + "forget code this guard exists to reject.");
    }

    /// <summary>
    /// The source text of one handler, from its signature to the start of the next member.
    /// </summary>
    /// <remarks>
    /// Bounded by the next <c>private static</c>/<c>public static</c> declaration rather than by
    /// brace matching, which string literals and comments in this file would break. Fails loudly if
    /// the handler is renamed or moved, which is the right outcome: a guard that silently stops
    /// finding its target is worse than one that breaks.
    /// </remarks>
    private static string HandlerBody(string handler)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.core", "Native", "AndroidNativeExports.cs"));

        // THE DECLARATION, NOT THE FIRST MENTION. A plain IndexOf($" {handler}(") matches the CALL
        // inside the UnmanagedCallersOnly entry point several hundred lines earlier — which bounded
        // the "body" to a few lines of the dispatcher and made the discard check pass without ever
        // reading the handler. It was the failure-path check that exposed it, by failing on code that
        // does have a failure path.
        var declaration = Regex.Match(
            source,
            $@"(private|internal|public)\s+static\s+\w+\??\s+{Regex.Escape(handler)}\s*\(");
        Assert.True(
            declaration.Success,
            $"could not find the declaration of {handler} in AndroidNativeExports.cs — has it been renamed?");

        var start = declaration.Index;

        // Bounded by the NEXT such declaration, skipping this one.
        var afterSignature = start + declaration.Length;
        var next = Regex.Match(source[afterSignature..], @"\n\s*(private|public|internal)\s+static\s");
        var end = next.Success ? afterSignature + next.Index : source.Length;

        // Comments stripped, as the Kotlin sibling guard does. The window runs to the next member, so
        // it picks up that member's doc comment — and these handlers are heavily commented ABOUT the
        // very strings being matched. A comment mentioning SerializeOperationFailure must not stand in
        // for one.
        var body = Regex.Replace(source[start..end], @"/\*[\s\S]*?\*/", string.Empty);
        body = Regex.Replace(body, @"//.*", string.Empty);

        Assert.True(body.Trim().Length > 0, $"{handler} resolved to an empty body");
        return body;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
