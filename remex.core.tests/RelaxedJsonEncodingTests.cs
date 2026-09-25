using System.Text;
using Remex.Core.Messages;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Perf audit P3-3: <see cref="RemexJsonSerializerContext.Relaxed"/> uses
/// <c>JavaScriptEncoder.UnsafeRelaxedJsonEscaping</c> instead of the default encoder, which escaped
/// every non-ASCII character and several ASCII punctuation marks (including base64's '+') as
/// <c>\uXXXX</c> on every message. RG:396's "same JSON semantics" claim - the only thing this
/// row is allowed to change - is what these tests actually verify, not just "it compiles".
/// </summary>
public class RelaxedJsonEncodingTests
{
    /// <summary>
    /// Review round 1 LOW: <c>Utf8JsonWriter</c>'s OWN <c>JsonWriterOptions.Encoder</c> decides
    /// escaping for <c>RemexJson.SerializeIndented</c>, not the passed <c>typeInfo.Options.Encoder</c>
    /// - so simply passing <c>RemexJsonSerializerContext.Relaxed</c> as the typeInfo argument did
    /// NOTHING for this helper until it was fixed to thread the encoder through explicitly.
    /// </summary>
    [Fact]
    public void SerializeIndentedActuallyAppliesTheRelaxedEncoder()
    {
        var request = new CommandRequest("SHUTDOWN", new Dictionary<string, string> { ["data"] = "a+b/c=" });

        var json = RemexJson.SerializeIndented(request, RemexJsonSerializerContext.Relaxed.CommandRequest);

        Assert.Contains("a+b/c=", json);
        Assert.DoesNotContain(@"\u002B", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RelaxedDoesNotEscapeABase64PlusSign()
    {
        var request = new CommandRequest("SHUTDOWN", new Dictionary<string, string> { ["data"] = "a+b/c=" });

        var json = RemexJson.Serialize(request, RemexJsonSerializerContext.Relaxed.CommandRequest);

        Assert.Contains("a+b/c=", json);
        Assert.DoesNotContain(@"\u002B", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultStillEscapesTheSamePlusSign_ProvingTheDifferenceIsReal()
    {
        // The control: if Default ALSO didn't escape '+', the test above would prove nothing about
        // which encoder is actually in effect.
        var request = new CommandRequest("SHUTDOWN", new Dictionary<string, string> { ["data"] = "a+b/c=" });

        var json = RemexJson.Serialize(request, RemexJsonSerializerContext.Default.CommandRequest);

        Assert.Contains(@"\u002B", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RelaxedDoesNotEscapeNonAsciiText()
    {
        var request = new CommandRequest("SHUTDOWN", new Dictionary<string, string> { ["label"] = "café — 日本語" });

        var json = RemexJson.Serialize(request, RemexJsonSerializerContext.Relaxed.CommandRequest);

        Assert.Contains("café", json);
        Assert.Contains("日本語", json);
    }

    [Fact]
    public void RelaxedAndDefaultDecodeToTheSameValue_SameWireSemanticsDifferentBytes()
    {
        // RG:396's actual claim: the DECODED value is identical regardless of which encoder wrote
        // it. Two different byte representations of the same string is fine; two different strings
        // after round-tripping would mean this row silently changed the protocol.
        var request = new CommandRequest("SHUTDOWN", new Dictionary<string, string> { ["data"] = "a+b/c= café 日本語" });

        var relaxedJson = RemexJson.SerializeToUtf8Bytes(request, RemexJsonSerializerContext.Relaxed.CommandRequest);
        var defaultJson = RemexJson.SerializeToUtf8Bytes(request, RemexJsonSerializerContext.Default.CommandRequest);

        Assert.NotEqual(relaxedJson, defaultJson); // different bytes on the wire...
        var fromRelaxed = RemexJson.Deserialize(relaxedJson, RemexJsonSerializerContext.Default.CommandRequest);
        var fromDefault = RemexJson.Deserialize(defaultJson, RemexJsonSerializerContext.Relaxed.CommandRequest);
        Assert.Equal(fromDefault!.Parameters!["data"], fromRelaxed!.Parameters!["data"]); // ...same decoded value
    }

    /// <summary>
    /// Perf audit P3-4: RemexNetworkListener now deserializes CommandRequest directly from the raw
    /// UTF-8 bytes ReadExactlyAsync produces, and serializes CommandResponse directly to UTF-8 bytes,
    /// instead of round-tripping through a decoded/encoded string. This is the listener's own
    /// deserialize/serialize call shape, exercised directly (no TLS/TCP harness exists for this
    /// listener in this assembly) to prove the byte-based path round-trips correctly.
    /// </summary>
    [Fact]
    public void CommandRequestRoundTripsFromRawUtf8BytesWithoutAStringDecodeStep()
    {
        var original = new CommandRequest("LAUNCHAPP", new Dictionary<string, string> { ["path"] = "C:\\Program Files\\App\\app.exe" }, ClientId: "device-1");
        var wireBytes = RemexJson.SerializeToUtf8Bytes(original, RemexJsonSerializerContext.Relaxed.CommandRequest);

        // What RemexNetworkListener does: ReadExactlyAsync fills a byte[] buffer, then deserializes
        // straight from it - no Encoding.UTF8.GetString step in between.
        var parsed = RemexJson.Deserialize(wireBytes.AsSpan(), RemexJsonSerializerContext.Relaxed.CommandRequest);

        Assert.NotNull(parsed);
        Assert.Equal(original.Action, parsed!.Action);
        Assert.Equal(original.ClientId, parsed.ClientId);
        Assert.Equal(original.Parameters!["path"], parsed.Parameters!["path"]);
    }

    [Fact]
    public void CommandResponseSerializesDirectlyToUtf8BytesWithoutAStringEncodeStep()
    {
        var response = new CommandResponse(true, "OK", null);

        // What RemexNetworkListener does: SerializeToUtf8Bytes straight onto the wire - no
        // Serialize-to-string-then-Encoding.UTF8.GetBytes step in between.
        var bytes = RemexJson.SerializeToUtf8Bytes(response, RemexJsonSerializerContext.Relaxed.CommandResponse);
        var roundTripped = RemexJson.Deserialize(bytes.AsSpan(), RemexJsonSerializerContext.Relaxed.CommandResponse);

        Assert.NotNull(roundTripped);
        Assert.True(roundTripped!.Success);
        Assert.Equal("OK", roundTripped.Message);
    }
}
