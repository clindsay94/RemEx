using System;
using System.Text.Json.Serialization;

namespace Remex.Core.Models;

[Flags]
public enum DesktopStylusCapabilities
{
    None = 0,
    Pressure = 1 << 0,
    Tilt = 1 << 1,
    Hover = 1 << 2,
    BarrelButton = 1 << 3,
    Eraser = 1 << 4,
}

public record DesktopMeta
{
    // ── Legacy fields (maintained for backward compatibility) ──────────────

    [JsonPropertyName("screenWidth")]
    public int ScreenWidth { get; init; }

    [JsonPropertyName("screenHeight")]
    public int ScreenHeight { get; init; }

    [JsonPropertyName("desktopLeft")]
    public int DesktopLeft { get; init; }

    [JsonPropertyName("desktopTop")]
    public int DesktopTop { get; init; }

    /// <summary>
    /// Unique identifier for the host process instance.
    /// Used to detect self-connections (infinite mirror prevention).
    /// </summary>
    [JsonPropertyName("hostInstanceId")]
    public string? HostInstanceId { get; init; }

    /// <summary>Current mouse cursor X position (for trackpad mode visibility).</summary>
    [JsonPropertyName("cursorX")]
    public int CursorX { get; init; }

    /// <summary>Current mouse cursor Y position (for trackpad mode visibility).</summary>
    [JsonPropertyName("cursorY")]
    public int CursorY { get; init; }

    /// <summary>
    /// True when the cursor lies within the currently captured surface (so the client should
    /// draw its cursor overlay). False when the pointer is on a monitor outside the active
    /// capture target (e.g. single-monitor capture while the cursor is on another display),
    /// so the overlay can be hidden instead of clamped to the frame edge.
    /// Defaults true for backward compatibility with legacy hosts that omit it.
    /// </summary>
    [JsonPropertyName("cursorVisible")]
    public bool CursorVisible { get; init; } = true;

    // ── Stage 3 additions ─────────────────────────────────────────────────

    /// <summary>
    /// Name of the active screen-capture backend (e.g. "PipeWire/DMA-BUF", "PipeWire/memfd",
    /// "DXGI", "Unknown"). Null on legacy hosts that have not been updated.
    /// </summary>
    [JsonPropertyName("captureBackend")]
    public string? CaptureBackend { get; init; }

    /// <summary>
    /// Name of the active input backend (e.g. "libei", "Portal/Notify",
    /// "uinput", "xdotool", "Win32"). Null on legacy hosts.
    /// </summary>
    [JsonPropertyName("inputBackend")]
    public string? InputBackend { get; init; }

    /// <summary>
    /// Opaque stream surface identifier shared with DesktopStreamDescriptor and
    /// DesktopPointerSample. Null on legacy hosts.
    /// </summary>
    [JsonPropertyName("streamMappingId")]
    public string? StreamMappingId { get; init; }

    /// <summary>
    /// Logical width of the capture surface in host coordinate-space units.
    /// May differ from ScreenWidth when DPI scaling is active.
    /// 0 on legacy hosts (treat ScreenWidth as logical width in that case).
    /// </summary>
    [JsonPropertyName("logicalWidth")]
    public int LogicalWidth { get; init; }

    /// <summary>Logical height of the capture surface. 0 on legacy hosts.</summary>
    [JsonPropertyName("logicalHeight")]
    public int LogicalHeight { get; init; }

    /// <summary>Actual pixel width of frames the host is sending. 0 on legacy hosts.</summary>
    [JsonPropertyName("pixelWidth")]
    public int PixelWidth { get; init; }

    /// <summary>Actual pixel height of frames the host is sending. 0 on legacy hosts.</summary>
    [JsonPropertyName("pixelHeight")]
    public int PixelHeight { get; init; }

    /// <summary>
    /// Monotonically increasing stream serial. Incremented on session restart or
    /// monitor change. Clients should reset pointer state when this changes.
    /// 0 on legacy hosts.
    /// </summary>
    [JsonPropertyName("streamSerial")]
    public long StreamSerial { get; init; }

    /// <summary>
    /// Active cursor delivery mode: "absolute", "relative", or "stylus".
    /// Null on legacy hosts (treat as "absolute").
    /// </summary>
    [JsonPropertyName("cursorMode")]
    public string? CursorMode { get; init; }

    /// <summary>
    /// Codec currently in use for this stream.
    /// Null on legacy hosts (assume Mjpeg).
    /// </summary>
    [JsonPropertyName("codecInfo")]
    public DesktopCodecInfo? CodecInfo { get; init; }

    /// <summary>
    /// Bitmask of stylus capabilities the host input backend can accept.
    /// Zero on non-stylus hosts or legacy hosts.
    /// </summary>
    [JsonPropertyName("stylusCapabilities")]
    public DesktopStylusCapabilities StylusCapabilities { get; init; }
}
