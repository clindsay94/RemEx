using System.Text.Json.Serialization;

namespace Remex.Core.Models;

public record DesktopConfig
{
    [JsonPropertyName("quality")]
    public int Quality { get; init; } = 50;

    [JsonPropertyName("scale")]
    public double Scale { get; init; } = 0.5;

    [JsonPropertyName("targetFps")]
    public int TargetFps { get; init; } = 10;

    [JsonPropertyName("monitorIndex")]
    public int MonitorIndex { get; init; } = 0;
}
