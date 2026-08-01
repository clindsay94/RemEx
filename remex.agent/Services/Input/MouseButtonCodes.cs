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
    /// Maps a protocol button index to a Linux evdev <c>BTN_*</c> code, used by the portal injector,
    /// the EIS backend and ydotool alike.
    /// </summary>
    /// <remarks>
    /// Unknown indices fall back to <c>BTN_LEFT</c> rather than throwing, matching every table this
    /// replaced. A malformed index from the wire should produce an ordinary click, not tear down the
    /// input path.
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
