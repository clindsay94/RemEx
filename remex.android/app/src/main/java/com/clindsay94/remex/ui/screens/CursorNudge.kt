package com.clindsay94.remex.ui.screens

/** A cursor position in REMOTE screen pixels. */
data class RemoteCursor(val x: Int, val y: Int)

/** Which way a nudge moves the cursor. */
enum class NudgeDirection { Left, Right, Up, Down }

/**
 * Moves the remote cursor by keyboard-or-TalkBack-sized steps (RemEx-2qzc).
 *
 * The remote-desktop surface is a single opaque node to TalkBack: a blind or low-vision user gets
 * one static label for the entire PC and no way to drive the pointer, because the input path is raw
 * `awaitPointerEvent` below the accessibility tree. D-pad-style nudge actions are how the pointer
 * becomes reachable without a drag gesture.
 *
 * **STEPS ARE IN REMOTE PIXELS, NOT IN THE PHONE'S VIEW PIXELS**, and that is the decision the rest
 * of this follows from. The phone renders the stream scaled and pan-able, so a step measured on the
 * phone would move the cursor a different distance on the PC at every zoom level — the same action
 * would creep at one zoom and leap at another, which is unusable when the user cannot see the
 * result. Measuring on the PC makes one action mean one thing.
 */
object CursorNudge {

    /** Fine positioning — landing on a button. */
    const val SmallStep: Int = 10

    /** Coarse traversal — crossing a screen without a hundred actions. */
    const val LargeStep: Int = 100

    /**
     * Applies a nudge, clamped to the remote screen.
     *
     * @param screenWidth remote screen width in pixels.
     * @param screenHeight remote screen height in pixels.
     */
    fun apply(
        from: RemoteCursor,
        direction: NudgeDirection,
        step: Int,
        screenWidth: Int,
        screenHeight: Int
    ): RemoteCursor {
        // A screen with no area has no valid position to move to, and clamping against a negative
        // bound would produce coordinates outside it. Refusing to move is the only honest answer,
        // and it happens whenever a nudge arrives before the first frame has described the display.
        if (screenWidth <= 0 || screenHeight <= 0) return from

        val dx = when (direction) {
            NudgeDirection.Left -> -step
            NudgeDirection.Right -> step
            else -> 0
        }
        val dy = when (direction) {
            NudgeDirection.Up -> -step
            NudgeDirection.Down -> step
            else -> 0
        }

        // CLAMPED, NOT WRAPPED. A cursor that reappears on the opposite edge is disorienting for a
        // sighted user and unrecoverable for someone driving it through a screen reader, who has no
        // way to notice it happened. Stopping at the edge is also how every physical pointer
        // behaves, so it needs no explanation.
        return RemoteCursor(
            x = (from.x + dx).coerceIn(0, screenWidth - 1),
            y = (from.y + dy).coerceIn(0, screenHeight - 1)
        )
    }

    /**
     * Whether a nudge would actually move the cursor.
     *
     * Exposed so the accessibility node can omit an action that would do nothing rather than
     * announcing one that silently fails. A screen-reader user who triggers "move left" at the left
     * edge and hears nothing cannot tell that from the connection having dropped.
     */
    fun wouldMove(
        from: RemoteCursor,
        direction: NudgeDirection,
        step: Int,
        screenWidth: Int,
        screenHeight: Int
    ): Boolean = apply(from, direction, step, screenWidth, screenHeight) != from
}
