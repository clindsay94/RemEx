package com.clindsay94.remex.ui.routines

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * The snackbar plumbing (RemEx-pp0rt.6 review, MAJOR 1): latest wins, nothing queues behind a
 * message that is waiting for a tap, and a message is never dropped without a newer one replacing it.
 */
class RoutinesMessageChannelTest {
    @Test
    fun `a new message replaces the pending one instead of queueing behind it`() {
        val channel = RoutinesMessageChannel()
        channel.post(RoutinesMessage("Step deleted.", RoutinesMessageAction.Undo {}))
        channel.post(RoutinesMessage("Fix 2 things before saving."))
        assertEquals("Fix 2 things before saving.", channel.current.value?.text)
    }

    @Test
    fun `any number of posts never loses the newest`() {
        val channel = RoutinesMessageChannel()
        repeat(50) { channel.post(RoutinesMessage("message $it")) }
        assertEquals("message 49", channel.current.value?.text)
    }

    @Test
    fun `the same text twice is two messages, so the second still shows`() {
        val channel = RoutinesMessageChannel()
        val first = channel.post(RoutinesMessage("Saved."))
        val second = channel.post(RoutinesMessage("Saved."))
        assertNotEquals(first, second)
        assertEquals(second, channel.current.value)
    }

    @Test
    fun `consuming clears the shown message but never a newer one`() {
        val channel = RoutinesMessageChannel()
        val shown = channel.post(RoutinesMessage("Routine deleted."))
        channel.consumed(shown)
        assertNull(channel.current.value)

        val stale = channel.post(RoutinesMessage("old"))
        val fresh = channel.post(RoutinesMessage("new"))
        channel.consumed(stale)
        assertEquals(fresh, channel.current.value)
    }
}
