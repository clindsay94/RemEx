using System.Runtime.Versioning;
using System.Text;

using Remex.Agent.Services.Input;

namespace Remex.Agent.Services.Input.Linux;

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
    /// Converts a protocol-level key code (raw Win32 virtual-key style code used by
    /// the current RemEx clients) into a Linux evdev keycode suitable for ydotool
    /// and the portal <c>NotifyKeyboardKeycode</c> API.
    /// </summary>
    public static int ProtocolKeyCodeToLinuxKeycode(int keyCode) => keyCode switch
    {
        0x08 => 14,   // Backspace
        0x09 => 15,   // Tab
        0x0D => 28,   // Enter
        0x1B => 1,    // Escape
        0x20 => 57,   // Space
        0x21 => 104,  // PageUp
        0x22 => 109,  // PageDown
        0x23 => 107,  // End
        0x24 => 102,  // Home
        0x25 => 105,  // Left
        0x26 => 103,  // Up
        0x27 => 106,  // Right
        0x28 => 108,  // Down
        0x2D => 110,  // Insert
        0x2E => 111,  // Delete
        0x30 => 11,
        0x31 => 2,
        0x32 => 3,
        0x33 => 4,
        0x34 => 5,
        0x35 => 6,
        0x36 => 7,
        0x37 => 8,
        0x38 => 9,
        0x39 => 10,
        0x41 => 30,
        0x42 => 48,
        0x43 => 46,
        0x44 => 32,
        0x45 => 18,
        0x46 => 33,
        0x47 => 34,
        0x48 => 35,
        0x49 => 23,
        0x4A => 36,
        0x4B => 37,
        0x4C => 38,
        0x4D => 50,
        0x4E => 49,
        0x4F => 24,
        0x50 => 25,
        0x51 => 16,
        0x52 => 19,
        0x53 => 31,
        0x54 => 20,
        0x55 => 22,
        0x56 => 47,
        0x57 => 17,
        0x58 => 45,
        0x59 => 21,
        0x5A => 44,
        0x5B => 125,  // Left Super
        0x5C => 126,  // Right Super
        0x70 => 59,   // F1
        0x71 => 60,
        0x72 => 61,
        0x73 => 62,
        0x74 => 63,
        0x75 => 64,
        0x76 => 65,
        0x77 => 66,
        0x78 => 67,
        0x79 => 68,
        0x7A => 87,
        0x7B => 88,
        0xA0 => 42,   // Left Shift
        0xA1 => 54,   // Right Shift
        0xA2 => 29,   // Left Ctrl
        0xA3 => 97,   // Right Ctrl
        0xA4 => 56,   // Left Alt
        0xA5 => 100,  // Right Alt
        0xBB => 13,   // Equal / plus
        0xBC => 51,   // Comma
        0xBD => 12,   // Minus
        0xBE => 52,   // Period
        0xBF => 53,   // Slash
        0xC0 => 41,   // Grave
        0xDB => 26,   // [
        0xDC => 43,   // backslash
        0xDD => 27,   // ]
        0xDE => 40,   // apostrophe
        _ => -1,
    };

    /// <summary>
    /// Converts a protocol-level key code into an XKB / xdotool key name.
    /// </summary>
    public static string? ProtocolKeyCodeToXkbName(int keyCode) => keyCode switch
    {
        0x08 => "BackSpace",
        0x09 => "Tab",
        0x0D => "Return",
        0x1B => "Escape",
        0x20 => "space",
        0x21 => "Prior",
        0x22 => "Next",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)keyCode).ToString(),
        >= 0x41 and <= 0x5A => ((char)(keyCode + 32)).ToString(),
        0x5B => "Super_L",
        0x5C => "Super_R",
        >= 0x70 and <= 0x7B => $"F{keyCode - 0x6F}",
        0xA0 => "Shift_L",
        0xA1 => "Shift_R",
        0xA2 => "Control_L",
        0xA3 => "Control_R",
        0xA4 => "Alt_L",
        0xA5 => "Alt_R",
        0xBB => "equal",
        0xBC => "comma",
        0xBD => "minus",
        0xBE => "period",
        0xBF => "slash",
        0xC0 => "grave",
        0xDB => "bracketleft",
        0xDC => "backslash",
        0xDD => "bracketright",
        0xDE => "apostrophe",
        _ => null,
    };

    /// <summary>
    /// Converts a UTF-16 string into XKB keysyms for portal text injection.
    /// </summary>
    public static IReadOnlyList<int> TextToPortalKeysyms(string text)
    {
        var keysyms = new List<int>();
        if (string.IsNullOrEmpty(text))
        {
            return keysyms;
        }

        foreach (var rune in text.EnumerateRunes())
        {
            keysyms.Add(RuneToPortalKeysym(rune));
        }

        return keysyms;
    }

    /// <summary>
    /// Converts a Unicode rune into an XKB keysym value suitable for
    /// <c>NotifyKeyboardKeysym</c>.
    /// </summary>
    public static int RuneToPortalKeysym(Rune rune)
    {
        return rune.Value switch
        {
            0x08 => 0xFF08, // BackSpace
            0x09 => 0xFF09, // Tab
            0x0A => 0xFF0D, // Line feed -> Return
            0x0D => 0xFF0D, // Return
            0x1B => 0xFF1B, // Escape
            <= 0xFF => rune.Value,
            _ => unchecked((int)(0x01000000u | (uint)rune.Value)),
        };
    }

    /// <summary>
    /// Converts a protocol button index (<see cref="InputEvent.Button"/>) to a Linux BTN_ code.
    /// Index 0 = BTN_LEFT, 1 = BTN_MIDDLE, 2 = BTN_RIGHT.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 1 AND 2 WERE THE OTHER WAY ROUND UNTIL RemEx-kie3, and the swap survived because nothing in
    /// production calls this. Only a unit test referenced it, and that test pinned the wrong order,
    /// so the contradiction looked verified rather than wrong. Wiring this up for a future evdev
    /// path would have silently swapped right-click and middle-click on Linux.
    /// <para>
    /// THERE ARE SIX BUTTON TABLES IN THIS REPO and every other one already agreed on
    /// 0/1/2 = left/middle/right: <c>MapButtonXdotool</c>, <c>MapButtonYdotool</c> and
    /// <c>MapButtonLinux</c> in <c>LinuxInputSimulationService</c>; <c>ButtonToLinuxCode</c> and
    /// <c>ButtonToXdotoolButton</c> in <c>LinuxInputBackendRouter</c> (the second of which is now
    /// byte-identical to this one); and the <c>MOUSEEVENTF_*</c> switch in
    /// <c>WindowsInputSimulationService</c>. Six copies with no shared definition is the actual
    /// defect — <c>EveryHostButtonMapping_AgreesOnLeftMiddleRight</c> compares them until one
    /// constant replaces them (RemEx-upxn).
    /// </para>
    /// </para>
    /// <para>
    /// The old summary also mis-stated its input: <see cref="DesktopPointerSample"/> carries a
    /// button MASK, not an index (the router reads <c>ButtonMask &amp; 0x02</c> / <c>&amp; 0x04</c>),
    /// so this never applied to the pointer path at all.
    /// </para>
    /// </remarks>
    public static uint ButtonIndexToLinuxCode(int index) => MouseButtonCodes.ToEvdev(index);
}
