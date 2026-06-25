using System.Text.Json.Serialization;

namespace Remex.Core.Models;

public sealed record DesktopWindowQuery
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("searchText")]
    public string SearchText { get; init; } = string.Empty;

    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 25;

    [JsonPropertyName("includeAllDesktops")]
    public bool IncludeAllDesktops { get; init; } = true;
}
