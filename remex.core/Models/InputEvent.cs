using System.Text.Json.Serialization;

namespace Remex.Core.Models;

/// <summary>
/// One remote input action sent phone → host on the Remote Desktop channel, as the
/// <c>inputEvent</c> payload of a <c>desktop_input</c> message.
/// </summary>
/// <remarks>
/// <para>
/// Every field except <see cref="EventType"/> is optional, because this one record carries eight
/// different actions (see <see cref="InputEventTypes"/>) and each reads a different subset. The host
/// dispatches on <see cref="EventType"/> and IGNORES the event outright if the fields that action
/// needs are absent — <c>mouseDown</c> without <see cref="Button"/> does nothing at all rather than
/// defaulting to the left button. Populate what the action requires.
/// </para>
/// <para>
/// Events are queued host-side and drained by a single worker, so ordering within a connection is
/// preserved. The queue is bounded: under a flood the host drops events and logs, it does not block
/// the socket, so a client must not assume every event it sends is delivered.
/// </para>
/// </remarks>
public record InputEvent
{
    /// <summary>
    /// Which action this is. One of the <see cref="InputEventTypes"/> constants; an unrecognised
    /// value is silently ignored by the host.
    /// </summary>
    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = string.Empty;

    /// <summary>
    /// Absolute pointer X in VIRTUAL-DESKTOP coordinates — the multi-monitor space, not coordinates
    /// within the streamed image and not coordinates relative to the captured display.
    /// </summary>
    /// <remarks>
    /// The CLIENT is responsible for adding the host-reported display origin
    /// (<see cref="DesktopDisplayInfo.Left"/>/<see cref="DesktopDisplayInfo.Top"/>) before sending.
    /// The host deliberately does not add it again: doing so double-applied the offset and, on a
    /// secondary monitor with a non-zero origin, drove the cursor off-screen where the OS clamped it
    /// to the desktop edge. On the primary monitor the origin is (0,0), which is why that bug was
    /// invisible there. The host clamps the result to the active display's bounds.
    /// </remarks>
    [JsonPropertyName("x")]
    public int? X { get; init; }

    /// <summary>Absolute pointer Y in virtual-desktop coordinates. See <see cref="X"/>.</summary>
    [JsonPropertyName("y")]
    public int? Y { get; init; }

    /// <summary>
    /// Mouse button INDEX, not a platform button code. 0 is left on both hosts; <b>1 and 2 are NOT
    /// agreed on</b> — see the remarks before choosing a value.
    /// </summary>
    /// <remarks>
    /// The two hosts currently disagree, and this record cannot fix that by declaring a winner:
    /// <list type="bullet">
    /// <item><description>Windows: <c>0</c> left, <c>1</c> MIDDLE, <c>2</c> RIGHT; anything else
    /// falls back to left.</description></item>
    /// <item><description>Linux: <c>0</c> left, <c>1</c> RIGHT, <c>2</c> MIDDLE, <c>3</c> side,
    /// <c>4</c> extra.</description></item>
    /// </list>
    /// So <c>1</c> means middle-click on a Windows host and right-click on a Linux one. The Android
    /// client is not self-consistent about it either. Filed as RemEx-kie3; until that lands, treat
    /// only <c>0</c> as portable and check the host platform before sending anything else.
    /// </remarks>
    [JsonPropertyName("button")]
    public int? Button { get; init; }

    /// <summary>
    /// Key identity as a WIN32 VIRTUAL-KEY code (<c>0x08</c> Backspace, <c>0x0D</c> Enter,
    /// <c>0x1B</c> Escape, <c>0x25</c>–<c>0x28</c> arrows …), regardless of which platform the host
    /// runs on — this is a protocol-level encoding, not the sending device's native keymap.
    /// </summary>
    /// <remarks>
    /// The Linux host translates these to evdev keycodes in <c>LinuxInputEventTranslator</c>; the
    /// Windows host passes them through. An Android client therefore has to map its own key events
    /// INTO this encoding rather than sending Android key codes.
    /// </remarks>
    [JsonPropertyName("keyCode")]
    public int? KeyCode { get; init; }

    /// <summary>
    /// Literal text to type, for <see cref="InputEventTypes.TypeText"/>. Exists so clients can send
    /// characters they cannot express as a virtual-key code — IME output, emoji, and anything a
    /// software keyboard produces without a physical key behind it.
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>
    /// Horizontal delta. Its meaning depends on <see cref="EventType"/>: for
    /// <see cref="InputEventTypes.MouseScroll"/> it is a scroll amount; for
    /// <see cref="InputEventTypes.MouseMove"/> it is a RELATIVE pointer movement in pixels, used when
    /// <see cref="X"/>/<see cref="Y"/> are absent.
    /// </summary>
    /// <remarks>
    /// That overload is the one thing about this record worth reading twice: <c>mouseMove</c> is
    /// absolute when X/Y are present and relative when they are not, so sending all four fields at
    /// once is ambiguous — absolute wins, and the deltas are ignored.
    /// </remarks>
    [JsonPropertyName("deltaX")]
    public int? DeltaX { get; init; }

    /// <summary>Vertical delta. See <see cref="DeltaX"/> for the scroll/relative-move overload.</summary>
    [JsonPropertyName("deltaY")]
    public int? DeltaY { get; init; }
}

/// <summary>
/// Legal values for <see cref="InputEvent.EventType"/>, and the fields each one reads.
/// </summary>
/// <remarks>
/// Wire strings, so they are part of the protocol contract: changing one silently breaks every
/// shipped client, because the host ignores event types it does not recognise rather than erroring.
/// The names are their own documentation; what is NOT obvious is which fields each requires, which
/// is recorded per constant below.
/// </remarks>
public static class InputEventTypes
{
    /// <summary>
    /// Move the pointer. Absolute when <c>X</c>/<c>Y</c> are supplied, relative when only
    /// <c>DeltaX</c>/<c>DeltaY</c> are.
    /// </summary>
    public const string MouseMove = "mouseMove";

    /// <summary>Press and hold <c>Button</c>. Moves to <c>X</c>/<c>Y</c> first when supplied.</summary>
    public const string MouseDown = "mouseDown";

    /// <summary>Release <c>Button</c>. Does NOT move first, so it releases wherever the pointer is.</summary>
    public const string MouseUp = "mouseUp";

    /// <summary>Press and release <c>Button</c>. Moves to <c>X</c>/<c>Y</c> first when supplied.</summary>
    public const string MouseClick = "mouseClick";

    /// <summary>Scroll by <c>DeltaX</c>/<c>DeltaY</c>. Ignores <c>X</c>/<c>Y</c>.</summary>
    public const string MouseScroll = "mouseScroll";

    /// <summary>Press and hold <c>KeyCode</c>. The client owns releasing it.</summary>
    public const string KeyDown = "keyDown";

    /// <summary>Release <c>KeyCode</c>.</summary>
    public const string KeyUp = "keyUp";

    /// <summary>Type <c>Text</c> verbatim, for characters no virtual-key code can express.</summary>
    public const string TypeText = "typeText";
}
