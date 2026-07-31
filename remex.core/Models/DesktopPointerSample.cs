using System.Text.Json.Serialization;

namespace Remex.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<PointerDeviceKind>))]
public enum PointerDeviceKind
{
    Mouse,
    Touch,
    Stylus,
    Eraser,
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<PointerToolKind>))]
public enum PointerToolKind
{
    None,
    Pen,
    Eraser,
    Mouse,
    Finger,
}

[JsonConverter(typeof(JsonStringEnumConverter<PointerPhase>))]
public enum PointerPhase
{
    HoverStart,
    HoverMove,
    HoverEnd,
    ContactStart,
    ContactMove,
    ContactEnd,
    ButtonPress,
    ButtonRelease,
}

/// <summary>
/// A single high-resolution pointer/stylus sample.
/// Used for stylus-grade input from Android (S-Pen) to the Linux host.
/// Backward-compatible addition: the existing InputEvent mouse path remains unchanged.
/// </summary>
public record DesktopPointerSample
{
    /// <summary>Protocol version for this sample schema. Current: 1.</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; } = 1;

    /// <summary>Monotonic millisecond timestamp from the sending device.</summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }

    /// <summary>Pointer/touch ID for multi-touch disambiguation.</summary>
    [JsonPropertyName("pointerId")]
    public int PointerId { get; init; }

    /// <summary>Physical device kind.</summary>
    [JsonPropertyName("deviceKind")]
    public PointerDeviceKind DeviceKind { get; init; }

    /// <summary>Active tool reported by the device (pen tip, eraser, etc.).</summary>
    [JsonPropertyName("toolKind")]
    public PointerToolKind ToolKind { get; init; }

    /// <summary>Interaction phase.</summary>
    [JsonPropertyName("phase")]
    public PointerPhase Phase { get; init; }

    /// <summary>Logical X in the host coordinate space (maps via DesktopStreamDescriptor).</summary>
    [JsonPropertyName("logicalX")]
    public float LogicalX { get; init; }

    /// <summary>Logical Y in the host coordinate space.</summary>
    [JsonPropertyName("logicalY")]
    public float LogicalY { get; init; }

    /// <summary>Relative X delta from last sample (trackpad/relative mode).</summary>
    [JsonPropertyName("dx")]
    public float Dx { get; init; }

    /// <summary>Relative Y delta from last sample.</summary>
    [JsonPropertyName("dy")]
    public float Dy { get; init; }

    /// <summary>Normalised pressure [0.0, 1.0]. 0 when not available.</summary>
    [JsonPropertyName("pressure")]
    public float Pressure { get; init; }

    /// <summary>Hover distance above surface [0.0, 1.0], null when not available.</summary>
    [JsonPropertyName("hoverDistance")]
    public float? HoverDistance { get; init; }

    /// <summary>Stylus tilt X in degrees, null when not available.</summary>
    [JsonPropertyName("tiltX")]
    public float? TiltX { get; init; }

    /// <summary>Stylus tilt Y in degrees, null when not available.</summary>
    [JsonPropertyName("tiltY")]
    public float? TiltY { get; init; }

    /// <summary>Stylus orientation in degrees [0, 360), null when not available.</summary>
    [JsonPropertyName("orientation")]
    public float? Orientation { get; init; }

    /// <summary>
    /// Bitmask of currently pressed buttons.
    /// Bit 0 = primary (tip), Bit 1 = barrel primary, Bit 2 = barrel secondary, Bit 3 = eraser.
    /// </summary>
    [JsonPropertyName("buttonMask")]
    public int ButtonMask { get; init; }

    /// <summary>Stream mapping ID this sample belongs to (matches DesktopStreamDescriptor.StreamMappingId).</summary>
    [JsonPropertyName("streamMappingId")]
    public string? StreamMappingId { get; init; }

    /// <summary>
    /// Historical coalesced samples from between the last frame and this one.
    /// Null or empty for primary samples that have no history.
    /// </summary>
    [JsonPropertyName("coalescedHistory")]
    public List<DesktopPointerSample>? CoalescedHistory { get; init; }
}
