using System.Text.Json.Serialization;

namespace Remex.Core.Models;

public record MonitorInfo
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("left")]
    public int Left { get; init; }

    [JsonPropertyName("top")]
    public int Top { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("isPrimary")]
    public bool IsPrimary { get; init; }
}
