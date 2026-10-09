package com.clindsay94.remex

import com.clindsay94.remex.data.MediaPlaybackSnapshot
import com.clindsay94.remex.data.MediaPlaybackStatus
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

// Drives the JNI callbacks directly, without initialize(context): only callbacks that never reach
// the native library or a SettingsManager are exercised here.
class RemexClientManagerCallbackTest {

    @Before
    fun connect() {
        RemexClientManager.setPendingTarget("192.168.1.10", 5005)
        RemexClientManager.onConnectionStateChanged(isConnected = true)
    }

    @After
    fun disconnect() {
        RemexClientManager.onConnectionStateChanged(isConnected = false)
    }

    @Test
    fun `onConnectionError after onAuthenticated clears the authenticated identity`() {
        RemexClientManager.onAuthenticated()
        assertTrue(RemexClientManager.isAuthenticated.value)
        assertNotNull(RemexClientManager.authenticatedConnection.value)

        RemexClientManager.onConnectionError("simulated transport failure")

        assertFalse(RemexClientManager.isAuthenticated.value)
        assertNull(RemexClientManager.authenticatedConnection.value)
        assertFalse(RemexClientManager.isConnected.value)
        assertNull(RemexClientManager.connectedHost.value)
    }

    @Test
    fun `onConnectionError with a null reason still clears the authenticated identity`() {
        RemexClientManager.onAuthenticated()

        RemexClientManager.onConnectionError(null)

        assertFalse(RemexClientManager.isAuthenticated.value)
        assertNull(RemexClientManager.authenticatedConnection.value)
    }

    @Test
    fun `onAuthenticated after onConnectionError does not resurrect authentication`() {
        RemexClientManager.onConnectionError("simulated transport failure")

        RemexClientManager.onAuthenticated()

        assertFalse(RemexClientManager.isAuthenticated.value)
        assertNull(RemexClientManager.authenticatedConnection.value)
    }

    @Test
    fun `onHostInfoUpdate binds host_info to the live connection`() {
        val json = """{"machineName":"DESKTOP"}"""

        RemexClientManager.onHostInfoUpdate(json)

        val info = RemexClientManager.hostInfoForConnection.value
        assertEquals(json, info?.json)
        assertEquals(RemexClientManager.connectedHost.value, info?.connection)
    }

    @Test
    fun `onHostInfoUpdate with null keeps the previous host_info`() {
        RemexClientManager.onHostInfoUpdate("""{"machineName":"DESKTOP"}""")
        val before = RemexClientManager.hostInfoForConnection.value

        RemexClientManager.onHostInfoUpdate(null)

        assertNotNull(before)
        assertEquals(before, RemexClientManager.hostInfoForConnection.value)
    }

    @Test
    fun `onHostInfoUpdate with malformed JSON does not throw`() {
        RemexClientManager.onHostInfoUpdate("{not json")
        RemexClientManager.onHostInfoUpdate("")
    }

    @Test
    fun `onLinkQuality records a valid round trip`() {
        RemexClientManager.onLinkQuality("""{"roundTripMs":42.5}""")

        assertEquals(42.5, RemexClientManager.roundTripMs.value!!, 0.0)
    }

    @Test
    fun `onLinkQuality ignores null, malformed and out-of-range readings`() {
        RemexClientManager.onLinkQuality("""{"roundTripMs":17}""")

        listOf(
            null,
            "",
            "{not json",
            "[]",
            """{}""",
            """{"roundTripMs":"fast"}""",
            """{"roundTripMs":-1}""",
            """{"roundTripMs":null}"""
        ).forEach { payload ->
            RemexClientManager.onLinkQuality(payload)
            assertEquals("payload=$payload", 17.0, RemexClientManager.roundTripMs.value!!, 0.0)
        }
    }

    @Test
    fun `onMediaState with a valid reading replaces the state`() {
        RemexClientManager.onMediaState("""{"status":"playing","title":"Song"}""")

        val state = RemexClientManager.mediaState.value
        assertEquals(MediaPlaybackStatus.PLAYING, state.status)
        assertEquals("Song", state.title)
    }

    @Test
    fun `onMediaState with null keeps the previous reading`() {
        RemexClientManager.onMediaState("""{"status":"paused","title":"Song"}""")
        val before = RemexClientManager.mediaState.value

        RemexClientManager.onMediaState(null)

        assertEquals(MediaPlaybackStatus.PAUSED, before.status)
        assertEquals(before, RemexClientManager.mediaState.value)
    }

    @Test
    fun `onMediaState with malformed JSON does not throw and degrades to Unknown`() {
        RemexClientManager.onMediaState("""{"status":"playing","title":"Song"}""")

        RemexClientManager.onMediaState("{not json")

        assertEquals(MediaPlaybackSnapshot.Unknown, RemexClientManager.mediaState.value)
    }
}
