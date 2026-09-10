package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.parseHostCapabilities
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins how the client reads the host's input capability (RemEx-q9zw).
 *
 * THE HOST HAS ALWAYS SENT THIS FLAG AND THE CLIENT HAS NEVER READ IT. `supportsInputSimulation`
 * was produced by `HostCapabilitiesProvider` and consumed nowhere, so a host that could stream but
 * not inject still got a full input UI whose events went nowhere. RemEx-jvme made the flag truthful
 * for the first time; until it is read, that fix is inert end to end.
 *
 * THE DEFAULT IS THE PART WITH TEETH. Every sibling capability defaults to false, because absence
 * means "this host is too old to offer the feature". This one is a refusal rather than a feature:
 * the host says false only to state that it cannot inject. Defaulting it false would turn a missing
 * key into a session with no input at all, which is a far worse failure than the one being fixed.
 * So absence means "assume yes", and these tests exist mostly to stop someone tidying that
 * inconsistency away. The only thing that can omit the key is a host predating the field - the
 * property is a non-nullable bool and is always serialized - so this is a compatibility floor
 * rather than a routine case.
 */
class HostCapabilityParseTest {

    private fun capabilities(vararg pairs: Pair<String, Any>): String =
            JSONObject().apply { pairs.forEach { (k, v) -> put(k, v) } }.toString()

    @Test
    fun `an absent flag is treated as supported`() {
        // The backward-compatibility case, and the reason this default is not a copy of its
        // siblings'. Any host predating the field must keep working exactly as it does today.
        val state = parseHostCapabilities(capabilities("supportsRemoteDesktop" to true))

        assertTrue(state.supportsInputSimulation)
    }

    @Test
    fun `an explicit false is honoured`() {
        val state =
                parseHostCapabilities(
                        capabilities(
                                "supportsRemoteDesktop" to true,
                                "supportsInputSimulation" to false
                        )
                )

        assertFalse(state.supportsInputSimulation)
    }

    @Test
    fun `an explicit true is honoured`() {
        val state =
                parseHostCapabilities(
                        capabilities(
                                "supportsRemoteDesktop" to true,
                                "supportsInputSimulation" to true
                        )
                )

        assertTrue(state.supportsInputSimulation)
    }

    @Test
    fun `input and streaming are independent`() {
        // The whole point of reading a second flag: a host that can stream but not inject must come
        // out as view-only rather than as either extreme. Asserted in both directions so a future
        // simplification cannot quietly re-derive one from the other.
        val viewOnly =
                parseHostCapabilities(
                        capabilities(
                                "supportsRemoteDesktop" to true,
                                "supportsInputSimulation" to false
                        )
                )
        assertTrue(viewOnly.supportsRemoteDesktop)
        assertFalse(viewOnly.supportsInputSimulation)

        val neither =
                parseHostCapabilities(
                        capabilities(
                                "supportsRemoteDesktop" to false,
                                "supportsInputSimulation" to false
                        )
                )
        assertFalse(neither.supportsRemoteDesktop)
        assertFalse(neither.supportsInputSimulation)
    }

    @Test
    fun `the other capabilities still parse as they did`() {
        // Non-regression for the extraction itself. The parse moved out of the collector into a
        // function; if it had dropped or renamed a key on the way, the symptom would be a silently
        // missing feature rather than a crash.
        val state =
                parseHostCapabilities(
                        capabilities(
                                "supportsRemoteDesktop" to true,
                                "supportsCursorQuery" to true,
                                "supportsAdvancedWindowControl" to true,
                                "inputBackend" to "xdotool",
                                "windowControlBackend" to "kdotool",
                                "remoteDesktopUnavailableReason" to "because"
                        )
                )

        assertTrue(state.supportsRemoteDesktop)
        assertTrue(state.supportsCursorQuery)
        assertTrue(state.supportsAdvancedWindowControl)
        assertEquals("xdotool", state.inputBackend)
        assertEquals("kdotool", state.windowBackend)
        assertEquals("because", state.unavailableReason)
    }

    @Test
    fun `blank strings become null rather than empty`() {
        // Preserved behaviour worth pinning because it is easy to lose in a rewrite: the UI checks
        // these for null, so an empty string would render a blank backend name instead of hiding
        // the row.
        val state =
                parseHostCapabilities(
                        capabilities(
                                "supportsRemoteDesktop" to true,
                                "inputBackend" to "",
                                "windowControlBackend" to "   ",
                                "remoteDesktopUnavailableReason" to ""
                        )
                )

        assertEquals(null, state.inputBackend)
        assertEquals(null, state.windowBackend)
        assertEquals(null, state.unavailableReason)
    }
}
