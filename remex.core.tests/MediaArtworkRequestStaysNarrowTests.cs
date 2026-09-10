using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Remex.Core.Native;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins that the artwork export can only ever ask for artwork (RemEx-vtorl).
/// </summary>
/// <remarks>
/// <para>
/// THE GUARANTEE IS THE NARROW ARGUMENT, AND IT IS WORTH A TEST BECAUSE IT IS SO EASY TO WIDEN. This
/// entry point takes an id string and builds its own envelope; the caller cannot choose the type. An
/// envelope-shaped version would be <c>HandleDispatchMessage</c> with the routing switch removed, and
/// the next person who needs to send something from Kotlin would reach for it rather than adding
/// their own export — which is how a single-purpose door becomes a general one that nothing
/// downstream is expecting. Same shape and same reasoning as
/// <c>ControlInputStaysOffTheDesktopSocketTests</c>, which pins the sibling export next door.
/// </para>
/// <para>
/// The rest is source reading for the same reason that one reads source: whether the message reaches
/// the host cannot be observed from this assembly, so what is pinned is the ROUTE — an envelope of
/// the right type onto the outbound control queue, and no detour through the Remote Desktop client.
/// </para>
/// </remarks>
public class MediaArtworkRequestStaysNarrowTests
{
    [Fact]
    public void AnArtworkIdIsAcceptedAndReportedAsDispatched()
    {
        var response = AndroidNativeExports.HandleRequestMediaArtwork("0f1e2d3c4b5a6978");

        Assert.Contains("\"success\":true", response);
    }

    [Fact]
    public void AMissingIdIsRefusedRatherThanAskingForNothing()
    {
        // A blank id would serialize into a perfectly well-formed request the host can only answer
        // with an empty reply, so the phone would wait for a cover that was never coming and the only
        // record of the mistake would be a cheerful success on this side. Refusing here is the only
        // place the caller can be told.
        Assert.Contains("\"success\":false", AndroidNativeExports.HandleRequestMediaArtwork(null));
        Assert.Contains("\"success\":false", AndroidNativeExports.HandleRequestMediaArtwork(string.Empty));
        Assert.Contains("\"success\":false", AndroidNativeExports.HandleRequestMediaArtwork("   "));
    }

    [Fact]
    public void TheHandlerQueuesOneArtworkRequestAndNothingElse()
    {
        var body = HandlerBody("HandleRequestMediaArtwork");

        Assert.Contains("MessageTypes.MediaArtworkRequest", body);
        Assert.Contains("OutboundMessageQueue", body);

        // Exactly one write. A second TryWrite here would be a retry or a companion message, and
        // either turns one phone tap into two envelopes on a socket that is also carrying input.
        Assert.Single(Regex.Matches(body, @"OutboundMessageQueue\.Writer\.TryWrite"));

        // And it is not the Remote Desktop path. This export exists on the control socket precisely
        // so that asking for a cover cannot start a screen capture, which is the mistake RemEx-035d6
        // had to undo for media keys.
        Assert.DoesNotContain("RemexDesktopClient", body);
        Assert.DoesNotContain("QueueDesktopWork", body);
    }

    [Fact]
    public void TheExportTakesAnIdRatherThanAnEnvelope()
    {
        var body = HandlerBody("HandleRequestMediaArtwork");

        // It constructs the payload rather than deserializing one, so there is no reachable shape in
        // which a caller-supplied type reaches the queue.
        Assert.Contains("new MediaArtworkRequest", body);
        Assert.DoesNotContain("RemexJsonSerializerContext.Default.RemexMessage", body);
        Assert.DoesNotContain("Deserialize", body);
    }

    /// <summary>
    /// The source text of one handler, from its declaration to the start of the next member, with
    /// comments stripped.
    /// </summary>
    /// <remarks>
    /// Comments have to go: the handler's own doc names the things it must not do, at length, in the
    /// course of explaining why. A guard that reads the explanation as the violation is worse than no
    /// guard. Same shape as <c>ControlInputStaysOffTheDesktopSocketTests.HandlerBody</c>.
    /// </remarks>
    private static string HandlerBody(string handler)
    {
        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "remex.core", "Native", "AndroidNativeExports.cs"));

        var declaration = Regex.Match(
            source,
            $@"internal static string {Regex.Escape(handler)}\s*\(");
        Assert.True(declaration.Success, $"{handler} was renamed or removed");

        var rest = source[declaration.Index..];
        var end = rest.IndexOf("\n    /// <summary>", 1, System.StringComparison.Ordinal);
        var body = end > 0 ? rest[..end] : rest;

        return Regex.Replace(
            Regex.Replace(body, @"/\*[\s\S]*?\*/", string.Empty),
            @"//.*",
            string.Empty);
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
