package com.clindsay94.remex.share

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The share screen says why a Connect failed instead of dropping back to the picker (RemEx-172ba R9). */
@OptIn(ExperimentalCoroutinesApi::class)
class ShareConnectErrorTest {

    @Test
    fun `a failed connect shows its reason until the next attempt or a connection`() = runTest(UnconfinedTestDispatcher()) {
        val errors = MutableSharedFlow<String>()
        val connected = MutableStateFlow(false)
        val error = ShareConnectError()
        error.track(backgroundScope, errors, connected)

        errors.emit("The PC refused the connection.")
        assertEquals("The PC refused the connection.", error.message.value)
        assertFalse(error.opensAppSettings.value)

        error.attempting()
        assertNull("a new attempt starts clean", error.message.value)

        errors.emit("Timed out.")
        connected.value = true
        assertNull("a connection that came up makes the last error stale", error.message.value)
    }

    @Test
    fun `a refused local-network permission says so, with the settings way out when Android stopped asking`() =
            runTest(UnconfinedTestDispatcher()) {
                val error = ShareConnectError()
                error.track(backgroundScope, MutableSharedFlow(), MutableStateFlow(false))

                error.localNetworkRefused("Local network access is off.", permanently = true)
                assertEquals("Local network access is off.", error.message.value)
                assertTrue(error.opensAppSettings.value)

                error.attempting()
                assertFalse(error.opensAppSettings.value)
            }
}
