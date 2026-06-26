using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Remex.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<DesktopCaptureMode>))]
public enum DesktopCaptureMode
{
    VirtualDesktop,
    Monitor,
}

public sealed record DesktopClientCapabilities
{
    [JsonPropertyName("supportsDisplaySelection")]
    public bool SupportsDisplaySelection { get; init; }

    [JsonPropertyName("supportsFrameEnvelope")]
    public bool SupportsFrameEnvelope { get; init; }

    [JsonPropertyName("supportsTargetSwitch")]
    public bool SupportsTargetSwitch { get; init; }

    [JsonPropertyName("supportsCursorState")]
    public bool SupportsCursorState { get; init; }

    [JsonPropertyName("supportsCursorShape")]
    public bool SupportsCursorShape { get; init; }

    // RD-E: client can receive cursor POSITION as a binary "RDXC" packet over the desktop binary channel
    // instead of a JSON desktop_cursor_state message (cuts GC churn at 60–90 Hz). Position only; the cursor
    // shape still arrives as JSON. Default false keeps older hosts/clients on the JSON path. See
    // DesktopCursorBinaryEnvelope.
    [JsonPropertyName("supportsBinaryCursor")]
    public bool SupportsBinaryCursor { get; init; }
}

public sealed record DesktopDisplayInfo
{
    [JsonPropertyName("displayId")]
    public string DisplayId { get; init; } = string.Empty;

    [JsonPropertyName("persistentDisplayKey")]
    public string PersistentDisplayKey { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("isPrimary")]
    public bool IsPrimary { get; init; }

    [JsonPropertyName("left")]
    public int Left { get; init; }

    [JsonPropertyName("top")]
    public int Top { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }
}

public sealed record DesktopDisplayCatalog
{
    [JsonPropertyName("displayListVersion")]
    public int DisplayListVersion { get; init; }

    [JsonPropertyName("supportedCaptureModes")]
    public IReadOnlyList<DesktopCaptureMode> SupportedCaptureModes { get; init; } = [];

    [JsonPropertyName("displays")]
    public IReadOnlyList<DesktopDisplayInfo> Displays { get; init; } = [];
}

public sealed record DesktopCaptureTarget
{
    [JsonPropertyName("captureMode")]
    public DesktopCaptureMode CaptureMode { get; init; } = DesktopCaptureMode.VirtualDesktop;

    [JsonPropertyName("displayId")]
    public string? DisplayId { get; init; }
}

public sealed record DesktopTargetSwitchRequest
{
    [JsonPropertyName("target")]
    public DesktopCaptureTarget Target { get; init; } = new();

    [JsonPropertyName("displayListVersion")]
    public int? DisplayListVersion { get; init; }
}

public sealed record DesktopCursorState
{
    [JsonPropertyName("cursorSerial")]
    public long CursorSerial { get; init; }

    [JsonPropertyName("streamSerial")]
    public long StreamSerial { get; init; }

    [JsonPropertyName("x")]
    public int X { get; init; }

    [JsonPropertyName("y")]
    public int Y { get; init; }

    [JsonPropertyName("visible")]
    public bool Visible { get; init; } = true;

    [JsonPropertyName("shapeSerial")]
    public long ShapeSerial { get; init; }

    [JsonPropertyName("hotspotX")]
    public int HotspotX { get; init; }

    [JsonPropertyName("hotspotY")]
    public int HotspotY { get; init; }
}

public sealed record DesktopCursorShape
{
    [JsonPropertyName("shapeSerial")]
    public long ShapeSerial { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("hotspotX")]
    public int HotspotX { get; init; }

    [JsonPropertyName("hotspotY")]
    public int HotspotY { get; init; }

    [JsonPropertyName("pixelFormat")]
    public string PixelFormat { get; init; } = "bgra8888";

    [JsonPropertyName("shapeBytes")]
    public byte[] ShapeBytes { get; init; } = [];
}
