using System.Collections.Generic;
using System.Text.Json.Serialization;

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

    /// <summary>Remote desktop input event.</summary>
    [JsonPropertyName("inputEvent")]
    public Remex.Core.Models.InputEvent? InputEvent { get; init; }

    /// <summary>Remote desktop streaming configuration.</summary>
    [JsonPropertyName("desktopConfig")]
    public Remex.Core.Models.DesktopConfig? DesktopConfig { get; init; }

    /// <summary>Remote desktop screen metadata.</summary>
    [JsonPropertyName("desktopMeta")]
    public Remex.Core.Models.DesktopMeta? DesktopMeta { get; init; }
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
    public const string DesktopStart = "desktop_start";
    public const string DesktopStop = "desktop_stop";
    public const string DesktopFrame = "desktop_frame";
    public const string DesktopInput = "desktop_input";
    public const string DesktopConfig = "desktop_config";
    public const string DesktopMeta = "desktop_meta";
}
