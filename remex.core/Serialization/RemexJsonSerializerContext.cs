using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Models.IPC;
using Remex.Core.Native;

namespace Remex.Core.Serialization;

// WhenWritingNull: RemexMessage is one envelope with ~60 nullable payload slots of which exactly one
// is ever set, so writing nulls put ~55 "field":null entries on the wire in BOTH directions — a pong
// that should be ~50 bytes went out at 1.5–2 KB, and input events at pointer rate paid it too.
//
// Safe without a protocolVersion bump because absent and null are the same thing to System.Text.Json,
// so every C# reader is unaffected by construction. The risk was only ever the Android side, which
// parses some forwarded messages with org.json by hand — and that was audited rather than assumed
// (RemEx-bcgr):
//
//   • Every strict get*() call (which THROWS on a missing key) lands on a field that is non-nullable
//     in C# — AppEntry.DisplayName/TargetPath, ProcessInfo.Id/Name, the required pairing nonce,
//     DesktopMeta.CursorX/CursorY — so those keys keep being written. reconnectChallenge is read only
//     after type == "reconnect_challenge", where it is non-null.
//     NOTE the guarantee is by convention, not by the runtime: nullable reference types are not
//     enforced at run time, so a non-nullable property CAN hold null (e.g. AppEntry is rehydrated
//     from apps.json into a positional record whose parameters are not `required`). Today that
//     serializes as "displayName":null and Android's getString hands back the literal string "null";
//     once omitted it throws instead, and AppLauncherViewModel catches at whole-array scope, so one
//     ugly row becomes an empty launcher. Low probability, but the invariant to preserve is
//     "these properties are never null", NOT "the compiler stops them being null".
//     The same shape is now load-bearing for `required` members: a null-valued required property
//     used to serialize present-and-null (satisfying required on read) and would now be omitted,
//     making the receiver throw and the message drop SILENTLY — the RemEx-y6x6 failure mode. There
//     are currently no `required T?` properties; keep it that way.
//   • has() && !isNull() pairs are unchanged: isNull() is true for an absent key as well as an
//     explicit null.
//
// It also FIXES two live defects, which is why this is worth more than the bytes. Android's
// optString() on an explicit JSON null returns the literal string "null", so the eight
// `if (has(k)) optString(k) else null` sites in FileHostHandler/FileTransferEngine were producing
// "null" instead of null — FileTransferViewModel already carries a takeUnless(it == "null") guard
// that exists solely to undo it. And RemoteDesktopViewModel's
// `optInt("desktopNumber").takeIf { has("desktopNumber") }` was yielding 0 for a null desktopNumber,
// because has() is true for an explicitly-null key; it now yields the null the code was written to
// produce.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppEntry))]
[JsonSerializable(typeof(AndroidNativeInitRequest))]
[JsonSerializable(typeof(AndroidNativeInitializationResponse))]
[JsonSerializable(typeof(AndroidNativeOperationResponse))]
[JsonSerializable(typeof(AndroidNativeTelemetryResponse))]
[JsonSerializable(typeof(CardState))]
[JsonSerializable(typeof(GraphType))]
[JsonSerializable(typeof(CommandRequest))]
[JsonSerializable(typeof(CommandResponse))]
[JsonSerializable(typeof(PairingPinInfo))]
[JsonSerializable(typeof(CustomizationSettings))]
[JsonSerializable(typeof(DashboardProfile))]
[JsonSerializable(typeof(DesktopConfig))]
[JsonSerializable(typeof(DesktopClientCapabilities))]
[JsonSerializable(typeof(DesktopDisplayCatalog))]
[JsonSerializable(typeof(DesktopDisplayInfo))]
[JsonSerializable(typeof(DesktopCaptureTarget))]
[JsonSerializable(typeof(DesktopTargetSwitchRequest))]
[JsonSerializable(typeof(DesktopCursorState))]
[JsonSerializable(typeof(DesktopCursorShape))]
[JsonSerializable(typeof(DesktopMeta))]
[JsonSerializable(typeof(DesktopFrameEnvelopeHeader))]
[JsonSerializable(typeof(DesktopPointerSample))]
[JsonSerializable(typeof(DesktopPointerBatch))]
[JsonSerializable(typeof(DesktopCodecInfo))]
[JsonSerializable(typeof(DesktopStreamDescriptor))]
[JsonSerializable(typeof(DesktopWindowAction))]
[JsonSerializable(typeof(DesktopWindowInfo))]
[JsonSerializable(typeof(DesktopWindowQuery))]
[JsonSerializable(typeof(DesktopWindowResult))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(HostCapabilities))]
[JsonSerializable(typeof(InputEvent))]
[JsonSerializable(typeof(List<AppEntry>))]
[JsonSerializable(typeof(List<CardState>))]
[JsonSerializable(typeof(List<DesktopWindowInfo>))]
[JsonSerializable(typeof(List<DesktopDisplayInfo>))]
[JsonSerializable(typeof(List<DesktopCaptureMode>))]
[JsonSerializable(typeof(List<ProcessInfo>))]
[JsonSerializable(typeof(List<SensorReading>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(MonitorInfo))]
[JsonSerializable(typeof(ProcessInfo))]
[JsonSerializable(typeof(RemexMessage))]
[JsonSerializable(typeof(MetricKind))]
[JsonSerializable(typeof(SensorReading))]
[JsonSerializable(typeof(SensorCardTheme))]
[JsonSerializable(typeof(TelemetryPayload))]
// ── 2.0 Pairing ──
[JsonSerializable(typeof(PairingRequest))]
[JsonSerializable(typeof(PairingResponse))]
[JsonSerializable(typeof(PairingComplete))]
[JsonSerializable(typeof(ReconnectChallenge))]
[JsonSerializable(typeof(ReconnectProof))]
// ── 2.0 File Transfer ──
[JsonSerializable(typeof(FileRootsRequest))]
[JsonSerializable(typeof(FileRootsResponse))]
[JsonSerializable(typeof(FileSharedRoot))]
[JsonSerializable(typeof(FileTransferStart))]
[JsonSerializable(typeof(FileTransferChunk))]
[JsonSerializable(typeof(FileTransferEnd))]
[JsonSerializable(typeof(FileTransferCancel))]
[JsonSerializable(typeof(FileTransferProgress))]
[JsonSerializable(typeof(FileBrowseRequest))]
[JsonSerializable(typeof(FileBrowseResponse))]
[JsonSerializable(typeof(FileEntry))]
[JsonSerializable(typeof(FileManageRequest))]
[JsonSerializable(typeof(FileManageResponse))]
[JsonSerializable(typeof(FileHashRequest))]
[JsonSerializable(typeof(FileHashResponse))]
[JsonSerializable(typeof(FileRootManageRequest))]
[JsonSerializable(typeof(FileRootManageResponse))]
// ── 2.1 File Sharing Overhaul (protocolVersion 3) ──
// Every new serializable payload/nested type MUST be listed here or the NativeAOT Android link breaks.
[JsonSerializable(typeof(FileCapabilities))]
[JsonSerializable(typeof(FileFrameEnvelope))]
[JsonSerializable(typeof(FileTransferOffer))]
[JsonSerializable(typeof(FileTransferReady))]
[JsonSerializable(typeof(FileTransferComplete))]
[JsonSerializable(typeof(FileTransferResult))]
[JsonSerializable(typeof(FileTransferControl))]
[JsonSerializable(typeof(FileVolumesRequest))]
[JsonSerializable(typeof(FileVolumeInfo))]
[JsonSerializable(typeof(FileVolumesResponse))]
[JsonSerializable(typeof(FileSearchRequest))]
[JsonSerializable(typeof(FileSearchEntry))]
[JsonSerializable(typeof(FileSearchResponse))]
[JsonSerializable(typeof(FileManifestRequest))]
[JsonSerializable(typeof(FileManifestEntry))]
[JsonSerializable(typeof(FileManifestResponse))]
[JsonSerializable(typeof(FileMetadataRequest))]
[JsonSerializable(typeof(FileMetadataResponse))]
[JsonSerializable(typeof(FileThumbnailRequest))]
[JsonSerializable(typeof(FileThumbnailResponse))]
[JsonSerializable(typeof(ClientCapabilities))]
[JsonSerializable(typeof(FileConsentRequest))]
[JsonSerializable(typeof(FileConsentResponse))]
[JsonSerializable(typeof(FilePushFile))]
[JsonSerializable(typeof(ClipboardPush))]
[JsonSerializable(typeof(ClipboardContent))]
[JsonSerializable(typeof(ClipboardPushResult))]
[JsonSerializable(typeof(FilePushOffer))]
[JsonSerializable(typeof(FilePushResponse))]
[JsonSerializable(typeof(MediaPlaybackState))]
[JsonSerializable(typeof(MediaArtworkRequest))]
[JsonSerializable(typeof(MediaArtwork))]
public partial class RemexJsonSerializerContext : JsonSerializerContext
{
}

public static class RemexJson
{
    public static JsonSerializerOptions Compact { get; } = new(RemexJsonSerializerContext.Default.Options);

    public static JsonTypeInfo<T> TypeInfo<T>()
        => (JsonTypeInfo<T>)RemexJsonSerializerContext.Default.GetTypeInfo(typeof(T))!;

    public static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Serialize(value, typeInfo);

    public static string SerializeIndented<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        JsonSerializer.Serialize(writer, value, typeInfo);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

    public static async Task SerializeIndentedAsync<T>(Stream stream, T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        JsonSerializer.Serialize(writer, value, typeInfo);
        await writer.FlushAsync(cancellationToken);
    }

    public static T? Deserialize<T>(string json, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Deserialize(json, typeInfo);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Deserialize(utf8Json, typeInfo);

    public static ValueTask<T?> DeserializeAsync<T>(Stream utf8Json, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
        => JsonSerializer.DeserializeAsync(utf8Json, typeInfo, cancellationToken);
}
