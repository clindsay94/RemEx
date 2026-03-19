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
}
