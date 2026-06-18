using System.Collections.Generic;

namespace Remex.Host.Services.Input;

/// <summary>
/// A single synthetic <c>KEYEVENTF_UNICODE</c> keyboard event: one UTF-16 code unit, either a
/// key-down or a key-up. Platform-neutral so the surrogate-handling logic can be unit-tested
/// without invoking the Windows-only <see cref="WindowsInputSimulationService"/> or any P/Invoke.
/// </summary>
internal readonly record struct UnicodeKeyEvent(ushort ScanCode, bool IsKeyUp);

/// <summary>
/// Turns a string into the ordered sequence of synthetic Unicode keyboard events required to type
/// it via <c>SendInput</c>.
///
/// <para>
/// The critical rule is surrogate pairs: a code point outside the Basic Multilingual Plane (emoji,
/// some CJK extensions) is encoded as two UTF-16 code units — a high surrogate followed by a low
/// surrogate. Windows only reconstructs them into a single code point when the two <em>key-downs</em>
/// are delivered consecutively. The naive "down+up per code unit" approach
/// (high-down, high-up, low-down, low-up) injects a key-up between the surrogates, which breaks the
/// composition so the emoji is garbled or dropped. This helper instead emits both key-downs first
/// and then both key-ups, and the caller sends each group in a single <c>SendInput</c> batch.
/// </para>
/// </summary>
internal static class UnicodeTextInput
{
    /// <summary>
    /// Builds the per-code-point event groups for <paramref name="text"/>. Each returned group must
    /// be sent atomically (a single <c>SendInput</c> call): two events for a Basic-Multilingual-Plane
    /// character, four for a surrogate pair. An unpaired/lone surrogate (malformed input) is typed
    /// best-effort as its own single code unit.
    /// </summary>
    public static List<UnicodeKeyEvent[]> BuildKeyEventGroups(string text)
    {
        var groups = new List<UnicodeKeyEvent[]>();
        if (string.IsNullOrEmpty(text)) return groups;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                char low = text[i + 1];
                // Both key-downs consecutive, then both key-ups: required for Windows to
                // reconstruct the non-BMP code point from the surrogate pair.
                groups.Add(new[]
                {
                    new UnicodeKeyEvent(c, false),
                    new UnicodeKeyEvent(low, false),
                    new UnicodeKeyEvent(c, true),
                    new UnicodeKeyEvent(low, true),
                });
                i++; // consumed the trailing low surrogate as part of this group
            }
            else
            {
                groups.Add(new[]
                {
                    new UnicodeKeyEvent(c, false),
                    new UnicodeKeyEvent(c, true),
                });
            }
        }

        return groups;
    }
}
