package com.clindsay94.remex

import android.content.Context
import com.clindsay94.remex.widget.WidgetRefreshPorts
import com.clindsay94.remex.widget.startWidgetRefreshConnect
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import kotlinx.coroutines.withTimeout
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.mockito.kotlin.doAnswer
import org.mockito.kotlin.mock

/**
 * The real [RemexClientManager] paths around Home's Disconnect (RemEx-172ba R7, R8, R11): what is saved,
 * what a user connect does, and what the widget refresh must not do. Replaces a test that only checked
 * the order of strings in the source.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class UserDisconnectWiringTest {

    /** Written from Dispatchers.IO, so the value is a StateFlow the test can wait on. */
    private class DiskStore : UserDisconnectStore {
        val saved = MutableStateFlow<Boolean?>(null)

        override suspend fun read() = saved.value ?: false

        override suspend fun write(disconnected: Boolean) {
            saved.value = disconnected
        }
    }

    private val disk = DiskStore()
    private val context: Context = mock { on { applicationContext } doAnswer { it.mock as Context } }

    @Before
    fun setUp() = runBlocking {
        // toggleConnection launches on Main. With no saved PC (initialize never runs here) connect()
        // returns at once, so no socket is ever attempted.
        Dispatchers.setMain(UnconfinedTestDispatcher())
        RemexClientManager.restoreUserDisconnected(disk)
    }

    @After
    fun tearDown() {
        RemexClientManager.toggleConnection() // leave the singleton connected-by-choice for later tests
        Dispatchers.resetMain()
    }

    private fun awaitSaved(value: Boolean) = runBlocking {
        withTimeout(5_000) { disk.saved.first { it == value } }
    }

    @Test
    fun `Disconnect is saved and a restarted process reads it back with the heartbeat parked`() {
        RemexClientManager.disconnect(context)
        assertTrue(RemexClientManager.userDisconnected.value)
        awaitSaved(true)

        val restarted = UserDisconnectLatch()
        runBlocking { restarted.restore(disk) }
        assertTrue("the next process must start disconnected", restarted.state.value)
        assertFalse(
                "and its heartbeat may not reconnect",
                ReconnectGate.allows(true, true, true, true, userDisconnected = restarted.state.value),
        )
    }

    @Test
    fun `a user connect clears the flag and saves that`() {
        RemexClientManager.disconnect(context)
        awaitSaved(true)

        RemexClientManager.toggleConnection()

        assertFalse(RemexClientManager.userDisconnected.value)
        awaitSaved(false)
    }

    @Test
    fun `the widget refresh gets one attempt and leaves Disconnect in place`() {
        RemexClientManager.disconnect(context)
        awaitSaved(true)
        var oneShots = 0
        var keepalives = 0
        val ports =
                object : WidgetRefreshPorts {
                    override suspend fun startOneShotConnect(appContext: Context): Boolean {
                        oneShots++
                        return true
                    }

                    override fun startCaching(appContext: Context) = Unit

                    override suspend fun userDisconnected() = RemexClientManager.awaitUserDisconnected()

                    override fun startKeepalive(appContext: Context) {
                        keepalives++
                    }
                }

        runBlocking { startWidgetRefreshConnect(context, ports) }

        assertEquals("a refresh tap is one attempt", 1, oneShots)
        assertTrue("it must not end the person's Disconnect", RemexClientManager.userDisconnected.value)
        assertEquals("and nothing is kept alive for a connection they closed", 0, keepalives)
        assertEquals(true, disk.saved.value)
    }
}
