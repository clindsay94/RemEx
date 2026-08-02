package com.clindsay94.remex

import com.clindsay94.remex.ui.screens.CursorNudge
import com.clindsay94.remex.ui.screens.NudgeDirection
import com.clindsay94.remex.ui.screens.RemoteCursor
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the TalkBack cursor nudge (RemEx-2qzc).
 *
 * The user driving this cannot see the result, so every failure mode is silent to them. That makes
 * "does nothing" and "does the wrong thing" equally bad, and both indistinguishable from a dropped
 * connection.
 */
class CursorNudgeTest {

    private val width = 1920
    private val height = 1080
    private val middle = RemoteCursor(960, 540)

    private fun nudge(
        from: RemoteCursor,
        direction: NudgeDirection,
        step: Int = CursorNudge.SmallStep
    ) = CursorNudge.apply(from, direction, step, width, height)

    @Test
    fun `each direction moves the expected way`() {
        assertEquals(RemoteCursor(950, 540), nudge(middle, NudgeDirection.Left))
        assertEquals(RemoteCursor(970, 540), nudge(middle, NudgeDirection.Right))
        assertEquals(RemoteCursor(960, 530), nudge(middle, NudgeDirection.Up))
        assertEquals(RemoteCursor(960, 550), nudge(middle, NudgeDirection.Down))
    }

    @Test
    fun `up decreases y, because screen coordinates grow downward`() {
        // The same inversion that catches out sparkline rendering. "Up" is a smaller y, and getting
        // it backwards sends the cursor away from where the user asked - which they cannot see.
        assertTrue(nudge(middle, NudgeDirection.Up).y < middle.y)
        assertTrue(nudge(middle, NudgeDirection.Down).y > middle.y)
    }

    @Test
    fun `the large step is coarse enough to cross a screen without a hundred actions`() {
        // A screen-reader user driving a 1920-wide desktop at 10px per action would need 192
        // actions to cross it. The two step sizes are what make the surface usable at all.
        assertTrue(CursorNudge.LargeStep >= CursorNudge.SmallStep * 5)

        assertEquals(RemoteCursor(1060, 540), nudge(middle, NudgeDirection.Right, CursorNudge.LargeStep))
    }

    @Test
    fun `the cursor clamps at the edges rather than wrapping`() {
        // A cursor that reappears on the opposite edge is disorienting for a sighted user and
        // UNRECOVERABLE for someone driving it through a screen reader, who has no way to notice it
        // happened.
        assertEquals(RemoteCursor(0, 540), nudge(RemoteCursor(3, 540), NudgeDirection.Left))
        assertEquals(RemoteCursor(width - 1, 540),
            nudge(RemoteCursor(width - 3, 540), NudgeDirection.Right))
        assertEquals(RemoteCursor(960, 0), nudge(RemoteCursor(960, 3), NudgeDirection.Up))
        assertEquals(RemoteCursor(960, height - 1),
            nudge(RemoteCursor(960, height - 3), NudgeDirection.Down))
    }

    @Test
    fun `a nudge at the edge is a no-op rather than an error`() {
        val atCorner = RemoteCursor(0, 0)

        assertEquals(atCorner, nudge(atCorner, NudgeDirection.Left))
        assertEquals(atCorner, nudge(atCorner, NudgeDirection.Up))
    }

    @Test
    fun `an action that would do nothing can be identified so it is not offered`() {
        // A screen-reader user who triggers "move left" at the left edge and hears nothing cannot
        // tell that from the connection having dropped. Omitting the action is honest; offering one
        // that silently fails is not.
        assertFalse(CursorNudge.wouldMove(RemoteCursor(0, 540), NudgeDirection.Left,
            CursorNudge.SmallStep, width, height))
        assertTrue(CursorNudge.wouldMove(RemoteCursor(0, 540), NudgeDirection.Right,
            CursorNudge.SmallStep, width, height))
    }

    @Test
    fun `the cursor never leaves the screen no matter how many nudges are applied`() {
        // Swept, because an off-by-one at the boundary produces a coordinate the host will reject
        // or clamp differently - and the user would experience that as the pointer sticking.
        var cursor = middle
        repeat(400) { cursor = nudge(cursor, NudgeDirection.Right, CursorNudge.LargeStep) }
        assertEquals(width - 1, cursor.x)

        repeat(400) { cursor = nudge(cursor, NudgeDirection.Up, CursorNudge.LargeStep) }
        assertEquals(0, cursor.y)

        assertTrue(cursor.x in 0 until width)
        assertTrue(cursor.y in 0 until height)
    }

    @Test
    fun `a screen with no area refuses to move rather than producing coordinates outside it`() {
        // A nudge can arrive before the first frame has described the display. Clamping against a
        // negative bound would produce a position outside the screen, which is worse than not
        // moving.
        val before = RemoteCursor(5, 5)

        assertEquals(before, CursorNudge.apply(before, NudgeDirection.Right, 10, 0, 1080))
        assertEquals(before, CursorNudge.apply(before, NudgeDirection.Down, 10, 1920, 0))
        assertEquals(before, CursorNudge.apply(before, NudgeDirection.Left, 10, -1920, -1080))
    }

    @Test
    fun `a one-pixel screen leaves the cursor at its only valid position`() {
        val only = RemoteCursor(0, 0)

        for (direction in NudgeDirection.entries) {
            assertEquals(only, CursorNudge.apply(only, direction, 10, 1, 1))
        }
    }
}
