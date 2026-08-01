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
 *
 * MIRRORED BY `Remex.Core.Models.MouseButtons` ON THE HOST, which the PC's own backends now switch
 * on (RemEx-upxn). The two cannot share a definition across the JNI boundary, so they are two files
 * that must agree — change one and change the other. The host side additionally names SIDE (3) and
 * EXTRA (4). NOTHING IN THIS APP SENDS EITHER: the pointer path carries a button MASK rather than an
 * index (the host reads bit 0x02 / 0x04 as stylus barrel state), and the click UI offers only these
 * three. They exist so a host receiving a stray index maps it to the button it names rather than
 * silently left-clicking.
 */
internal object MouseButtons {
    /** Primary button. The only value any caller should assume for a plain tap or click. */
    const val LEFT = 0

    /** Middle / wheel button. */
    const val MIDDLE = 1

    /** Secondary button — the context-menu one. */
    const val RIGHT = 2
}
