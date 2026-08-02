package com.clindsay94.remex

import com.clindsay94.remex.data.DiscoveredHost
import com.clindsay94.remex.data.DiscoveredHostList
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the discovery-list reduction (RemEx-ado4).
 *
 * The defect being replaced was a silent coin flip: discovery resolved the first mDNS answer and
 * stopped, so with two PCs on one LAN the user never learned the second existed.
 */
class DiscoveredHostListTest {

    private fun host(name: String, address: String = "192.168.1.50", port: Int = 5005) =
        DiscoveredHost(serviceName = name, host = address, port = port)

    @Test
    fun `two PCs both appear`() {
        // The whole point. This is the case the old first-answer-wins behaviour could not represent.
        var list = emptyList<DiscoveredHost>()
        list = DiscoveredHostList.onFound(list, host("desktop", "192.168.1.50"))
        list = DiscoveredHostList.onFound(list, host("laptop", "192.168.1.51"))

        assertEquals(2, list.size)
        assertEquals(listOf("desktop", "laptop"), list.map { it.serviceName })
    }

    @Test
    fun `a PC that changes address updates in place instead of appearing twice`() {
        // DHCP renewal, or a hop from Wi-Fi to Ethernet. Same machine, new address - identity is
        // the service name, which mDNS guarantees unique on the link.
        var list = DiscoveredHostList.onFound(emptyList(), host("desktop", "192.168.1.50"))
        list = DiscoveredHostList.onFound(list, host("desktop", "192.168.1.77"))

        assertEquals(1, list.size)
        assertEquals("192.168.1.77", list.single().host)
    }

    @Test
    fun `a re-found service does not move to the end of the list`() {
        // THE ONE THAT PROTECTS THE USER'S FINGER. The list renders with entrance animations and
        // re-resolution happens on its own schedule, so appending a re-found service would shuffle
        // rows for reasons the user cannot see - and a tap could land on a different PC than the
        // one they aimed at.
        var list = emptyList<DiscoveredHost>()
        list = DiscoveredHostList.onFound(list, host("desktop"))
        list = DiscoveredHostList.onFound(list, host("laptop"))
        list = DiscoveredHostList.onFound(list, host("server"))

        list = DiscoveredHostList.onFound(list, host("desktop", "192.168.1.99"))

        assertEquals(listOf("desktop", "laptop", "server"), list.map { it.serviceName })
        assertEquals("192.168.1.99", list.first().host)
    }

    @Test
    fun `a lost service is removed even when the callback carries a stale address`() {
        // A lost-service notification does not repeat the resolve, so it may arrive with a blank
        // host or a zero port. Matching on the whole object would silently remove NOTHING and leave
        // a dead PC in the list for the user to tap.
        var list = emptyList<DiscoveredHost>()
        list = DiscoveredHostList.onFound(list, host("desktop", "192.168.1.50", 5005))
        list = DiscoveredHostList.onFound(list, host("laptop", "192.168.1.51", 5005))

        list = DiscoveredHostList.onLost(list, "desktop")

        assertEquals(listOf("laptop"), list.map { it.serviceName })
    }

    @Test
    fun `losing a service that was never in the list is harmless`() {
        val list = DiscoveredHostList.onFound(emptyList(), host("desktop"))

        assertEquals(list, DiscoveredHostList.onLost(list, "never-seen"))
    }

    @Test
    fun `a service can be lost and found again`() {
        // Wi-Fi drops, comes back. The row must return rather than the app needing a screen bounce.
        var list = DiscoveredHostList.onFound(emptyList(), host("desktop"))
        list = DiscoveredHostList.onLost(list, "desktop")
        assertTrue(list.isEmpty())

        list = DiscoveredHostList.onFound(list, host("desktop", "192.168.1.60"))
        assertEquals(1, list.size)
        assertEquals("192.168.1.60", list.single().host)
    }

    @Test
    fun `autofill fires for one PC and never for several`() {
        // AUTOFILLING WITH SEVERAL VISIBLE IS THE ORIGINAL BUG WEARING A LIST: the user would see
        // three PCs and find the field already filled with whichever answered first.
        assertFalse(DiscoveredHostList.shouldAutofill(emptyList()))
        assertTrue(DiscoveredHostList.shouldAutofill(listOf(host("desktop"))))
        assertFalse(DiscoveredHostList.shouldAutofill(listOf(host("desktop"), host("laptop"))))
    }

    @Test
    fun `an unresolved service is not offered as a tappable row`() {
        // A service is announced before it is resolved, so host can be blank and port zero. Such a
        // row is a tap that does nothing, which reads as the app being broken rather than as
        // discovery still working.
        val list = listOf(
            host("resolved", "192.168.1.50", 5005),
            host("announced-only", "", 0),
            host("no-port", "192.168.1.51", 0),
            host("bad-port", "192.168.1.52", 70000)
        )

        assertEquals(listOf("resolved"), DiscoveredHostList.usableOnly(list).map { it.serviceName })
    }

    @Test
    fun `the input list is never mutated`() {
        // The list is held in a StateFlow; mutating in place would let Compose miss the change and
        // render a stale list that is technically already correct in memory.
        val original = listOf(host("desktop"))

        DiscoveredHostList.onFound(original, host("laptop"))
        DiscoveredHostList.onFound(original, host("desktop", "192.168.1.99"))
        DiscoveredHostList.onLost(original, "desktop")

        assertEquals(listOf("desktop"), original.map { it.serviceName })
        assertEquals("192.168.1.50", original.single().host)
    }
}
