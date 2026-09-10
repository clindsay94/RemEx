using System.Text.Json;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Serialization;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// The envelope must not put its ~60 unused payload slots on the wire as <c>"field":null</c>.
/// </summary>
/// <remarks>
/// <see cref="RemexMessage"/> is one envelope with a slot per message type and exactly one ever set,
/// so writing nulls made every message in both directions carry ~55 dead entries — a pong that should
/// be ~50 bytes went out at 1.5–2 KB, and pointer-rate input events paid the same tax (RemEx-bcgr).
/// <para>
/// The reason this needs no <c>protocolVersion</c> bump is asserted here rather than argued: absent
/// and null are indistinguishable to System.Text.Json, so a message serialized without nulls
/// round-trips to a value equal to one serialized with them.
/// </para>
/// </remarks>
public class NullFieldOmissionTests
{
    [Fact]
    public void Pong_CarriesOnlyItsOwnFields()
    {
        var json = RemexJson.Serialize(
            new RemexMessage { Type = MessageTypes.Pong },
            RemexJsonSerializerContext.Default.RemexMessage);

        // Key-level, not a bare substring search: a message can legitimately carry "null" inside a
        // VALUE — a file literally named "null" is the case that already bit the Android side.
        Assert.DoesNotContain("\":null", json);

        // The envelope scalars that are genuinely set still ship.
        Assert.Contains("\"type\":\"pong\"", json);
        Assert.Contains("\"protocolVersion\":2", json);
    }

    [Fact]
    public void APongIsSmallAgain()
    {
        var json = RemexJson.Serialize(
            new RemexMessage { Type = MessageTypes.Pong },
            RemexJsonSerializerContext.Default.RemexMessage);

        // Before this change the same message serialized to roughly 1.5-2 KB of "field":null. The
        // bound is deliberately loose — the point is the order of magnitude, not a byte count that
        // breaks every time someone adds an envelope field.
        Assert.True(json.Length < 200, $"a pong serialized to {json.Length} bytes: {json}");
    }

    [Fact]
    public void SetPayloadSlots_AreStillWritten()
    {
        var json = RemexJson.Serialize(
            new RemexMessage
            {
                Type = MessageTypes.DesktopInput,
                InputEvent = new InputEvent { EventType = InputEventTypes.MouseMove, X = 10, Y = 20 },
            },
            RemexJsonSerializerContext.Default.RemexMessage);

        Assert.Contains("\"inputEvent\"", json);
        Assert.Contains("\"x\":10", json);

        // ...and the unset OPTIONAL members of the payload itself are omitted too, which is where
        // most of the saving on a high-rate message comes from.
        Assert.DoesNotContain("\"deltaX\"", json);
    }

    [Fact]
    public void OmittingNulls_RoundTripsIdenticallyToWritingThem()
    {
        // This is the compatibility argument, executed rather than asserted in prose: a C# reader
        // cannot tell an absent field from a null one, so no protocolVersion bump is required.
        var original = new RemexMessage
        {
            Type = MessageTypes.CommandResponse,
            CommandSuccess = true,
            CommandMessage = "ok",
        };

        var withoutNulls = RemexJson.Serialize(original, RemexJsonSerializerContext.Default.RemexMessage);
        var withNulls = JsonSerializer.Serialize(original, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = RemexJsonSerializerContext.Default,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        });

        Assert.True(withoutNulls.Length < withNulls.Length,
            "omitting nulls should be strictly smaller than writing them");

        var fromShort = RemexJson.Deserialize(withoutNulls, RemexJsonSerializerContext.Default.RemexMessage);
        var fromLong = RemexJson.Deserialize(withNulls, RemexJsonSerializerContext.Default.RemexMessage);

        Assert.NotNull(fromShort);
        Assert.NotNull(fromLong);
        Assert.Equal(fromLong, fromShort);
    }

    [Fact]
    public void NonNullableFields_TheAndroidSideReadsStrictly_AreStillPresent()
    {
        // The Android client reads these with org.json get*(), which THROWS on a missing key. They
        // are non-nullable on the C# side so they are always written — this pins that, because
        // making one of them nullable later would break the phone at runtime with no compile error
        // on either side (RemEx-bcgr).
        var entry = RemexJson.Serialize(
            new AppEntry(Guid.NewGuid(), "Calculator", @"C:\calc.exe", "#4A3AFF", null),
            RemexJsonSerializerContext.Default.AppEntry);

        Assert.Contains("\"displayName\"", entry);
        Assert.Contains("\"targetPath\"", entry);

        var process = RemexJson.Serialize(
            new ProcessInfo { Id = 42, Name = "notepad" },
            RemexJsonSerializerContext.Default.ProcessInfo);

        Assert.Contains("\"id\"", process);
        Assert.Contains("\"name\"", process);
    }
}
