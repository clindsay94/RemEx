using System.Text.Json.Serialization;
using Remex.Core.Models;
using Remex.Core.Models.IPC;

namespace Remex.Core.Messages;

/// <summary>
/// Lightweight JSON envelope for all Remex IPC messages.
/// </summary>
/// <remarks>
/// <para>
/// ONE ENVELOPE, MANY PAYLOADS. <see cref="Type"/> is the discriminator and everything else is
/// optional; a message carries exactly one payload and leaves the other slots null. The slots follow
/// a strict convention that is worth stating once instead of on each of the thirty-odd properties:
/// <b>the property name is the payload type name</b> (<c>FileTransferStart? FileTransferStart</c>),
/// and the matching <see cref="MessageTypes"/> constant is the same name in snake_case
/// (<c>file_transfer_start</c>). For the types that CARRY a payload the mapping runs both ways, so
/// no lookup table is needed. The reverse is not total: plenty of constants name payload-free
/// messages (<c>ping</c>, <c>desktop_start</c>, <c>desktop_frame</c>) and a few carry a payload under
/// a different name (<c>desktop_input</c> arrives in the <c>InputEvent</c> slot). The individual
/// slots are left undocumented on purpose — a summary on each could only restate its own name.
/// </para>
/// <para>
/// WHAT IS NOT SELF-EVIDENT, and what actually bites:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Adding a slot is backward compatible; renaming one is not.</b> A new optional property is
/// invisible to older peers, so it needs no <see cref="ProtocolVersion"/> bump. Changing a name or a
/// meaning does, on BOTH sides in the same release — a version mismatch produces silent
/// deserialization failures, not clean errors.
/// </description></item>
/// <item><description>
/// <b>An unrecognised type is dropped in silence.</b> Neither end errors on a type it does not know,
/// so a new host → client message reaches the Android app only if it is also routed to a JNI
/// callback. <c>file_*</c> types are covered by prefix; anything else needs explicit wiring, and
/// forgetting it once bricked all of v3 file transfer with a misleading "peer did not respond"
/// (RemEx-y6x6). Always test a new client-bound type round-trip on a real device.
/// </description></item>
/// <item><description>
/// <b>The envelope is source-generated, not reflected.</b> <c>Remex.Core</c> compiles to a NativeAOT
/// library, so every payload type must be registered in the serializer context; a type that is not
/// silently fails to serialize in the Android build only.
/// </description></item>
/// </list>
/// </remarks>
public sealed record RemexMessage
{
    /// <summary>
    /// Message type discriminator (e.g. "ping", "pong").
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Protocol version for forward/backward compatibility.
    /// Defaults to 2 in RemEx 2.0; host rejects messages with ProtocolVersion &lt; 2.
    /// </summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; } = 2;

    /// <summary>
    /// Unique identifier for the client, used to track paired sessions across reconnections.
    /// </summary>
    [JsonPropertyName("clientId")]
    public string? ClientId { get; init; }

    /// <summary>
    /// UTC ticks at the time the message was created, used for latency measurement.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; init; }

    /// <summary>
    /// Optional payload attached for telemetry streaming.
    /// </summary>
    [JsonPropertyName("telemetry")]
    public TelemetryPayload? Telemetry { get; init; }

    /// <summary>Command action name (e.g. "Shutdown", "Lock").</summary>
    [JsonPropertyName("commandAction")]
    public string? CommandAction { get; init; }

    /// <summary>Command parameters (e.g. for WoL MAC address).</summary>
    [JsonPropertyName("commandParameters")]
    public Dictionary<string, string>? CommandParameters { get; init; }

    /// <summary>Whether the command succeeded (for response messages).</summary>
    [JsonPropertyName("commandSuccess")]
    public bool? CommandSuccess { get; init; }

    /// <summary>Response message from command execution.</summary>
    [JsonPropertyName("commandMessage")]
    public string? CommandMessage { get; init; }

    /// <summary>Launcher sync list.</summary>
    [JsonPropertyName("launcherEntries")]
    public List<Remex.Core.Models.AppEntry>? LauncherEntries { get; init; }

    /// <summary>Single launcher entry for add/remove.</summary>
    [JsonPropertyName("launcherEntry")]
    public Remex.Core.Models.AppEntry? LauncherEntry { get; init; }

    /// <summary>Dashboard layout profile for synchronization.</summary>
    [JsonPropertyName("dashboardProfile")]
    public Remex.Core.Models.DashboardProfile? DashboardProfile { get; init; }

    /// <summary>List of running processes.</summary>
    [JsonPropertyName("processList")]
    public List<Remex.Core.Models.ProcessInfo>? ProcessList { get; init; }

    /// <summary>Capability summary for the active host runtime.</summary>
    [JsonPropertyName("hostCapabilities")]
    public Remex.Core.Models.HostCapabilities? HostCapabilities { get; init; }

    /// <summary>Remote desktop input event.</summary>
    [JsonPropertyName("inputEvent")]
    public Remex.Core.Models.InputEvent? InputEvent { get; init; }

    /// <summary>Remote desktop streaming configuration.</summary>
    [JsonPropertyName("desktopConfig")]
    public Remex.Core.Models.DesktopConfig? DesktopConfig { get; init; }

    /// <summary>Remote desktop screen metadata.</summary>
    [JsonPropertyName("desktopMeta")]
    public Remex.Core.Models.DesktopMeta? DesktopMeta { get; init; }

    /// <summary>Remote desktop display enumeration response.</summary>
    [JsonPropertyName("desktopDisplayCatalog")]
    public Remex.Core.Models.DesktopDisplayCatalog? DesktopDisplayCatalog { get; init; }

    /// <summary>Remote desktop in-session target switch request.</summary>
    [JsonPropertyName("desktopTargetSwitch")]
    public Remex.Core.Models.DesktopTargetSwitchRequest? DesktopTargetSwitch { get; init; }

    /// <summary>Remote desktop window query request.</summary>
    [JsonPropertyName("desktopWindowQuery")]
    public Remex.Core.Models.DesktopWindowQuery? DesktopWindowQuery { get; init; }

    /// <summary>Remote desktop window action request.</summary>
    [JsonPropertyName("desktopWindowAction")]
    public Remex.Core.Models.DesktopWindowAction? DesktopWindowAction { get; init; }

    /// <summary>High-rate pointer/stylus sample batch (Stage 3 — Android → host).</summary>
    [JsonPropertyName("desktopPointerBatch")]
    public Remex.Core.Models.DesktopPointerBatch? DesktopPointerBatch { get; init; }

    /// <summary>Stream surface descriptor (Stage 3 — host → client).</summary>
    [JsonPropertyName("desktopStreamDescriptor")]
    public Remex.Core.Models.DesktopStreamDescriptor? DesktopStreamDescriptor { get; init; }

    /// <summary>Explicit remote cursor state for first-party clients.</summary>
    [JsonPropertyName("desktopCursorState")]
    public Remex.Core.Models.DesktopCursorState? DesktopCursorState { get; init; }

    /// <summary>Explicit remote cursor shape payload for first-party clients.</summary>
    [JsonPropertyName("desktopCursorShape")]
    public Remex.Core.Models.DesktopCursorShape? DesktopCursorShape { get; init; }

    /// <summary>Remote desktop window query/action response.</summary>
    [JsonPropertyName("desktopWindowResult")]
    public Remex.Core.Models.DesktopWindowResult? DesktopWindowResult { get; init; }

    /// <summary>Human-readable error/diagnostic text sent by the host.</summary>
    [JsonPropertyName("errorText")]
    public string? ErrorText { get; init; }

    /// <summary>
    /// Correlation identifier used to match a command response to its originating request.
    /// The client embeds a generated ID when sending a command; the host echoes it back in
    /// the response so the client can complete the correct awaiter.
    /// Null for messages that do not participate in request-response pairing.
    /// </summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    // ── 2.0 Pairing ──

    [JsonPropertyName("pairingRequest")]
    public PairingRequest? PairingRequest { get; init; }

    [JsonPropertyName("pairingResponse")]
    public PairingResponse? PairingResponse { get; init; }

    [JsonPropertyName("pairingComplete")]
    public PairingComplete? PairingComplete { get; init; }

    /// <summary>Host → client reconnect challenge (proof-of-possession nonce).</summary>
    [JsonPropertyName("reconnectChallenge")]
    public ReconnectChallenge? ReconnectChallenge { get; init; }

    /// <summary>Client → host reconnect proof (HMAC over the challenge nonce).</summary>
    [JsonPropertyName("reconnectProof")]
    public ReconnectProof? ReconnectProof { get; init; }

    // ── 2.0 File Transfer ──

    [JsonPropertyName("fileTransferStart")]
    public FileTransferStart? FileTransferStart { get; init; }

    [JsonPropertyName("fileTransferChunk")]
    public FileTransferChunk? FileTransferChunk { get; init; }

    [JsonPropertyName("fileTransferEnd")]
    public FileTransferEnd? FileTransferEnd { get; init; }

    [JsonPropertyName("fileTransferCancel")]
    public FileTransferCancel? FileTransferCancel { get; init; }

    [JsonPropertyName("fileTransferProgress")]
    public FileTransferProgress? FileTransferProgress { get; init; }

    [JsonPropertyName("fileRootsRequest")]
    public FileRootsRequest? FileRootsRequest { get; init; }

    [JsonPropertyName("fileRootsResponse")]
    public FileRootsResponse? FileRootsResponse { get; init; }

    [JsonPropertyName("fileBrowseRequest")]
    public FileBrowseRequest? FileBrowseRequest { get; init; }

    [JsonPropertyName("fileBrowseResponse")]
    public FileBrowseResponse? FileBrowseResponse { get; init; }

    [JsonPropertyName("fileManageRequest")]
    public FileManageRequest? FileManageRequest { get; init; }

    [JsonPropertyName("fileManageResponse")]
    public FileManageResponse? FileManageResponse { get; init; }

    [JsonPropertyName("fileHashRequest")]
    public FileHashRequest? FileHashRequest { get; init; }

    [JsonPropertyName("fileHashResponse")]
    public FileHashResponse? FileHashResponse { get; init; }

    [JsonPropertyName("fileRootManageRequest")]
    public FileRootManageRequest? FileRootManageRequest { get; init; }

    [JsonPropertyName("fileRootManageResponse")]
    public FileRootManageResponse? FileRootManageResponse { get; init; }

    // ── 2.1 File Sharing Overhaul (protocolVersion 3) ──
    // All additive and nullable; v2 peers ignore them and keep the legacy base64 path.

    [JsonPropertyName("fileTransferOffer")]
    public FileTransferOffer? FileTransferOffer { get; init; }

    [JsonPropertyName("fileTransferReady")]
    public FileTransferReady? FileTransferReady { get; init; }

    [JsonPropertyName("fileTransferComplete")]
    public FileTransferComplete? FileTransferComplete { get; init; }

    [JsonPropertyName("fileTransferResult")]
    public FileTransferResult? FileTransferResult { get; init; }

    [JsonPropertyName("fileTransferControl")]
    public FileTransferControl? FileTransferControl { get; init; }

    [JsonPropertyName("fileVolumesRequest")]
    public FileVolumesRequest? FileVolumesRequest { get; init; }

    [JsonPropertyName("fileVolumesResponse")]
    public FileVolumesResponse? FileVolumesResponse { get; init; }

    [JsonPropertyName("fileSearchRequest")]
    public FileSearchRequest? FileSearchRequest { get; init; }

    [JsonPropertyName("fileSearchResponse")]
    public FileSearchResponse? FileSearchResponse { get; init; }

    [JsonPropertyName("fileMetadataRequest")]
    public FileMetadataRequest? FileMetadataRequest { get; init; }

    [JsonPropertyName("fileMetadataResponse")]
    public FileMetadataResponse? FileMetadataResponse { get; init; }

    [JsonPropertyName("fileThumbnailRequest")]
    public FileThumbnailRequest? FileThumbnailRequest { get; init; }

    [JsonPropertyName("fileThumbnailResponse")]
    public FileThumbnailResponse? FileThumbnailResponse { get; init; }

    [JsonPropertyName("fileConsentRequest")]
    public FileConsentRequest? FileConsentRequest { get; init; }

    [JsonPropertyName("fileConsentResponse")]
    public FileConsentResponse? FileConsentResponse { get; init; }

    [JsonPropertyName("filePushOffer")]
    public FilePushOffer? FilePushOffer { get; init; }

    [JsonPropertyName("filePushResponse")]
    public FilePushResponse? FilePushResponse { get; init; }

    /// <summary>
    /// PIN payload for <see cref="MessageTypes.PairingPinResponse"/>. Null when the transport is
    /// untrusted OR no pairing session is active (deliberately indistinguishable — mirrors the
    /// <c>GET /pairing-pin</c> 404-for-both design). Optional addition; no protocolVersion bump.
    /// </summary>
    [JsonPropertyName("pairingPin")]
    public PairingPinInfo? PairingPin { get; init; }
}

/// <summary>
/// Well-known message type constants.
/// </summary>
/// <remarks>
/// <para>
/// These strings ARE the wire protocol. Each is the snake_case form of its
/// <see cref="RemexMessage"/> payload slot, so the constants are left individually undocumented:
/// the name and the value are the same word, and a per-constant summary could only repeat it.
/// </para>
/// <para>
/// Two things are worth knowing before adding one. Changing an existing value silently breaks every
/// shipped client, because a peer ignores types it does not recognise rather than reporting an
/// error. And the Android client builds some of its JSON by hand in Kotlin, so a type can exist as a
/// literal on the phone with no constant here — the parity test
/// <c>EveryMessageTypeAndroidSends_HasAMatchingConstant</c> exists because exactly that drift once
/// shipped, and it also matters for authorisation, since loopback-only gating matches on CONSTANTS
/// and cannot gate a hand-built literal at all.
/// </para>
/// </remarks>
public static class MessageTypes
{
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Telemetry = "telemetry";
    public const string Command = "command";
    public const string CommandResponse = "command_response";
    public const string LauncherSync = "launcher_sync";
    public const string LauncherAdd = "launcher_add";
    public const string LauncherRemove = "launcher_remove";
    /// <summary>
    /// Client → host: re-send the current launcher allowlist. A pure read, so unlike the three
    /// mutating launcher types it is NOT loopback-gated (see <c>PingPongHandler.RequiresLoopback</c>).
    /// The host replies with <see cref="LauncherSync"/>.
    /// </summary>
    /// <remarks>
    /// Existed only as a hand-built literal on the Android side with no constant and no host case,
    /// so every Refresh tap fell through to the handler's "Unknown message type" default and the
    /// phone sat on its spinner until a 5s client-side safety net expired (RemEx-vpxx).
    /// </remarks>
    public const string LauncherSyncRequest = "launcher_sync_request";
    public const string LayoutSync = "layout_sync";
    public const string LayoutUpdate = "layout_update";
    public const string LayoutRequest = "layout_request";
    public const string ProcessListRequest = "process_list_request";
    public const string ProcessListSync = "process_list_sync";
    public const string HostInfo = "host_info";
    public const string DesktopStart = "desktop_start";
    public const string DesktopStop = "desktop_stop";
    public const string DesktopInput = "desktop_input";
    public const string DesktopConfig = "desktop_config";
    public const string DesktopMeta = "desktop_meta";
    public const string DesktopError = "desktop_error";
    public const string DesktopDisplayQuery = "desktop_display_query";
    public const string DesktopDisplayList = "desktop_display_list";
    public const string DesktopTargetSwitch = "desktop_target_switch";
    /// <summary>High-rate pointer/stylus batch from Android to host (Stage 3).</summary>
    public const string DesktopPointerBatch = "desktop_pointer_batch";
    /// <summary>Stream surface descriptor from host to client (Stage 3).</summary>
    public const string DesktopStreamDescriptor = "desktop_stream_descriptor";
    public const string DesktopCursorState = "desktop_cursor_state";
    public const string DesktopCursorShape = "desktop_cursor_shape";
    public const string DesktopWindowQuery = "desktop_window_query";
    public const string DesktopWindowAction = "desktop_window_action";
    public const string DesktopWindowResult = "desktop_window_result";
    /// <summary>Client-to-host request for an on-demand keyframe (IDR) after decoder desync (RD-2).</summary>
    public const string DesktopKeyframeRequest = "desktop_keyframe_request";

    // ── 2.0 Pairing ──
    public const string PairingRequest = "pairing_request";
    public const string PairingResponse = "pairing_response";
    public const string PairingComplete = "pairing_complete";
    public const string PairingError = "pairing_error";
    /// <summary>Host → client reconnect challenge (proof-of-possession nonce).</summary>
    public const string ReconnectChallenge = "reconnect_challenge";
    /// <summary>Client → host reconnect proof (HMAC over the challenge nonce).</summary>
    public const string ReconnectProof = "reconnect_proof";
    /// <summary>
    /// Client → host request to relay the active pairing PIN over the pairing <c>/ws</c> socket
    /// (ASI-compliant replacement for the trust-all HTTP auto-fetch). Gated host-side by
    /// <c>TransportTrust.IsTrustedForPinAutoFetch</c>; can only relay an already-active PIN.
    /// </summary>
    public const string PairingPinRequest = "pairing_pin_request";
    /// <summary>Host → client response carrying the active PIN (or null payload when denied/absent).</summary>
    public const string PairingPinResponse = "pairing_pin_response";

    // ── 2.0 File Transfer ──
    public const string FileTransferStart = "file_transfer_start";
    public const string FileTransferChunk = "file_transfer_chunk";
    public const string FileTransferEnd = "file_transfer_end";
    public const string FileTransferCancel = "file_transfer_cancel";
    public const string FileTransferProgress = "file_transfer_progress";
    public const string FileRootsRequest = "file_roots_request";
    public const string FileRootsResponse = "file_roots_response";
    public const string FileBrowseRequest = "file_browse_request";
    public const string FileBrowseResponse = "file_browse_response";
    public const string FileManageRequest = "file_manage_request";
    public const string FileManageResponse = "file_manage_response";
    public const string FileHashRequest = "file_hash_request";
    public const string FileHashResponse = "file_hash_response";
    public const string FileRootManageRequest = "file_root_manage_request";
    public const string FileRootManageResponse = "file_root_manage_response";

    // ── 2.1 File Sharing Overhaul (protocolVersion 3) ──
    // v3 transfer negotiation (replaces base64 start/chunk/end for v3 peers).
    public const string FileTransferOffer = "file_transfer_offer";
    public const string FileTransferReady = "file_transfer_ready";
    public const string FileTransferComplete = "file_transfer_complete";
    public const string FileTransferResult = "file_transfer_result";
    public const string FileTransferControl = "file_transfer_control";
    // Browse / metadata.
    public const string FileVolumesRequest = "file_volumes_request";
    public const string FileVolumesResponse = "file_volumes_response";
    public const string FileSearchRequest = "file_search_request";
    public const string FileSearchResponse = "file_search_response";
    public const string FileMetadataRequest = "file_metadata_request";
    public const string FileMetadataResponse = "file_metadata_response";
    public const string FileThumbnailRequest = "file_thumbnail_request";
    public const string FileThumbnailResponse = "file_thumbnail_response";
    // Consent / push.
    public const string FileConsentRequest = "file_consent_request";
    public const string FileConsentResponse = "file_consent_response";
    public const string FilePushOffer = "file_push_offer";
    public const string FilePushResponse = "file_push_response";
}
