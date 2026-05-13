using System.Collections.Generic;
using System.Text.Json.Serialization;
using Remex.Core.Models;

namespace Remex.Core.Messages;

/// <summary>
/// Lightweight JSON envelope for all Remex IPC messages.
/// </summary>
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
}

/// <summary>
/// Well-known message type constants.
/// </summary>
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
    public const string LayoutSync = "layout_sync";
    public const string LayoutUpdate = "layout_update";
    public const string LayoutRequest = "layout_request";
    public const string ProcessListRequest = "process_list_request";
    public const string ProcessListSync = "process_list_sync";
    public const string HostInfo = "host_info";
    public const string DesktopStart = "desktop_start";
    public const string DesktopStop = "desktop_stop";
    public const string DesktopFrame = "desktop_frame";
    public const string DesktopInput = "desktop_input";
    public const string DesktopConfig = "desktop_config";
    public const string DesktopMeta = "desktop_meta";
    public const string DesktopError = "desktop_error";
    /// <summary>High-rate pointer/stylus batch from Android to host (Stage 3).</summary>
    public const string DesktopPointerBatch = "desktop_pointer_batch";
    /// <summary>Stream surface descriptor from host to client (Stage 3).</summary>
    public const string DesktopStreamDescriptor = "desktop_stream_descriptor";
    public const string DesktopWindowQuery = "desktop_window_query";
    public const string DesktopWindowAction = "desktop_window_action";
    public const string DesktopWindowResult = "desktop_window_result";

    // ── 2.0 Pairing ──
    public const string PairingRequest = "pairing_request";
    public const string PairingResponse = "pairing_response";
    public const string PairingComplete = "pairing_complete";
    public const string PairingError = "pairing_error";

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
}
