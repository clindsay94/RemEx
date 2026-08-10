package com.clindsay94.remex

import com.clindsay94.remex.ui.screens.isInputUnavailableError
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The "your input is being discarded" advisory must never reach the fatal desktop-error path
 * (RemEx-iaxc).
 *
 * **THIS IS THE CLIENT HALF OF A CROSS-LANGUAGE CONTRACT, AND THE FAILURE IT GUARDS IS WORSE THAN THE
 * BUG IT SUPPORTS.** The host sends `desktop_error` with the code `input_unavailable` while the video
 * stream is entirely healthy — only input is dead, because a Wayland permission prompt was declined
 * and the PC has no fallback tool. Everything else arriving on that flow clears `isStreaming` and
 * calls `attemptReconnect()`. If this predicate stops matching — a renamed constant on either side, a
 * changed delimiter — the advisory falls through to that handler and a declined prompt becomes a
 * reconnect loop that blanks a working picture once per input event.
 *
 * Neither build breaks on that drift, which is exactly why it is pinned here and, on the host, by a
 * matching assertion on `DesktopErrorCodes.InputUnavailable`.
 */
class InputUnavailableRoutingTest {

    /** Same wire shape as `DesktopErrorCodes.Format`: `code␟arg␟englishFallback`. */
    private fun coded(code: String, arg: String = "", fallback: String = "text") =
            "$code\u001F$arg\u001F$fallback"

    @Test
    fun `the input-unavailable code is recognised`() {
        assertTrue(
                isInputUnavailableError(
                        coded("input_unavailable", fallback = "The PC refused permission to control it.")
                )
        )
    }

    @Test
    fun `other coded desktop errors still take the fatal path`() {
        // THE ASSERTION THAT STOPS THE FIX SWALLOWING REAL FAILURES. A predicate that matched
        // everything would keep the stream "running" through a genuine capture failure, showing a
        // frozen last frame with an input warning over it and never reconnecting.
        assertFalse(isInputUnavailableError(coded("capture_stopped", arg = "42")))
        assertFalse(isInputUnavailableError(coded("capture_unavailable")))
        assertFalse(isInputUnavailableError(coded("runtime_unavailable")))
        assertFalse(isInputUnavailableError(coded("connect_timeout", arg = "10.0.0.2:5006")))
    }

    @Test
    fun `untagged legacy text is not mistaken for the code`() {
        // Hosts older than the coded-error protocol send bare prose with no delimiter, and
        // substringBefore returns the WHOLE string when its delimiter is absent - so a plain sentence
        // is compared in full rather than accidentally matching on a prefix.
        assertFalse(isInputUnavailableError("Screen capture is not working on the host."))
        assertFalse(isInputUnavailableError("input_unavailable is not what happened here"))
        assertFalse(isInputUnavailableError(null))
        assertFalse(isInputUnavailableError(""))
    }

    @Test
    fun `a code that merely starts with the same text does not match`() {
        // substringBefore, not startsWith: a future `input_unavailable_pending` must not be routed
        // away from the fatal path by a prefix match on this one.
        assertFalse(isInputUnavailableError(coded("input_unavailable_pending")))
    }
}
