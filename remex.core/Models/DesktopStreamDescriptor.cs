using System.Text.Json.Serialization;

namespace Remex.Core.Models;

/// <summary>
/// Logical-to-pixel mapping descriptor for a single remote desktop stream surface.
/// Sent from the host to the client so pointer samples can be mapped correctly
/// regardless of scaling, DPI, or multi-monitor layout.
/// </summary>
public record DesktopStreamDescriptor
{
    /// <summary>
    /// Opaque mapping identifier that ties this descriptor to DesktopPointerSample.StreamMappingId
    /// and DesktopMeta.StreamMappingId.
    /// </summary>
    [JsonPropertyName("streamMappingId")]
    public required string StreamMappingId { get; init; }

    /// <summary>
    /// Monotonically increasing serial number. Incremented each time the stream surface
    /// changes (monitor hotplug, resolution change, capture session restart).
    /// Clients should discard queued pointer samples from a previous serial.
    /// </summary>
    [JsonPropertyName("streamSerial")]
    public long StreamSerial { get; init; }

    /// <summary>Logical width in the host coordinate space (e.g. scaled DPI units).</summary>
    [JsonPropertyName("logicalWidth")]
    public int LogicalWidth { get; init; }

    /// <summary>Logical height in the host coordinate space.</summary>
    [JsonPropertyName("logicalHeight")]
    public int LogicalHeight { get; init; }

    /// <summary>Actual pixel width of the captured frame.</summary>
    [JsonPropertyName("pixelWidth")]
    public int PixelWidth { get; init; }

    /// <summary>Actual pixel height of the captured frame.</summary>
    [JsonPropertyName("pixelHeight")]
    public int PixelHeight { get; init; }
}
