package com.clindsay94.remex.ui.screens

/**
 * Button indices for the `button` field of a `mouseDown` / `mouseUp` / `mouseClick` input event.
 *
 * These are PROTOCOL indices, not Android or platform button codes. Both hosts map them the same
 * way — Windows through `MOUSEEVENTF_*` and Linux through all three of its backends (xdotool,
 * ydotool and the portal) — and the order matches the W3C `MouseEvent.button` convention, so a
 * value that looks right to a web or desktop developer is right here too.
 *
 * They exist because sending the bare integer is how [RIGHT] and [MIDDLE] got swapped: the Remote
 * Mouse screen sent `1` for its "Left click" button and its trackpad tap, which is MIDDLE-click on
 * every platform, so left click did not left-click at all (RemEx-kie3). Nothing about `1` looks
 * wrong at a call site. Use these names instead of the digits.
 */
internal object MouseButtons {
    /** Primary button. The only value any caller should assume for a plain tap or click. */
    const val LEFT = 0

    /** Middle / wheel button. */
    const val MIDDLE = 1

    /** Secondary button — the context-menu one. */
    const val RIGHT = 2
}
