using System.Text.Json.Serialization;

namespace Remex.Core.Models;

/// <summary>
/// What the Remote Desktop stream is capturing: the whole virtual desktop, or one monitor.
/// </summary>
/// <remarks>
/// Serialised by NAME, not by ordinal (<see cref="JsonStringEnumConverter{T}"/>), so the wire form is
/// <c>"VirtualDesktop"</c> / <c>"Monitor"</c>. Reordering the members is therefore safe, but renaming
/// one is a breaking protocol change.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<DesktopCaptureMode>))]
public enum DesktopCaptureMode
{
    /// <summary>Capture every monitor as one image, in virtual-desktop coordinates.</summary>
    VirtualDesktop,

    /// <summary>Capture a single monitor, named by <see cref="DesktopCaptureTarget.DisplayId"/>.</summary>
    Monitor,
}

/// <summary>
/// What the connecting client can handle, sent client → host when the desktop stream opens.
/// </summary>
/// <remarks>
/// Pure feature negotiation, and every flag DEFAULTS TO FALSE on purpose: an older client that has
/// never heard of a feature omits the field, JSON gives the host <c>false</c>, and the host stays on
/// the path that client understands. That is what lets the host add a stream feature without a
/// <c>protocolVersion</c> bump — so a new flag must always mean "I additionally support X", never
/// "I do not support the old X".
/// </remarks>
public sealed record DesktopClientCapabilities
{
    /// <summary>Client can ask for a specific monitor rather than only the whole desktop.</summary>
    [JsonPropertyName("supportsDisplaySelection")]
    public bool SupportsDisplaySelection { get; init; }

    /// <summary>Client understands the binary frame envelope rather than bare image payloads.</summary>
    [JsonPropertyName("supportsFrameEnvelope")]
    public bool SupportsFrameEnvelope { get; init; }

    /// <summary>Client can switch capture target mid-stream instead of reconnecting.</summary>
    [JsonPropertyName("supportsTargetSwitch")]
    public bool SupportsTargetSwitch { get; init; }

    /// <summary>Client draws the cursor itself from <see cref="DesktopCursorState"/> updates.</summary>
    [JsonPropertyName("supportsCursorState")]
    public bool SupportsCursorState { get; init; }

    /// <summary>Client can render the cursor bitmap from <see cref="DesktopCursorShape"/>.</summary>
    [JsonPropertyName("supportsCursorShape")]
    public bool SupportsCursorShape { get; init; }

    /// <summary>
    /// Client can receive cursor POSITION as a binary <c>RDXC</c> packet on the desktop binary
    /// channel instead of a JSON <c>desktop_cursor_state</c> message, which cuts GC churn at 60–90 Hz.
    /// Position only — the cursor SHAPE still arrives as JSON either way. Default false keeps older
    /// hosts and clients on the JSON path. See <c>DesktopCursorBinaryEnvelope</c>. (RD-E.)
    /// </summary>
    [JsonPropertyName("supportsBinaryCursor")]
    public bool SupportsBinaryCursor { get; init; }
}

/// <summary>One monitor, as the host sees it.</summary>
public sealed record DesktopDisplayInfo
{
    /// <summary>
    /// Handle used to name this display in requests, valid only for the CURRENT host session.
    /// </summary>
    /// <remarks>
    /// Session-scoped by construction — it comes from the OS's own enumeration, which renumbers when
    /// monitors are added, removed or replugged. Do not persist it; see
    /// <see cref="PersistentDisplayKey"/> for the value that survives.
    /// </remarks>
    [JsonPropertyName("displayId")]
    public string DisplayId { get; init; } = string.Empty;

    /// <summary>
    /// Stable identity for this physical monitor, intended to survive reboots and re-plugging.
    /// </summary>
    /// <remarks>
    /// Intended for the client that wants to reselect "the monitor I was watching last time".
    /// Resolve it back to a live <see cref="DisplayId"/> against a fresh
    /// <see cref="DesktopDisplayCatalog"/> before asking to capture it.
    /// <para>
    /// THE GUARANTEE DOES NOT CURRENTLY HOLD ON WINDOWS, so do not rely on it yet. Linux derives it
    /// from the display's own identity (its UUID where available, otherwise the output name), which
    /// is stable. The Windows host assigns it the SAME string as <see cref="DisplayId"/> — the device
    /// name, or a <c>monitor-N</c> ordinal fallback — so there it is exactly as session-scoped as the
    /// id it is supposed to outlive. Filed as RemEx-zftu.
    /// </para>
    /// </remarks>
    [JsonPropertyName("persistentDisplayKey")]
    public string PersistentDisplayKey { get; init; } = string.Empty;

    /// <summary>Human-readable label for pickers. Not unique and not an identifier.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Whether this is the OS's primary display.</summary>
    [JsonPropertyName("isPrimary")]
    public bool IsPrimary { get; init; }

    /// <summary>
    /// X origin in VIRTUAL-DESKTOP coordinates, which is why it can be negative: a monitor placed to
    /// the left of the primary has a negative <see cref="Left"/>.
    /// </summary>
    /// <remarks>
    /// A client mapping a touch to <see cref="InputEvent.X"/> must add this origin, because the host
    /// deliberately does not — see the remarks on <see cref="InputEvent.X"/> for what happened when
    /// both ends added it.
    /// </remarks>
    [JsonPropertyName("left")]
    public int Left { get; init; }

    /// <summary>Y origin in virtual-desktop coordinates; may be negative. See <see cref="Left"/>.</summary>
    [JsonPropertyName("top")]
    public int Top { get; init; }

    /// <summary>Width in pixels.</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>Height in pixels.</summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }
}

/// <summary>The host's current display layout, plus the token that says when it changed.</summary>
public sealed record DesktopDisplayCatalog
{
    /// <summary>
    /// Fingerprint of the layout this catalog describes. Send it back in
    /// <see cref="DesktopTargetSwitchRequest.DisplayListVersion"/> to make a target switch fail
    /// rather than act on a layout that has since changed.
    /// </summary>
    /// <remarks>
    /// A HASH of the displays' ids, bounds and primary flags — not a counter. Two consequences worth
    /// knowing: it changes when a monitor MOVES, not only when one is added or removed; and it can
    /// return to a previous value if the user undoes a layout change, so it answers "is this the same
    /// layout?" and never "which is newer?".
    /// </remarks>
    [JsonPropertyName("displayListVersion")]
    public int DisplayListVersion { get; init; }

    /// <summary>Capture modes this host offers. A client must not request one that is absent.</summary>
    [JsonPropertyName("supportedCaptureModes")]
    public IReadOnlyList<DesktopCaptureMode> SupportedCaptureModes { get; init; } = [];

    /// <summary>Every display the host can capture.</summary>
    [JsonPropertyName("displays")]
    public IReadOnlyList<DesktopDisplayInfo> Displays { get; init; } = [];
}

/// <summary>What to capture.</summary>
public sealed record DesktopCaptureTarget
{
    /// <summary>Whole virtual desktop (the default) or one monitor.</summary>
    [JsonPropertyName("captureMode")]
    public DesktopCaptureMode CaptureMode { get; init; } = DesktopCaptureMode.VirtualDesktop;

    /// <summary>
    /// Which monitor, when <see cref="CaptureMode"/> is <see cref="DesktopCaptureMode.Monitor"/>.
    /// A session-scoped <see cref="DesktopDisplayInfo.DisplayId"/>, not a persistent key. Ignored for
    /// <see cref="DesktopCaptureMode.VirtualDesktop"/>.
    /// </summary>
    [JsonPropertyName("displayId")]
    public string? DisplayId { get; init; }
}

/// <summary>Client → host request to retarget a running stream without reconnecting.</summary>
public sealed record DesktopTargetSwitchRequest
{
    /// <summary>The new capture target.</summary>
    [JsonPropertyName("target")]
    public DesktopCaptureTarget Target { get; init; } = new();

    /// <summary>
    /// The <see cref="DesktopDisplayCatalog.DisplayListVersion"/> the client's display list was built
    /// from. Optional, but supplying it is what makes the switch safe.
    /// </summary>
    /// <remarks>
    /// When present and no longer current, the host REFUSES the switch and says the display list
    /// changed — instead of resolving a now-stale <see cref="DesktopCaptureTarget.DisplayId"/> to
    /// whichever monitor happens to hold that id today. Omitting it opts out of that protection.
    /// </remarks>
    [JsonPropertyName("displayListVersion")]
    public int? DisplayListVersion { get; init; }
}

/// <summary>
/// Where the host's cursor is, so a client can draw it locally at pointer rate instead of waiting
/// for it to appear in a video frame.
/// </summary>
/// <remarks>
/// Carries THREE different serials, which is the confusing part of this record: <see cref="StreamSerial"/>
/// identifies the stream configuration, <see cref="ShapeSerial"/> identifies the bitmap, and
/// <see cref="CursorSerial"/> orders the position updates themselves.
/// </remarks>
public sealed record DesktopCursorState
{
    /// <summary>Monotonic counter for this position update, to drop ones that arrive out of order.</summary>
    [JsonPropertyName("cursorSerial")]
    public long CursorSerial { get; init; }

    /// <summary>
    /// Which stream CONFIGURATION this position belongs to. Discard the update if it does not match
    /// the stream currently being rendered.
    /// </summary>
    /// <remarks>
    /// The host bumps this whenever the capture target changes, and it is authoritative: it also
    /// stamps frames, so a client can drop pixels and cursor positions captured under the previous
    /// target instead of briefly drawing the old monitor's cursor over the new monitor's image.
    /// Seeded from a wall-clock millisecond value and then forced to increase, so it is monotonic
    /// within a session but not a count of anything.
    /// </remarks>
    [JsonPropertyName("streamSerial")]
    public long StreamSerial { get; init; }

    /// <summary>Cursor X in the captured target's coordinate space.</summary>
    [JsonPropertyName("x")]
    public int X { get; init; }

    /// <summary>Cursor Y in the captured target's coordinate space.</summary>
    [JsonPropertyName("y")]
    public int Y { get; init; }

    /// <summary>Whether the cursor is currently shown; false while it is hidden.</summary>
    [JsonPropertyName("visible")]
    public bool Visible { get; init; } = true;

    /// <summary>
    /// Which <see cref="DesktopCursorShape"/> is current. When this changes, the client's cached
    /// bitmap is stale and the host will send a new shape.
    /// </summary>
    [JsonPropertyName("shapeSerial")]
    public long ShapeSerial { get; init; }

    /// <summary>
    /// X of the shape's hotspot, repeated here so the client can position the cursor correctly
    /// without having received the bitmap yet.
    /// </summary>
    [JsonPropertyName("hotspotX")]
    public int HotspotX { get; init; }

    /// <summary>Y of the shape's hotspot. See <see cref="HotspotX"/>.</summary>
    [JsonPropertyName("hotspotY")]
    public int HotspotY { get; init; }
}

/// <summary>
/// The cursor bitmap itself, sent only when it changes rather than with every position update.
/// </summary>
public sealed record DesktopCursorShape
{
    /// <summary>
    /// Identity of this bitmap. Clients cache by this value and redraw when
    /// <see cref="DesktopCursorState.ShapeSerial"/> names one they do not hold.
    /// </summary>
    [JsonPropertyName("shapeSerial")]
    public long ShapeSerial { get; init; }

    /// <summary>Bitmap width in pixels.</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>Bitmap height in pixels.</summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }

    /// <summary>
    /// X of the point within the bitmap that sits ON the cursor position — the arrow's tip, not its
    /// top-left. Draw at <c>(state.X - hotspotX, state.Y - hotspotY)</c>.
    /// </summary>
    [JsonPropertyName("hotspotX")]
    public int HotspotX { get; init; }

    /// <summary>Y of the hotspot. See <see cref="HotspotX"/>.</summary>
    [JsonPropertyName("hotspotY")]
    public int HotspotY { get; init; }

    /// <summary>
    /// Pixel layout of <see cref="ShapeBytes"/>. Currently always <c>bgra8888</c>; present so the
    /// format can change without a protocol break, so clients should check rather than assume.
    /// </summary>
    [JsonPropertyName("pixelFormat")]
    public string PixelFormat { get; init; } = "bgra8888";

    /// <summary>
    /// Raw pixels, <see cref="Width"/> × <see cref="Height"/> in <see cref="PixelFormat"/>. Base64 on
    /// the wire, as a JSON byte array.
    /// </summary>
    [JsonPropertyName("shapeBytes")]
    public byte[] ShapeBytes { get; init; } = [];
}
