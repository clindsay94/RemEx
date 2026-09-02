using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Remex.Core.Native;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins that the seek export can only ever move the position of the track already playing
/// (RemEx-vtorl).
/// </summary>
/// <remarks>
/// <para>
/// THE GUARANTEE IS THE NARROW ARGUMENT, exactly as it is for the artwork export next door — see
/// <c>MediaArtworkRequestStaysNarrowTests</c> for the full reasoning. A seek is a number, so an
/// envelope-shaped version of this door would be even more tempting to reuse than that one: the
/// payload is trivial and the type would be the only thing a caller had to change.
/// </para>
/// <para>
/// The rest is source reading for the same reason: whether the message reaches the host cannot be
/// observed from this assembly, so what is pinned is the ROUTE — one envelope of the right type onto
/// the outbound control queue, and no detour through the Remote Desktop client.
/// </para>
/// </remarks>
public class MediaSeekStaysNarrowTests
{
    [Fact]
    public void APositionIsAcceptedAndReportedAsDispatched()
    {
        Assert.Contains("\"success\":true", AndroidNativeExports.HandleSeekMedia(1234));
        Assert.Contains("\"success\":true", AndroidNativeExports.HandleSeekMedia(0));
    }

    [Fact]
    public void ANegativePositionIsRefusedRatherThanRestartingTheTrack()
    {
        // THE ONE THAT WOULD DESTROY SOMETHING QUIETLY. Clamping a negative to zero would jump the
        // user's track back to the beginning and report success, so a unit mistake on the phone —
        // microseconds where milliseconds were wanted — would present as playback restarting with
        // nothing anywhere saying why. Refusing is the only place the caller can be told.
        Assert.Contains("\"success\":false", AndroidNativeExports.HandleSeekMedia(-1));
        Assert.Contains("\"success\":false", AndroidNativeExports.HandleSeekMedia(long.MinValue));
    }

    [Fact]
    public void TheHandlerQueuesOneSeekAndNothingElse()
    {
        var body = HandlerBody("HandleSeekMedia");

        Assert.Contains("MessageTypes.MediaSeek", body);
        Assert.Contains("OutboundMessageQueue", body);

        // Exactly one write. A second TryWrite here would be a retry or a companion message, and
        // either turns one drag of the scrubber into two envelopes on a socket that is also carrying
        // input — while a scrubber emits one of these per gesture already.
        Assert.Single(Regex.Matches(body, @"OutboundMessageQueue\.Writer\.TryWrite"));

        // And it is not the Remote Desktop path, for the reason RemEx-035d6 had to undo for media
        // keys: moving a progress bar must not be able to start a screen capture on the PC.
        Assert.DoesNotContain("RemexDesktopClient", body);
        Assert.DoesNotContain("QueueDesktopWork", body);
    }

    [Fact]
    public void TheExportTakesANumberRatherThanAnEnvelope()
    {
        var source = ExportsSource();

        // The signature itself, not just the handler: a widened export could keep this handler
        // untouched and add a string overload beside it.
        Assert.Matches(
            new Regex(
                @"EntryPoint\s*=\s*""Java_com_clindsay94_remex_RemexCoreClient_SeekMediaNative""\s*\)\]\s*"
                + @"public static IntPtr SeekMedia\(IntPtr env, IntPtr thiz, long positionMs\)"),
            source);

        var body = HandlerBody("HandleSeekMedia");

        // It constructs the payload rather than deserializing one, so there is no reachable shape in
        // which a caller-supplied type reaches the queue.
        Assert.Contains("new MediaSeekRequest", body);
        Assert.DoesNotContain("RemexJsonSerializerContext.Default.RemexMessage", body);
        Assert.DoesNotContain("Deserialize", body);
        Assert.DoesNotContain("ReadJString", body);
    }

    /// <summary>
    /// The source text of one handler, from its declaration to the start of the next member, with
    /// comments stripped.
    /// </summary>
    /// <remarks>
    /// Comments have to go: the handler's own doc names the things it must not do, at length, in the
    /// course of explaining why. A guard that reads the explanation as the violation is worse than no
    /// guard. Same shape as <c>MediaArtworkRequestStaysNarrowTests.HandlerBody</c>.
    /// </remarks>
    private static string HandlerBody(string handler)
    {
        var source = ExportsSource();

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

    private static string ExportsSource() => File.ReadAllText(
        Path.Combine(RepoRoot(), "remex.core", "Native", "AndroidNativeExports.cs"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
