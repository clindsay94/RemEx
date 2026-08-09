using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Serialization;
using Remex.Core.Validation;

namespace Remex.Core.Tests;

/// <summary>
/// <c>clipboard_push</c> survives the wire, in both directions (RemEx-hgqs).
/// </summary>
/// <remarks>
/// <para>
/// **THE PAYLOAD IS SERIALIZED TWICE ON ITS WAY OUT AND THAT IS WHERE IT CAN GO MISSING.** Android
/// sends this type as a JSON string through <c>SendMessageNative</c>; the native side deserializes
/// it into a <see cref="RemexMessage"/>, queues the OBJECT, and re-serializes it on the way to the
/// host (<c>AndroidNativeExports.ProcessOutboundMessagesAsync</c>). Anything the envelope drops in
/// that round trip — a slot that was never added, a <c>JsonPropertyName</c> that does not match what
/// Kotlin writes — does not throw. It arrives as <c>null</c>, the host's <c>when … is not null</c>
/// guard declines to match, and the push silently does nothing on a build that compiles and a suite
/// that passes.
/// </para>
/// <para>
/// MEASURED, NOT ASSUMED: removing this type's <c>[JsonSerializable]</c> line from
/// <c>RemexJsonSerializerContext</c> does NOT break these tests. The source generator reaches it
/// transitively through <see cref="RemexMessage"/>, so that registration is convention rather than
/// load-bearing, and a test claiming to guard it would be guarding nothing. What DOES break them is
/// changing the wire name — which is the thing worth pinning, and what
/// <see cref="TheJsonKotlinActuallyWritesDeserializes"/> is for.
/// </para>
/// <para>
/// This is the same shape of failure as RemEx-y6x6, one layer down: there, a host → client type was
/// dropped by the JNI router; here, a payload would be dropped by the serializer. Both present as
/// "the feature does nothing" with no error on either side.
/// </para>
/// </remarks>
public class ClipboardPushWireTests
{
    private static string Serialize(RemexMessage m) =>
        RemexJson.Serialize(m, RemexJsonSerializerContext.Default.RemexMessage);

    private static RemexMessage? Deserialize(string json) =>
        RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.RemexMessage);

    [Fact]
    public void ThePayloadSurvivesTheNativeQueueRoundTrip()
    {
        // Deserialize-then-reserialize is exactly what the native send path does to every outbound
        // message, so this is the real journey, not a proxy for it.
        var sent = new RemexMessage
        {
            Type = MessageTypes.ClipboardPush,
            ClipboardPush = new ClipboardPush { Text = "ssh user@host -p 2222" },
        };

        var arrived = Deserialize(Serialize(Deserialize(Serialize(sent))!));

        Assert.Equal(MessageTypes.ClipboardPush, arrived!.Type);
        Assert.Equal("ssh user@host -p 2222", arrived.ClipboardPush!.Text);
    }

    [Fact]
    public void TheJsonKotlinActuallyWritesDeserializes()
    {
        // Hand-written to match what the Android sender builds, because the round trip above would
        // pass just as happily on a property name only this codebase agrees with. The wire name is
        // the contract with the Kotlin side and nothing else in the build checks it.
        var arrived = Deserialize(
            """{"type":"clipboard_push","protocolVersion":2,"clipboardPush":{"text":"hello"}}""");

        Assert.NotNull(arrived?.ClipboardPush);
        Assert.Equal("hello", arrived.ClipboardPush.Text);

        // AND THE TYPE STRING ITSELF, which nothing else pins. Every other assertion here uses
        // MessageTypes.ClipboardPush on both sides, so they round-trip whatever the constant happens
        // to say - change it to anything at all and they stay green while the host's
        // `case MessageTypes.ClipboardPush` quietly stops matching what Kotlin sends.
        Assert.Equal(MessageTypes.ClipboardPush, arrived.Type);
    }

    [Fact]
    public void AMessageWithoutTheSlotLeavesItNull()
    {
        // The host case is `when message.ClipboardPush is not null`, so this null is what makes a
        // malformed or truncated push decline to match rather than write an empty clipboard - which
        // is the one outcome the validation rules single out as destructive.
        Assert.Null(Deserialize("""{"type":"clipboard_push","protocolVersion":2}""")!.ClipboardPush);
    }

    [Fact]
    public void EveryOtherSlotStaysNullWhenAClipboardPushIsSent()
    {
        // ONE ENVELOPE, MANY PAYLOADS - a message carries exactly one and leaves the rest null, as
        // RemexMessage's own doc states. Worth asserting once for a newly added slot: a payload that
        // arrived with, say, CommandAction populated would be routed by an earlier case in the
        // host's switch and never reach the clipboard one.
        var arrived = Deserialize(Serialize(new RemexMessage
        {
            Type = MessageTypes.ClipboardPush,
            ClipboardPush = new ClipboardPush { Text = "x" },
        }))!;

        Assert.Null(arrived.CommandAction);
        Assert.Null(arrived.InputEvent);
        Assert.Null(arrived.FilePushOffer);
        Assert.Null(arrived.Telemetry);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void TheValidationTheHostAppliesRefusesAnEmptyPush(string? text)
    {
        // Not a restatement of ClipboardValidationTests - it pins the rule the HOST case depends on.
        // An empty push that reached the clipboard would CLEAR the PC's, destroying something the
        // user deliberately put there with nothing to tell them the button did it.
        Assert.Equal(ClipboardRejectReason.Empty, ClipboardValidation.Validate(text, out var bytes));
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void WhitespaceOnlyIsContentAndIsAccepted()
    {
        // Someone who copied an indented code block copied the whitespace on purpose.
        Assert.Equal(ClipboardRejectReason.None, ClipboardValidation.Validate("    \n\t", out var bytes));
        Assert.Equal(6, bytes);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("hello", "none")]
    public void TheNativeVerdictUsesTheExactSpellingsKotlinParses(string text, string expected)
    {
        // THE CONTRACT WITH THE KOTLIN PARSER, AND IT WAS UNREACHABLE UNTIL IT MOVED. It used to be
        // built inline in an [UnmanagedCallersOnly] export, which no managed test can call - so
        // renaming "too_large" to "tooLarge" would compile, ship, and stop the phone recognising an
        // oversize payload, discoverable only from a device.
        Assert.Contains($"\"reason\":\"{expected}\"", ClipboardValidation.ToNativeJson(text));
        Assert.Contains($"\"maxBytes\":{ClipboardValidation.MaxPayloadBytes}", ClipboardValidation.ToNativeJson(text));
    }

    [Fact]
    public void AnOversizePayloadIsSpelledTooLarge()
    {
        Assert.Contains(
            "\"reason\":\"too_large\"",
            ClipboardValidation.ToNativeJson(new string('a', ClipboardValidation.MaxPayloadBytes + 1)));
    }

    [Fact]
    public void TheExportFailureAnswerHasTheSAMESHAPEAsAVerdict()
    {
        // Kotlin wraps an export failure in a SUCCESSFUL Result, so a failure missing "reason"
        // entirely would leave the phone's behaviour to its parser's default - and the fail-open
        // direction sends an unbounded payload. Both answers must be parseable the same way.
        var failure = ClipboardValidation.UnavailableNativeJson();

        Assert.Contains("\"reason\":\"unavailable\"", failure);
        Assert.Contains("\"maxBytes\":", failure);
        Assert.Contains("\"byteCount\":", failure);
        Assert.DoesNotContain("none", failure);
    }
}
