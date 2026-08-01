using Remex.Core.Models;

namespace Remex.Agent.Services.Input;

/// <summary>
/// The single place a protocol mouse-button index becomes a platform button code.
/// </summary>
/// <remarks>
/// <para>
/// There were SIX independent copies of these tables before RemEx-upxn — three in
/// <c>LinuxInputSimulationService</c>, two in <c>LinuxInputBackendRouter</c>, one (twice, for down
/// and up) in <c>WindowsInputSimulationService</c> — plus a seventh in
/// <c>LinuxInputEventTranslator</c>. They agreed only because RemEx-kie3 had just corrected the one
/// that did not, and nothing but a test comparing source text stood between them and the next drift.
/// </para>
/// <para>
/// THE FAILURE THIS PREVENTS IS SPECIFIC AND HAS HAPPENED. A wrong entry in one of these tables does
/// not crash and does not fail to work — it silently performs the WRONG CLICK. RemEx-kie3 was exactly
/// that: a left click that opened the middle-click behaviour instead. With one table, a mistake is
/// wrong everywhere at once, which is far easier to notice than wrong on one backend only.
/// </para>
/// <para>
/// Deliberately NOT in <c>Remex.Core</c>, unlike <see cref="MouseButtons"/>: evdev codes and xdotool
/// numbering are host implementation details that the Android client neither knows nor needs. Only
/// the index vocabulary is shared.
/// </para>
/// </remarks>
internal static class MouseButtonCodes
{
    // Linux input-event-codes.h. Note BTN_MIDDLE and BTN_RIGHT are NOT in index order — 274 is
    // middle and 273 is right - which is precisely the sort of detail that gets transcribed wrong
    // when it is written out six times.
    private const uint BtnLeft = 272;   // 0x110
    private const uint BtnRight = 273;  // 0x111
    private const uint BtnMiddle = 274; // 0x112
    private const uint BtnSide = 275;   // 0x113
    private const uint BtnExtra = 276;  // 0x114

    /// <summary>
    /// Maps a protocol button index to a Linux evdev <c>BTN_*</c> code, used by the portal injector
    /// and the EIS backend.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOT ydotool, despite what this summary used to say — see <see cref="ToYdotool"/>. That claim
    /// was the whole of RemEx-nb7c: it made feeding a 0x110-range evdev code to <c>ydotool click</c>
    /// look correct, when that command wants its own small button index OR'd with a press/release
    /// bit.
    /// </para>
    /// <para>
    /// Unknown indices fall back to <c>BTN_LEFT</c> rather than throwing, matching every table this
    /// replaced. A malformed index from the wire should produce an ordinary click, not tear down the
    /// input path.
    /// </para>
    /// </remarks>
    internal static uint ToEvdev(int button) => button switch
    {
        MouseButtons.Left => BtnLeft,
        MouseButtons.Middle => BtnMiddle,
        MouseButtons.Right => BtnRight,
        MouseButtons.Side => BtnSide,
        MouseButtons.Extra => BtnExtra,
        _ => BtnLeft,
    };

    /// <summary>Bit that tells <c>ydotool click</c> to press. Man page: <c>0x40 - Mouse down</c>.</summary>
    internal const int YdotoolDown = 0x40;

    /// <summary>Bit that tells <c>ydotool click</c> to release. Man page: <c>0x80 - Mouse up</c>.</summary>
    internal const int YdotoolUp = 0x80;

    /// <summary>
    /// Maps a protocol button index to the button number <c>ydotool click</c> expects, WITHOUT the
    /// press/release bit — OR in <see cref="YdotoolDown"/> or <see cref="YdotoolUp"/> to get a
    /// complete argument.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ITS ORDER IS NOT THIS REPO'S ORDER, which is the trap. ydotool numbers
    /// <c>0x00 LEFT, 0x01 RIGHT, 0x02 MIDDLE</c>; the protocol vocabulary and every other table here
    /// use <c>0 left, 1 middle, 2 right</c>. Passing the protocol index straight through would
    /// therefore swap right-click and middle-click — silently, and only on this backend, which is
    /// precisely the failure RemEx-kie3 already cost this project once.
    /// </para>
    /// <para>
    /// Cited rather than remembered (RemEx-nb7c). From ydotool's own man page:
    /// <c>0x00 - LEFT, 0x01 - RIGHT, 0x02 - MIDDLE, 0x03 - SIDE, 0x04 - EXTR, 0x05 - FORWARD,
    /// 0x06 - BACK, 0x07 - TASK, 0x40 - Mouse down, 0x80 - Mouse up</c>, with the worked examples
    /// <c>0xC0</c> = left click (down then up), <c>0x41</c> = right button down, <c>0x82</c> =
    /// middle button up. Only the first five are reachable from <see cref="MouseButtons"/>, which
    /// defines no forward, back or task index. The same page notes that <c>0x00</c> — a button with
    /// NEITHER bit set — "chooses left button, but does nothing", which is what the previous
    /// evdev-code argument amounted to.
    /// </para>
    /// <para>
    /// Unknown indices fall back to LEFT, matching <see cref="ToEvdev"/> and <see cref="ToXdotool"/>.
    /// </para>
    /// </remarks>
    internal static int ToYdotool(int button) => button switch
    {
        MouseButtons.Left => 0x00,
        MouseButtons.Right => 0x01,
        MouseButtons.Middle => 0x02,
        MouseButtons.Side => 0x03,
        MouseButtons.Extra => 0x04,
        _ => 0x00,
    };

    /// <summary>
    /// The complete argument for <c>ydotool click</c> — button number OR'd with the press or release
    /// bit, formatted as ydotool expects it.
    /// </summary>
    /// <remarks>
    /// A function rather than two interpolations at the call sites, so the string that actually
    /// reaches the command line is testable. The bug this replaces lived precisely in that
    /// interpolation — an evdev code with a literal 'D' or 'U' stuck on the end — and no test could
    /// see it, because the only thing under test was the mapping the interpolation then misused
    /// (RemEx-nb7c).
    /// </remarks>
    internal static string YdotoolClickArgument(int button, bool pressed) =>
        $"0x{(pressed ? YdotoolDown : YdotoolUp) | ToYdotool(button):X2}";

    /// <summary>
    /// Maps a protocol button index to xdotool's 1-based button numbering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOT 4 AND 5 FOR SIDE AND EXTRA — those are the scroll wheel in X11 (6 and 7 are horizontal
    /// scroll), so the naive completion of this table turns a back-button press into a scroll. Back
    /// and forward are 8 and 9.
    /// </para>
    /// <para>
    /// Both tables this replaced stopped at right and fell back to left for anything else, which
    /// would have left index 3 pressing BTN_SIDE on the portal and ydotool while LEFT-CLICKING on
    /// xdotool — one service, three backends, two answers. Unifying the tables and then leaving that
    /// in place would have widened the exact divergence this change exists to remove, so the missing
    /// arms are filled in rather than pinned. Nothing in the client sends 3 or 4 today
    /// (see <see cref="MouseButtons.Extra"/>), so this costs nothing and closes the gap.
    /// </para>
    /// </remarks>
    internal static int ToXdotool(int button) => button switch
    {
        MouseButtons.Left => 1,
        MouseButtons.Middle => 2,
        MouseButtons.Right => 3,
        MouseButtons.Side => 8,
        MouseButtons.Extra => 9,
        _ => 1,
    };
}
