package com.clindsay94.remex

import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.yield
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Stands in for the DataStore key: it is what outlives the process. */
internal class FakeDisconnectStore(var saved: Boolean = false, private val failRead: Boolean = false) : UserDisconnectStore {
    val writes = mutableListOf<Boolean>()

    override suspend fun read(): Boolean {
        if (failRead) error("disk unreadable")
        return saved
    }

    override suspend fun write(disconnected: Boolean) {
        saved = disconnected
        writes += disconnected
    }
}

/** Home's Disconnect across process death (RemEx-172ba R7). Each "process" is a fresh latch on one store. */
class UserDisconnectLatchTest {

    private fun gateAllows(disconnected: Boolean) =
            ReconnectGate.allows(
                    networkAvailable = true,
                    foreground = true,
                    screenInteractive = true,
                    widgetPlaced = true,
                    userDisconnected = disconnected,
            )

    @Test
    fun `Disconnect survives a restart and keeps the heartbeat gate shut`() = runTest {
        val disk = FakeDisconnectStore()
        val before = UserDisconnectLatch()
        before.restore(disk)
        before.choose(true)
        before.persist()

        val after = UserDisconnectLatch() // Android killed the app; a routine or widget restarts it
        after.restore(disk)

        assertTrue("the restarted process must still be disconnected", after.state.value)
        assertFalse("and the heartbeat must stay parked even with everything else in its favour", gateAllows(after.state.value))
    }

    @Test
    fun `a user connect clears it for the next process too`() = runTest {
        val disk = FakeDisconnectStore(saved = true)
        val latch = UserDisconnectLatch()
        latch.restore(disk)
        latch.choose(false)
        latch.persist()

        val after = UserDisconnectLatch()
        after.restore(disk)
        assertFalse(after.state.value)
        assertTrue(gateAllows(after.state.value))
    }

    @Test
    fun `nothing that reconnects runs before the saved value is read`() = runTest {
        val latch = UserDisconnectLatch()
        var heartbeatSawFlag: Boolean? = null
        val heartbeat = launch {
            latch.awaitRestored()
            heartbeatSawFlag = latch.state.value
        }
        yield()
        assertEquals("the heartbeat must wait for the read", null, heartbeatSawFlag)

        latch.restore(FakeDisconnectStore(saved = true))
        heartbeat.join()
        assertEquals(true, heartbeatSawFlag)
    }

    @Test
    fun `a Connect tapped during start-up outranks the stale saved Disconnect`() = runTest {
        val disk = FakeDisconnectStore(saved = true)
        val latch = UserDisconnectLatch()
        latch.choose(false)
        latch.persist() // no store yet: restore writes it

        latch.restore(disk)

        assertFalse("the read must not undo the person's Connect", latch.state.value)
        assertFalse("and the Connect is saved once the store is there", disk.saved)
    }

    @Test
    fun `a failed read fails open instead of stranding the phone`() = runTest {
        val latch = UserDisconnectLatch()
        latch.restore(FakeDisconnectStore(saved = true, failRead = true))
        assertFalse(latch.state.value)
        latch.awaitRestored() // and it still releases the heartbeat
    }
}
