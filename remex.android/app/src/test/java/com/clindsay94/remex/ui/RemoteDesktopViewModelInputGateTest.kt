package com.clindsay94.remex.ui

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins that input actually passes through the capability gate, and records which view model does not
 * yet have one (RemEx-q9zw, RemEx-i8ty).
 *
 * A SOURCE-READING TEST, WHICH THIS MODULE ONLY USES WHEN THE ALTERNATIVE IS NO TEST AT ALL.
 * `RemoteDesktopViewModel` is an `AndroidViewModel` reaching a JNI client, and there is no
 * Robolectric here, so the gate itself cannot be exercised behaviourally. Deleting either guard
 * currently fails nothing. `SendDispatcherDeclarationOrderTest` is the established precedent for
 * exactly this trade, and it already iterates these same two view models as peer input paths.
 *
 * THE SECOND ASSERTION IS DELIBERATELY THE WRONG WAY ROUND. `RemoteControlViewModel` sends the same
 * `desktop_input` envelope from the Remote Mouse screen with no capability awareness whatsoever, and
 * that screen needs no video stream, so it is reachable on a host where remote desktop is off
 * entirely. RemEx-q9zw did not cover it; RemEx-i8ty will. Pinning the gap rather than omitting it
 * means that bead has to come back here, instead of the two drifting apart — which is how an early
 * draft of RemEx-q9zw's own handover note came to claim input was fully sealed when it was not.
 */
class RemoteDesktopViewModelInputGateTest {

    private fun source(name: String): String {
        val file =
                File("src/main/java/com/clindsay94/remex/ui/screens/$name.kt").takeIf { it.isFile }
                        ?: File("app/src/main/java/com/clindsay94/remex/ui/screens/$name.kt")
        assertTrue("expected to find $name at ${file.path}", file.isFile)
        return file.readText()
    }

    private val gate = "if (!_capabilityState.value.supportsInputSimulation) return"

    @Test
    fun `both remote desktop send paths check the capability before sending`() {
        val text = source("RemoteDesktopViewModel")

        // Two choke points, because there are two ways input leaves that class: sendInput carries
        // mouse, keyboard, scroll and typed text; sendPointerBatch carries the stylus/touch stream.
        assertEquals(
                "expected the capability gate at both send choke points",
                2,
                Regex(Regex.escape(gate)).findAll(text).count()
        )

        // Position matters as much as presence for the batch path: gating before the consumer is
        // started means a host that cannot inject never even spins up the sender coroutine.
        val batch = text.indexOf("fun sendPointerBatch(")
        val startCall = text.indexOf("startPointerSenderIfNeeded()", batch)
        val gateInBatch = text.indexOf(gate, batch)
        assertTrue("sendPointerBatch should be gated", gateInBatch in (batch + 1) until startCall)
    }

    @Test
    fun `the remote mouse view model is NOT gated yet and this test must change when it is`() {
        // Documents a live gap rather than asserting the codebase is safe. When RemEx-i8ty adds the
        // gate, this assertion fails and its author replaces it with the positive one — which is the
        // point of writing it down instead of leaving the omission to be rediscovered.
        val text = source("RemoteControlViewModel")

        assertTrue(
                "RemoteControlViewModel still sends desktop_input, so this test is still needed",
                text.contains("\"desktop_input\"")
        )
        // KEYED ON THE CAPABILITY NAME, NOT ON THE WHOLE GUARD, and that difference decides whether
        // this tripwire ever fires. The exact guard text above contains `_capabilityState`, a field
        // RemoteControlViewModel does not have and whose replacement the author of RemEx-i8ty has
        // not named yet. Matching the full string would mean any other choice — `_hostCapabilities`,
        // a helper method — leaves the count at zero, the test green, and the handover silently
        // broken, which is the exact drift this class exists to prevent. `supportsInputSimulation`
        // cannot appear in that file for any other reason.
        assertFalse(
                "RemoteControlViewModel appears to have gained an input gate — see RemEx-i8ty and " +
                        "replace this assertion with the positive one",
                text.contains("supportsInputSimulation")
        )
    }
}
