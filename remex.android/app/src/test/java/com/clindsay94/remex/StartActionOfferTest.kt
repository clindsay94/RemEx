package com.clindsay94.remex

import com.clindsay94.remex.ui.screens.shouldOfferStartAction
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The remote-desktop idle screen always offers a way forward after a failure (RemEx-5k4dd).
 *
 * The button was gated on the capability flag alone, and several failure paths clear that flag - so
 * an error rendered under a monitor icon with nothing to press. The screen became a dead end
 * reachable by ordinary failure, and backing out to reconnect was the only route forward.
 */
class StartActionOfferTest {

    @Test
    fun `an error offers the action even when the capability flag says no`() {
        // THE BUG. Both conditions together are exactly the state a failed stream leaves behind, and
        // it is the state in which the user most needs something to press.
        assertTrue(
            shouldOfferStartAction(isStreaming = false, supportsRemoteDesktop = false, hasError = true)
        )
    }

    @Test
    fun `an unsupported host with no error still hides it`() {
        // THE CASE THAT MUST NOT REGRESS, and the reason the fix is an OR rather than dropping the
        // flag. A host that genuinely cannot stream should not be offered a button that can only
        // fail - the gate was incomplete, not wrong.
        assertFalse(
            shouldOfferStartAction(isStreaming = false, supportsRemoteDesktop = false, hasError = false)
        )
    }

    @Test
    fun `a capable idle host offers it, error or not`() {
        assertTrue(
            shouldOfferStartAction(isStreaming = false, supportsRemoteDesktop = true, hasError = false)
        )
        assertTrue(
            shouldOfferStartAction(isStreaming = false, supportsRemoteDesktop = true, hasError = true)
        )
    }

    @Test
    fun `streaming never offers it, whatever else is true`() {
        // Streaming outranks both. Offering "start" mid-stream would be an action with no meaning,
        // and the enumeration is here so a later edit cannot quietly make the error case win.
        for (supports in listOf(true, false)) {
            for (error in listOf(true, false)) {
                assertFalse(
                    "streaming=true supports=$supports error=$error should offer nothing",
                    shouldOfferStartAction(
                        isStreaming = true,
                        supportsRemoteDesktop = supports,
                        hasError = error
                    )
                )
            }
        }
    }
}
