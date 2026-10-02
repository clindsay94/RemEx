package com.clindsay94.remex.data

import kotlinx.coroutines.test.runTest
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * [HomePinsRepository]'s rules (RemEx-wqo7a.6): the PC owns the list, the phone sends one delta per
 * edit, an older PC gets a phone-local list and never a message, and the first sync from a newly
 * updated PC seeds it from that local list once. Fake send and store; no socket, no DataStore.
 */
class HomePinsRepositoryTest {

    private class FakeStore : HomePinsStore {
        val saved = HashMap<String, HomePinsCache>()

        override suspend fun load(identity: String): HomePinsCache? = saved[identity]

        override suspend fun save(identity: String, cache: HomePinsCache) {
            saved[identity] = cache
        }
    }

    private val sent = ArrayList<String>()
    private var authenticated = true
    private val store = FakeStore()
    private val repo =
            HomePinsRepository(send = { sent += it }, isAuthenticated = { authenticated }).also { it.attachStore(store) }

    private fun sync(names: List<String>, pinnable: List<String>, revision: Long): String =
            JSONObject()
                    .put("type", "home_pins_sync")
                    .put(
                            "homePins",
                            JSONObject()
                                    .put("sensorNames", JSONArray(names))
                                    .put("pinnableSensorNames", JSONArray(pinnable))
                                    .put("revision", revision)
                    )
                    .toString()

    private fun sentChanges(): List<Pair<String, Boolean>> =
            sent.map { JSONObject(it).getJSONObject("homePinChange") }.map { it.getString("sensorName") to it.getBoolean("pinned") }

    @Test
    fun `a host sync replaces the list and is cached for that PC`() = runTest {
        repo.onConnected(1, "pc-a")
        repo.onSyncMessage(sync(listOf("CPU"), listOf("CPU", "GPU"), 1))

        val state = repo.state.value
        assertEquals(listOf("CPU"), state.pinned)
        assertEquals(listOf("CPU", "GPU"), state.pinnable)
        assertTrue(state.syncSupported)
        assertEquals(HomePinsSource.HOST, state.source)
        assertEquals(HomePinsCache(listOf("CPU"), listOf("CPU", "GPU"), HomePinsSource.HOST), store.saved["pc-a"])
        assertTrue("a host sync alone must not make the phone send anything", sent.isEmpty())
    }

    @Test
    fun `a phone toggle on a syncing PC is optimistic and sends one delta`() = runTest {
        repo.onConnected(1, "pc-a")
        repo.onSyncMessage(sync(listOf("CPU"), listOf("CPU", "GPU"), 1))

        assertTrue(repo.setPinned("GPU", pinned = true))
        assertEquals(listOf("CPU", "GPU"), repo.state.value.pinned)
        assertEquals(listOf("GPU" to true), sentChanges())

        // The PC refuses (it resends the unchanged list): that undoes the optimistic edit.
        repo.onSyncMessage(sync(listOf("CPU"), listOf("CPU", "GPU"), 1))
        assertEquals(listOf("CPU"), repo.state.value.pinned)
    }

    @Test
    fun `a sensor the PC cannot pin is refused on a syncing PC and nothing is sent`() = runTest {
        repo.onConnected(1, "pc-a")
        repo.onSyncMessage(sync(emptyList(), listOf("CPU"), 1))

        assertFalse(repo.setPinned("Fan 3", pinned = true))
        assertTrue(sent.isEmpty())
        assertEquals(emptyList<String>(), repo.state.value.pinned)
    }

    @Test
    fun `nothing is sent before the host acked the connection`() = runTest {
        repo.onConnected(1, "pc-a")
        repo.onSyncMessage(sync(emptyList(), listOf("CPU"), 1))
        authenticated = false

        assertFalse(repo.setPinned("CPU", pinned = true))
        assertTrue(sent.isEmpty())
    }

    @Test
    fun `an older PC gets a phone-local list and never a message`() = runTest {
        repo.onConnected(1, "pc-old")
        repo.onHostInfo(1, """{"machineName":"Old Tower"}""")
        assertFalse(repo.state.value.syncSupported)
        assertTrue("an older PC pins anything", repo.state.value.canPin("Anything at all"))

        assertTrue(repo.setPinned("CPU", pinned = true))
        assertTrue(repo.setPinned("RAM", pinned = true))
        assertTrue(repo.setPinned("CPU", pinned = false))

        assertEquals(listOf("RAM"), repo.state.value.pinned)
        assertEquals(HomePinsSource.LOCAL, repo.state.value.source)
        assertEquals(HomePinsCache(listOf("RAM"), emptyList(), HomePinsSource.LOCAL), store.saved["pc-old"])
        assertTrue("an older PC must never be sent home_pins_change", sent.isEmpty())
    }

    @Test
    fun `the first sync with an empty list seeds it from the local list once`() = runTest {
        store.saved["pc-a"] = HomePinsCache(listOf("CPU", "Fan 3", "RAM"), emptyList(), HomePinsSource.LOCAL)
        repo.onConnected(1, "pc-a")
        assertEquals(listOf("CPU", "Fan 3", "RAM"), repo.state.value.pinned)

        repo.onSyncMessage(sync(emptyList(), listOf("CPU", "RAM"), 1))
        // Only what the PC can pin goes over, one delta each; Fan 3 has no card on the PC.
        assertEquals(listOf("CPU" to true, "RAM" to true), sentChanges())
        assertEquals(listOf("CPU", "RAM"), repo.state.value.pinned)
        assertEquals(HomePinsSource.HOST, store.saved["pc-a"]!!.source)

        // Never again: a later empty sync (the user unpinned everything on the PC) is just the list.
        sent.clear()
        repo.onDisconnected()
        repo.onConnected(2, "pc-a")
        repo.onSyncMessage(sync(emptyList(), listOf("CPU", "RAM"), 1))
        assertTrue(sent.isEmpty())
        assertEquals(emptyList<String>(), repo.state.value.pinned)
    }

    @Test
    fun `a PC that already has pins is never seeded`() = runTest {
        store.saved["pc-a"] = HomePinsCache(listOf("RAM"), emptyList(), HomePinsSource.LOCAL)
        repo.onConnected(1, "pc-a")
        repo.onSyncMessage(sync(listOf("CPU"), listOf("CPU", "RAM"), 1))
        assertTrue(sent.isEmpty())
        assertEquals(listOf("CPU"), repo.state.value.pinned)
    }

    @Test
    fun `a seed waits for the host's ack`() = runTest {
        store.saved["pc-a"] = HomePinsCache(listOf("CPU"), emptyList(), HomePinsSource.LOCAL)
        authenticated = false
        repo.onConnected(1, "pc-a")
        repo.onSyncMessage(sync(emptyList(), listOf("CPU"), 1))
        assertTrue(sent.isEmpty())

        authenticated = true
        repo.onAuthenticated()
        assertEquals(listOf("CPU" to true), sentChanges())
    }

    @Test
    fun `an older sync on the same connection is discarded and the counter resets on reconnect`() = runTest {
        repo.onConnected(1, "pc-a")
        repo.onSyncMessage(sync(listOf("CPU"), listOf("CPU", "GPU"), 5))
        repo.onSyncMessage(sync(listOf("GPU"), listOf("CPU", "GPU"), 4))
        assertEquals(listOf("CPU"), repo.state.value.pinned)

        repo.onDisconnected()
        repo.onConnected(2, "pc-a")
        repo.onSyncMessage(sync(listOf("GPU"), listOf("CPU", "GPU"), 1))
        assertEquals(listOf("GPU"), repo.state.value.pinned)
    }

    @Test
    fun `a sync that lands before the connection is known is applied by it`() = runTest {
        repo.onSyncMessage(sync(listOf("CPU"), listOf("CPU"), 3))
        assertFalse(repo.state.value.connected)

        repo.onConnected(1, "pc-a")
        assertEquals(listOf("CPU"), repo.state.value.pinned)
        assertTrue(repo.state.value.syncSupported)
        // Held syncs are not a baseline: the next real one is accepted whatever its revision.
        repo.onSyncMessage(sync(listOf("RAM"), listOf("CPU", "RAM"), 1))
        assertEquals(listOf("RAM"), repo.state.value.pinned)
    }

    @Test
    fun `a host_info that wins the race against onConnected still counts for that connection`() = runTest {
        repo.onHostInfo(7, """{"supportsHomePinsSync":true}""")
        repo.onConnected(7, "pc-a")
        assertTrue(repo.state.value.syncSupported)

        repo.onDisconnected()
        repo.onConnected(8, "pc-a")
        assertFalse("another connection's host_info must not count", repo.state.value.syncSupported)
    }

    @Test
    fun `a host switch never shows the last PC's list`() = runTest {
        repo.onConnected(1, "pc-a")
        repo.onSyncMessage(sync(listOf("CPU"), listOf("CPU"), 1))
        repo.onDisconnected()
        repo.onConnected(2, "pc-b")
        assertEquals(emptyList<String>(), repo.state.value.pinned)
        assertFalse(repo.state.value.syncSupported)
    }

    // ── Phase 3 review R3: syncs are tied to the connection they arrived on ──

    @Test
    fun `B's first sync landing before B is announced is neither scored nor saved as A's`() = runTest {
        // The connection flow conflated A -> null -> B, and B's sync beat the connection collector.
        repo.onConnected(1, "pc-a")
        repo.onSyncMessage(sync(listOf("CPU"), listOf("CPU", "GPU"), 9), connectionEpoch = 1)

        repo.onSyncMessage(sync(listOf("RAM"), listOf("RAM"), 1), connectionEpoch = 2)
        assertEquals("A's list must stand until B is announced", listOf("CPU"), repo.state.value.pinned)
        assertEquals(listOf("CPU"), store.saved["pc-a"]?.names)

        repo.onConnected(2, "pc-b")
        // Applied for B despite revision 1 < A's 9, and saved under B's key, not A's.
        assertEquals(listOf("RAM"), repo.state.value.pinned)
        assertEquals(listOf("RAM"), store.saved["pc-b"]?.names)
        assertEquals(listOf("CPU"), store.saved["pc-a"]?.names)
        // It was B's own sync, so it is B's baseline: an older one on B is refused.
        repo.onSyncMessage(sync(listOf("GPU"), listOf("GPU", "RAM"), 0), connectionEpoch = 2)
        assertEquals(listOf("RAM"), repo.state.value.pinned)
    }

    @Test
    fun `a sync held for B survives the disconnect of A that the collector sees late`() = runTest {
        repo.onConnected(1, "pc-a")
        repo.onSyncMessage(sync(listOf("RAM"), listOf("RAM"), 1), connectionEpoch = 2)
        repo.onDisconnected()
        repo.onConnected(2, "pc-b")
        assertEquals(listOf("RAM"), repo.state.value.pinned)
    }

    @Test
    fun `a straggler from an older connection is dropped`() = runTest {
        repo.onConnected(2, "pc-b")
        repo.onSyncMessage(sync(listOf("RAM"), listOf("RAM"), 1), connectionEpoch = 2)
        repo.onSyncMessage(sync(listOf("CPU"), listOf("CPU"), 50), connectionEpoch = 1)
        assertEquals(listOf("RAM"), repo.state.value.pinned)
        assertEquals(listOf("RAM"), store.saved["pc-b"]?.names)
    }

    @Test
    fun `a held sync for a connection that never came is not applied to the one that did`() = runTest {
        repo.onSyncMessage(sync(listOf("CPU"), listOf("CPU"), 1), connectionEpoch = 3)
        repo.onConnected(4, "pc-b")
        assertEquals(emptyList<String>(), repo.state.value.pinned)
    }

    @Test
    fun `nothing changes while disconnected`() = runTest {
        assertFalse(repo.setPinned("CPU", pinned = true))
        assertTrue(sent.isEmpty())
        assertTrue(store.saved.isEmpty())
    }
}
