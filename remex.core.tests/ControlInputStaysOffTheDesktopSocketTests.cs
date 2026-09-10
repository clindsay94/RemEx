using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Remex.Core.Native;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins that input from a screen with no Remote Desktop stream goes out on the CONTROL socket
/// (RemEx-035d6).
/// </summary>
/// <remarks>
/// <para>
/// THE BUG THESE EXIST FOR PRESENTED AS SILENCE, WHICH IS WHY HALF OF THEM READ SOURCE.
/// <c>HandleDispatchMessage</c> routes by message type, so every <c>desktop_input</c> was claimed by
/// <c>HandleDesktopMessage</c> and pushed out over <c>/ws/desktop</c> by <c>RemexDesktopClient</c>.
/// That is right for the Remote Desktop screen and wrong for the Remote Control screen's media and
/// volume row (RemEx-hulc), which has no stream at all — and wrong in two opposite ways at once:
/// </para>
/// <para>
/// <c>RemexDesktopClient</c> is a process singleton whose stopped-by-request latch (RemEx-yzbb) is
/// set by <c>StopStreamAsync</c> and cleared ONLY by <c>StartStreamAsync</c>.
/// <c>RemoteDesktopViewModel.onCleared</c> stops the stream, so opening the Remote Desktop screen
/// once and navigating away latched it for the life of the process; <c>SendInputAsync</c> then
/// returned before sending and every media key was discarded with no error anywhere. And before the
/// latch is set, that same method AUTO-STARTS a stream when none is running — so the first volume
/// tap on a screen showing no video began a full capture session on the PC.
/// </para>
/// <para>
/// Neither half raises anything. A behavioural test cannot tell the fixed export from the broken one
/// without a real host and a real socket, so what is pinned is the ROUTE: this handler builds its own
/// envelope onto the outbound queue and never mentions the desktop client. The companion Kotlin guard
/// in <c>RemoteDesktopViewModelInputGateTest</c> pins the caller side — that
/// <c>RemoteControlViewModel</c> no longer hand-builds a <c>desktop_input</c> for
/// <c>SendMessage</c> to intercept.
/// </para>
/// <para>
/// The residual gap is honest and worth stating: none of this proves the HOST still routes
/// <c>desktop_input</c> to <c>PingPongHandler.DispatchInput</c>. Deleting that one case would leave
/// every test here green. That is the RemEx-y6x6 shape, and only a round trip from a real phone
/// closes it.
/// </para>
/// </remarks>
public class ControlInputStaysOffTheDesktopSocketTests
{
    [Fact]
    public void AnInputEventIsAcceptedAndReportedAsDispatched()
    {
        var response = AndroidNativeExports.HandleSendControlInput(
            """{"eventType":"keyDown","keyCode":179}""");

        Assert.Contains("\"success\":true", response);
    }

    [Fact]
    public void MissingJsonIsRefusedRatherThanQueuedEmpty()
    {
        Assert.Contains("\"success\":false", AndroidNativeExports.HandleSendControlInput(null));
        Assert.Contains("\"success\":false", AndroidNativeExports.HandleSendControlInput("   "));
    }

    [Fact]
    public void UnparseableJsonIsRefused()
    {
        // Not merely tidiness. The export returns the only answer Kotlin ever sees, and a cheerful
        // one for a payload that never became an InputEvent is the shape RemEx-66rf had to fix on the
        // command path: a well-formed success that means nothing happened.
        Assert.Contains("\"success\":false", AndroidNativeExports.HandleSendControlInput("not json"));
    }

    [Fact]
    public void SendingControlInputDoesNotTouchTheRemoteDesktopClient()
    {
        // The observable half of the routing claim. RemexDesktopClient is a process singleton and is
        // never connected in this assembly, so the meaningful assertion is that this call had no
        // interest in it — no socket opened, nothing started.
        AndroidNativeExports.HandleSendControlInput("""{"eventType":"keyUp","keyCode":179}""");

        Assert.False(
            RemexDesktopClient.Current.IsConnected,
            "control-socket input must not open the Remote Desktop socket");
    }

    [Fact]
    public void TheHandlerBuildsItsOwnEnvelopeAndNeverReachesForTheDesktopClient()
    {
        var body = HandlerBody("HandleSendControlInput");

        Assert.DoesNotContain("RemexDesktopClient", body);
        Assert.DoesNotContain("QueueDesktopWork", body);

        // AND IT MUST STILL SEND desktop_input. The type is unchanged on the wire on purpose: the
        // host has always handled it on this socket (PingPongHandler.DispatchInput), so the fix
        // needed no new message type, no protocolVersion bump and no new client-bound type for the
        // inbound router to drop.
        Assert.Contains("MessageTypes.DesktopInput", body);
        Assert.Contains("OutboundMessageQueue", body);
    }

    [Fact]
    public void TheExportTakesAnInputEventRatherThanAnEnvelope()
    {
        var body = HandlerBody("HandleSendControlInput");

        // THE NARROW ARGUMENT IS THE GUARANTEE. An envelope-shaped export would be
        // HandleDispatchMessage with the routing switch removed, and the next caller to reach for it
        // would put something other than input on this path. Parsing an InputEvent — never a
        // RemexMessage — means this entry point can only ever send input, and only ever on the
        // control socket, whatever the caller passes.
        Assert.Contains("RemexJsonSerializerContext.Default.InputEvent", body);
        Assert.DoesNotContain("RemexJsonSerializerContext.Default.RemexMessage", body);
    }

    /// <summary>
    /// The source text of one handler, from its declaration to the start of the next member, with
    /// comments stripped.
    /// </summary>
    /// <remarks>
    /// Comments have to go: this handler's own doc names <c>RemexDesktopClient</c> repeatedly, in the
    /// course of explaining why the code must not touch it. A guard that reads the explanation as the
    /// violation is worse than no guard. Same reasoning and same shape as
    /// <c>NativeExportOutcomeTests.HandlerBody</c>.
    /// </remarks>
    private static string HandlerBody(string handler)
    {
        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "remex.core", "Native", "AndroidNativeExports.cs"));

        var declaration = Regex.Match(
            source,
            $@"(private|internal|public)\s+static\s+\w+\??\s+{Regex.Escape(handler)}\s*\(");
        Assert.True(
            declaration.Success,
            $"could not find the declaration of {handler} in AndroidNativeExports.cs — has it been renamed?");

        var afterSignature = declaration.Index + declaration.Length;
        var next = Regex.Match(source[afterSignature..], @"\n\s*(private|public|internal)\s+static\s");
        var end = next.Success ? afterSignature + next.Index : source.Length;

        var body = Regex.Replace(source[declaration.Index..end], @"/\*[\s\S]*?\*/", string.Empty);
        body = Regex.Replace(body, @"//.*", string.Empty);

        Assert.True(body.Trim().Length > 0, $"{handler} resolved to an empty body");
        return body;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
