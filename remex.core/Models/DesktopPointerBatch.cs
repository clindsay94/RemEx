using System.Text.Json.Serialization;

namespace Remex.Core.Models;

/// <summary>
/// A batch of pointer samples sent from the Android client to the host in a single message.
/// Batching reduces per-message overhead for high-rate stylus input.
/// </summary>
public record DesktopPointerBatch
{
    /// <summary>Stream mapping ID this batch belongs to.</summary>
    [JsonPropertyName("streamMappingId")]
    public string? StreamMappingId { get; init; }

    /// <summary>Ordered list of pointer samples in this batch.</summary>
    [JsonPropertyName("samples")]
    public required List<DesktopPointerSample> Samples { get; init; }
}
