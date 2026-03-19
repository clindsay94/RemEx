using System.Text.Json.Serialization;

namespace Remex.Core.Models;

public record DesktopMeta
{
    [JsonPropertyName("screenWidth")]
    public int ScreenWidth { get; init; }

    [JsonPropertyName("screenHeight")]
    public int ScreenHeight { get; init; }

    [JsonPropertyName("monitorCount")]
    public int MonitorCount { get; init; } = 1;

    /// <summary>
    /// Unique identifier for the host process instance.
    /// Used to detect self-connections (infinite mirror prevention).
    /// </summary>
    [JsonPropertyName("hostInstanceId")]
    public string? HostInstanceId { get; init; }
}
