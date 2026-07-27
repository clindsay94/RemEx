using System.Text.Json.Serialization;

namespace Remex.Core.Models;

public sealed record DesktopWindowResult
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("errorText")]
    public string? ErrorText { get; init; }

    [JsonPropertyName("backend")]
    public string? Backend { get; init; }

    [JsonPropertyName("currentDesktop")]
    public int? CurrentDesktop { get; init; }

    [JsonPropertyName("desktopCount")]
    public int? DesktopCount { get; init; }

    [JsonPropertyName("windows")]
    public List<DesktopWindowInfo>? Windows { get; init; }
}
