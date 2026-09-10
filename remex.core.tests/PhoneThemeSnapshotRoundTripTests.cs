using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Serialization;

namespace Remex.Core.Tests;

/// <summary>
/// <c>theme_sync</c> survives the wire through the source-generated
/// <see cref="RemexJsonSerializerContext"/> (RemEx-y06a0.1, RemEx-sudp8).
/// </summary>
/// <remarks>
/// <see cref="TheJsonThePhoneActuallySendsDeserializes"/> is hand-written against RemEx-y06a0.1's
/// real envelope (<c>ThemeSync.buildEnvelope</c> in <c>remex.android</c>) rather than only
/// round-tripping this codebase's own serializer against itself — the same reason
/// <c>ClipboardPushWireTests</c> keeps one hand-written case: a round trip that only compares this
/// side to itself would pass just as happily on a property name only the PC agrees with.
/// </remarks>
public class PhoneThemeSnapshotRoundTripTests
{
    private static string Serialize(RemexMessage m) =>
        RemexJson.Serialize(m, RemexJsonSerializerContext.Default.RemexMessage);

    private static RemexMessage? Deserialize(string json) =>
        RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.RemexMessage);

    private static PhoneThemeSnapshot Sample() => new()
    {
        SeedHex = "#6750A4",
        Style = "tonal_spot",
        Mode = "dark",
        Contrast = 0.5,
        DynamicColor = true,
        SentAtUnixMs = 1_700_000_000_000,
    };

    [Fact]
    public void EveryFieldSurvivesTheRoundTrip()
    {
        var sent = new RemexMessage { Type = MessageTypes.ThemeSync, ThemeSync = Sample() };

        var arrived = Deserialize(Serialize(sent));

        Assert.Equal(MessageTypes.ThemeSync, arrived!.Type);
        var sync = arrived.ThemeSync;
        Assert.NotNull(sync);
        Assert.Equal("#6750A4", sync!.SeedHex);
        Assert.Equal("tonal_spot", sync.Style);
        Assert.Equal("dark", sync.Mode);
        Assert.Equal(0.5, sync.Contrast);
        Assert.True(sync.DynamicColor);
        Assert.Equal(1_700_000_000_000, sync.SentAtUnixMs);
    }

    [Fact]
    public void TheJsonThePhoneActuallySendsDeserializes()
    {
        // Matches RemEx-y06a0.1's ThemeSync.buildEnvelope verbatim: nested under "themeSync", the
        // wire names are "seed" and "dynamic" (not SeedHex/DynamicColor's own camelCase), and there
        // is no protocolVersion field.
        var arrived = Deserialize(
            """
            {"type":"theme_sync","themeSync":{"seed":"#6750A4","style":"tonal_spot","mode":"dark","contrast":0.5,"dynamic":true,"sentAtUnixMs":1700000000000}}
            """);

        Assert.NotNull(arrived?.ThemeSync);
        var sync = arrived!.ThemeSync!;
        Assert.Equal("#6750A4", sync.SeedHex);
        Assert.Equal("tonal_spot", sync.Style);
        Assert.Equal("dark", sync.Mode);
        Assert.Equal(0.5, sync.Contrast);
        Assert.True(sync.DynamicColor);
        Assert.Equal(1_700_000_000_000, sync.SentAtUnixMs);
        Assert.Equal(MessageTypes.ThemeSync, arrived.Type);
    }

    [Fact]
    public void AMessageWithoutTheSlotLeavesItNull()
    {
        // The host case is `when message.ThemeSync is not null`, so a truncated or malformed sync
        // declines to match rather than being adopted with default/zeroed fields.
        Assert.Null(Deserialize("""{"type":"theme_sync"}""")!.ThemeSync);
    }

    [Fact]
    public void EveryOtherSlotStaysNullWhenAThemeSyncIsSent()
    {
        // ONE ENVELOPE, MANY PAYLOADS - a message carries exactly one and leaves the rest null.
        var arrived = Deserialize(Serialize(new RemexMessage
        {
            Type = MessageTypes.ThemeSync,
            ThemeSync = Sample(),
        }))!;

        Assert.Null(arrived.CommandAction);
        Assert.Null(arrived.MediaSeek);
        Assert.Null(arrived.ClipboardPush);
    }

    [Fact]
    public void AThemeSyncMissingAFieldDeserializesInsteadOfDroppingTheWholeMessage()
    {
        // RemEx-sudp8 fix round (Opus review, HIGH). No wire field on PhoneThemeSnapshot is
        // `required` any more: a `required` member missing from the JSON makes System.Text.Json
        // throw, MessageSerializer.Deserialize turns that into a null RemexMessage, and a null
        // message makes PingPongHandler's receive loop treat it as a disconnect - dropping the
        // WHOLE control session over one absent field, for every message type, not just this one.
        // "style" is missing here; it must default rather than throw, and PingPongHandler.IsValidThemeSync
        // (not this deserializer) is what rejects the incomplete snapshot.
        var arrived = Deserialize(
            """{"type":"theme_sync","themeSync":{"seed":"#6750A4","mode":"dark","contrast":0.5,"dynamic":true,"sentAtUnixMs":1700000000000}}""");

        Assert.NotNull(arrived);
        Assert.NotNull(arrived!.ThemeSync);
        // GENUINELY NULL, NOT string.Empty — System.Text.Json fills an absent record property with
        // a runtime null, bypassing PhoneThemeSnapshot.Style's own `= string.Empty` initializer
        // (that initializer only applies to a plain `new PhoneThemeSnapshot()` in C#). Consuming code
        // (IsValidThemeSync, TryMapPhoneTheme) has to tolerate this null, not this deserializer.
        Assert.Null(arrived.ThemeSync!.Style);
        Assert.Equal("#6750A4", arrived.ThemeSync.SeedHex);
    }

    [Fact]
    public void AThemeSyncWithAnExtraUnknownFieldDeserializesAndIgnoresIt()
    {
        var arrived = Deserialize(
            """
            {"type":"theme_sync","themeSync":{"seed":"#6750A4","style":"tonal_spot","mode":"dark","contrast":0.5,"dynamic":true,"sentAtUnixMs":1700000000000,"somethingFuture":"x"}}
            """);

        Assert.NotNull(arrived?.ThemeSync);
        Assert.Equal("#6750A4", arrived!.ThemeSync!.SeedHex);
        Assert.Equal("tonal_spot", arrived.ThemeSync.Style);
    }

    [Fact]
    public void ClientIdAndReceivedUtcAreHostStampedNotWireFields()
    {
        // The phone never sends these - they are absent from the wire and only appear once
        // PingPongHandler stamps a `with` copy after validating. A message with neither still
        // deserializes cleanly, and the fields default rather than throwing (they are not `required`).
        var arrived = Deserialize(Serialize(new RemexMessage
        {
            Type = MessageTypes.ThemeSync,
            ThemeSync = Sample(),
        }))!.ThemeSync!;

        Assert.Null(arrived.ClientId);
        Assert.Equal(default, arrived.ReceivedUtc);

        var stamped = arrived with { ClientId = "phone-1", ReceivedUtc = DateTimeOffset.UtcNow };
        Assert.Equal("phone-1", stamped.ClientId);
    }
}
