using System.Runtime.Versioning;

namespace Remex.Host.Services.Input.Linux;

/// <summary>
/// Translates between human-readable key names and Linux input keycodes.
/// Used by <see cref="LinuxInputBackendRouter"/> to convert the key names
/// sent from the Android client into the Linux KEY_* constants required by
/// the EIS sender.
///
/// Key names follow the XKB / xdotool convention (e.g., "ctrl", "shift",
/// "Return", "BackSpace", "a", "F1", "KP_Enter").
/// </summary>
[SupportedOSPlatform("linux")]
public static class LinuxInputEventTranslator
{
    // Linux KEY_* constants for common keys.
    // Only the subset needed for RemEx remote-control operations is included here.
    // See /usr/include/linux/input-event-codes.h for the full list.
    private static readonly System.Collections.Generic.Dictionary<string, int> XkbToKeycode = new(
        System.StringComparer.OrdinalIgnoreCase)
    {
        // Modifier keys
        ["shift"] = 42,    // KEY_LEFTSHIFT
        ["lshift"] = 42,
        ["rshift"] = 54,    // KEY_RIGHTSHIFT
        ["ctrl"] = 29,    // KEY_LEFTCTRL
        ["lctrl"] = 29,
        ["rctrl"] = 97,    // KEY_RIGHTCTRL
        ["alt"] = 56,    // KEY_LEFTALT
        ["lalt"] = 56,
        ["ralt"] = 100,   // KEY_RIGHTALT
        ["super"] = 125,   // KEY_LEFTMETA
        ["meta"] = 125,
        ["lmeta"] = 125,
        ["rmeta"] = 126,   // KEY_RIGHTMETA
        ["capslock"] = 58,    // KEY_CAPSLOCK

        // Navigation
        ["return"] = 28,    // KEY_ENTER
        ["enter"] = 28,
        ["kp_enter"] = 96,    // KEY_KPENTER
        ["backspace"] = 14,    // KEY_BACKSPACE
        ["tab"] = 15,    // KEY_TAB
        ["escape"] = 1,     // KEY_ESC
        ["esc"] = 1,
        ["delete"] = 111,   // KEY_DELETE
        ["del"] = 111,
        ["insert"] = 110,   // KEY_INSERT
        ["ins"] = 110,
        ["home"] = 102,   // KEY_HOME
        ["end"] = 107,   // KEY_END
        ["pageup"] = 104,   // KEY_PAGEUP
        ["prior"] = 104,
        ["pagedown"] = 109,   // KEY_PAGEDOWN
        ["next"] = 109,
        ["up"] = 103,   // KEY_UP
        ["down"] = 108,   // KEY_DOWN
        ["left"] = 105,   // KEY_LEFT
        ["right"] = 106,   // KEY_RIGHT

        // Function keys
        ["f1"] = 59,
        ["f2"] = 60,
        ["f3"] = 61,
        ["f4"] = 62,
        ["f5"] = 63,
        ["f6"] = 64,
        ["f7"] = 65,
        ["f8"] = 66,
        ["f9"] = 67,
        ["f10"] = 68,
        ["f11"] = 87,
        ["f12"] = 88,

        // Printable — top row
        ["grave"] = 41,   // `
        ["1"] = 2,
        ["2"] = 3,
        ["3"] = 4,
        ["4"] = 5,
        ["5"] = 6,
        ["6"] = 7,
        ["7"] = 8,
        ["8"] = 9,
        ["9"] = 10,
        ["0"] = 11,
        ["minus"] = 12,
        ["equal"] = 13,

        // Second row
        ["q"] = 16,
        ["w"] = 17,
        ["e"] = 18,
        ["r"] = 19,
        ["t"] = 20,
        ["y"] = 21,
        ["u"] = 22,
        ["i"] = 23,
        ["o"] = 24,
        ["p"] = 25,
        ["bracketleft"] = 26,  // [
        ["bracketright"] = 27,  // ]
        ["backslash"] = 43,

        // Third row
        ["a"] = 30,
        ["s"] = 31,
        ["d"] = 32,
        ["f"] = 33,
        ["g"] = 34,
        ["h"] = 35,
        ["j"] = 36,
        ["k"] = 37,
        ["l"] = 38,
        ["semicolon"] = 39,
        ["apostrophe"] = 40,

        // Fourth row
        ["z"] = 44,
        ["x"] = 45,
        ["c"] = 46,
        ["v"] = 47,
        ["b"] = 48,
        ["n"] = 49,
        ["m"] = 50,
        ["comma"] = 51,
        ["period"] = 52,
        ["slash"] = 53,

        // Misc
        ["space"] = 57,
        ["print"] = 99,   // KEY_SYSRQ
        ["scrolllock"] = 70,
        ["pause"] = 119,
    };

    /// <summary>
    /// Returns the Linux keycode for a given XKB / xdotool key name,
    /// or -1 if not found.
    /// </summary>
    public static int XkbNameToLinuxKeycode(string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return -1;
        return XkbToKeycode.TryGetValue(keyName, out var code) ? code : -1;
    }

    /// <summary>
    /// Converts a <see cref="DesktopPointerSample"/> button index to a Linux BTN_ code.
    /// Index 0 = BTN_LEFT, 1 = BTN_RIGHT, 2 = BTN_MIDDLE.
    /// </summary>
    public static uint ButtonIndexToLinuxCode(int index) => index switch
    {
        0 => 272u,   // BTN_LEFT
        1 => 273u,   // BTN_RIGHT
        2 => 274u,   // BTN_MIDDLE
        3 => 275u,   // BTN_SIDE
        4 => 276u,   // BTN_EXTRA
        _ => 272u,
    };
}
