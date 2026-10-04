package com.clindsay94.remex.ui.navigation

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.filter
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Tapping the tab you are already on goes back to the top (3.0 comb, no-reselect; RemEx-pp4cm.3).
 *
 * WHAT MUST NOT BECOME A RESELECT: a click on a tab that is selected but not yet reached (a far jump
 * on its way, a drag cancelled part-way). That click has to keep going through the normal tab-scroll
 * path, which finishes the move; swallowing it as "scroll to top" strands the pager between pages.
 */
class TabReselectTest {

    private fun reselect(
        clicked: Int = 2,
        selected: Int = 2,
        atPrimary: Boolean = true,
        settled: Int = 2,
        current: Int = 2,
        offset: Float = 0f,
        inFlight: Boolean = false,
    ) = TabReselect.isPrimaryReselect(clicked, selected, atPrimary, settled, current, offset, inFlight)

    @Test
    fun `clicking the settled selected tab is a reselect`() {
        assertTrue(reselect())
    }

    @Test
    fun `clicking a different tab is a move`() {
        assertFalse(reselect(clicked = 1))
    }

    @Test
    fun `a selected tab the pager has not reached yet is not a reselect`() {
        assertFalse(reselect(settled = 0, current = 1))
        assertFalse(reselect(settled = 2, current = 1))
        assertFalse(reselect(settled = 1, current = 2))
    }

    @Test
    fun `a slide still in progress is not a reselect`() {
        assertFalse(reselect(offset = 0.25f))
        assertFalse(reselect(inFlight = true))
    }

    @Test
    fun `off the tabs altogether is not a reselect`() {
        assertFalse(reselect(atPrimary = false))
    }

    @Test
    fun `an emitted destination reaches a collector for that destination only`() = runBlocking {
        val got = CompletableDeferred<NavDestination>()
        val job =
            launch(Dispatchers.Default) {
                got.complete(TabReselect.events.filter { it == Screen.Control }.first())
            }
        // The flow has no replay, so give the collector time to subscribe before emitting.
        delay(200)
        TabReselect.emit(Screen.Home)
        delay(50)
        TabReselect.emit(Screen.Control)
        assertEquals(Screen.Control, withTimeout(5_000) { got.await() })
        job.cancel()
    }
}
