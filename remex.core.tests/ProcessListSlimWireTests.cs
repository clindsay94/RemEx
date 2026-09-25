using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Serialization;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins the opt-in slim process list (perf audit P4-10).
/// </summary>
/// <remarks>
/// The phone's Task Manager reads id, name, CPU, memory and start time, and nothing else, yet every
/// 4-second poll carried each process's path, publisher, version, user and install date. A
/// <c>process_list_request</c> may now ask for <c>processListSlim</c>. The shape is additive both ways:
/// an old host ignores the unknown flag and sends the full list, and a request without the flag (the PC
/// desktop's, an old phone's) still gets the full list, so no <c>protocolVersion</c> bump.
/// </remarks>
public class ProcessListSlimWireTests
{
    private static readonly ProcessInfo Full = new()
    {
        Id = 4242,
        Name = "chrome",
        CpuUsage = 12.5,
        MemoryUsage = 512L * 1024 * 1024,
        UserName = @"DESKTOP\connor",
        FilePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        Version = "140.0.7339.81",
        Publisher = "Google LLC",
        InstallDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        StartTimeUnixMs = 1_790_000_000_123,
    };

    [Fact]
    public void ToSlim_KeepsEverythingTheAndroidTaskManagerReads()
    {
        var slim = Full.ToSlim();

        Assert.Equal(Full.Id, slim.Id);
        Assert.Equal(Full.Name, slim.Name);
        Assert.Equal(Full.CpuUsage, slim.CpuUsage);
        Assert.Equal(Full.MemoryUsage, slim.MemoryUsage);
        // The kill-time identity check (RemEx-on4n) depends on this one; dropping it would silently
        // turn every guarded kill into an unchecked one.
        Assert.Equal(Full.StartTimeUnixMs, slim.StartTimeUnixMs);
    }

    [Fact]
    public void ToSlim_DropsTheFieldsAndroidDiscards_FromTheWire()
    {
        var json = RemexJson.Serialize(Full.ToSlim(), RemexJsonSerializerContext.Default.ProcessInfo);

        Assert.DoesNotContain("chrome.exe", json);
        Assert.DoesNotContain("Google LLC", json);
        Assert.DoesNotContain("140.0.7339.81", json);
        Assert.DoesNotContain("connor", json);
        Assert.DoesNotContain("installDate", json);
        Assert.Contains("\"startTimeUnixMs\":1790000000123", json);
        // The empty keys stay (the type keeps its non-null strings); the VALUES are what go.
        var full = RemexJson.Serialize(Full, RemexJsonSerializerContext.Default.ProcessInfo);
        Assert.True(full.Length - json.Length > 100, $"full={full.Length} slim={json.Length}");
    }

    [Fact]
    public void ToSlim_StillDeserializesAsAProcessInfo_WithNonNullStrings()
    {
        // An older consumer of the full shape must not trip over the slim one.
        var json = RemexJson.Serialize(Full.ToSlim(), RemexJsonSerializerContext.Default.ProcessInfo);
        var back = RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.ProcessInfo);

        Assert.NotNull(back);
        Assert.Equal(string.Empty, back!.FilePath);
        Assert.Equal(string.Empty, back.Publisher);
        Assert.Equal(string.Empty, back.Version);
        Assert.Equal(string.Empty, back.UserName);
        Assert.Null(back.InstallDate);
    }

    [Fact]
    public void TheRequestFlag_RoundTrips()
    {
        var request = new RemexMessage { Type = MessageTypes.ProcessListRequest, ProcessListSlim = true };
        var json = RemexJson.Serialize(request, RemexJsonSerializerContext.Default.RemexMessage);

        Assert.Contains("\"processListSlim\":true", json);
        Assert.True(RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.RemexMessage)!.ProcessListSlim);
    }

    [Fact]
    public void ARequestWithoutTheFlag_DoesNotCarryIt_AndReadsAsNotSlim()
    {
        // The PC desktop's request and an old phone's: unchanged on the wire, full list back.
        var json = RemexJson.Serialize(
            new RemexMessage { Type = MessageTypes.ProcessListRequest },
            RemexJsonSerializerContext.Default.RemexMessage);

        Assert.DoesNotContain("processListSlim", json);
        Assert.Null(RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.RemexMessage)!.ProcessListSlim);
    }

    [Fact]
    public void TheKotlinRequestShape_IsReadByTheCoreDeserializer()
    {
        // Exactly what TaskManagerViewModel sends through SendMessageNative, which deserializes it
        // into a RemexMessage and re-sends THAT - so a flag the model does not declare is dropped
        // right there, on the phone, before the host could ever see it.
        var msg = RemexJson.Deserialize(
            """{"type":"process_list_request","processListSlim":true}""",
            RemexJsonSerializerContext.Default.RemexMessage);

        Assert.Equal(MessageTypes.ProcessListRequest, msg!.Type);
        Assert.True(msg.ProcessListSlim);
    }
}
