package com.clindsay94.remex.ui

import com.clindsay94.remex.data.KnownHost
import com.clindsay94.remex.data.KnownPcEntry
import com.clindsay94.remex.ui.screens.PcEndpoint
import com.clindsay94.remex.ui.screens.YourPcCards
import com.clindsay94.remex.ui.screens.YourPcStatus
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** The Connection screen's "Your PCs" cards (RemEx-wqo7a.6). */
class YourPcCardsTest {

    private val studio =
        KnownHost(
            identity = "0123456789abcdef",
            nickname = "Studio PC",
            addresses = listOf("192.168.1.10", "100.72.10.4"),
            port = 5005,
            lastConnectedAtMillis = 3_000L,
        )

    private fun row(address: String, at: Long, trusted: Boolean = true, port: Int = 5005) =
        KnownPcEntry(
            address = address,
            port = port,
            nickname = if (trusted) "Studio PC" else "",
            lastConnectedAtMillis = at,
            knownHost = if (trusted) studio else null,
            isTrusted = trusted,
        )

    private val lan = row("192.168.1.10", at = 3_000L)
    private val tailscale = row("100.72.10.4", at = 2_000L)
    private val unpaired = row("192.168.1.42", at = 1_000L, trusted = false)

    @Test
    fun oneCardPerRememberedAddress_inMostRecentOrder_whenNothingIsConnected() {
        val cards = YourPcCards.build(listOf(lan, tailscale, unpaired), connected = null, connecting = null)
        assertEquals(listOf("192.168.1.10", "100.72.10.4", "192.168.1.42"), cards.map { it.key })
        assertEquals(
            listOf(YourPcStatus.Ready, YourPcStatus.Ready, YourPcStatus.NeedsPairing),
            cards.map { it.status },
        )
        assertTrue(cards.none { it.isCurrent })
    }

    @Test
    fun theConnectedAddressIsMarked_andMovesToTheTop() {
        val cards =
            YourPcCards.build(
                listOf(lan, tailscale, unpaired),
                connected = PcEndpoint("100.72.10.4", 5005),
                connecting = null,
            )
        assertEquals("100.72.10.4", cards.first().key)
        assertTrue(cards.first().isCurrent)
        // Same machine, other address: not the live connection, so it keeps its Connect button.
        val other = cards.first { it.key == "192.168.1.10" }
        assertFalse(other.isCurrent)
        assertEquals(1, cards.count { it.isCurrent })
        assertEquals(listOf("100.72.10.4", "192.168.1.10", "192.168.1.42"), cards.map { it.key })
    }

    @Test
    fun aDifferentPortIsNotTheSameConnection() {
        val cards =
            YourPcCards.build(listOf(lan), connected = PcEndpoint("192.168.1.10", 6006), connecting = null)
        assertFalse(cards.single().isCurrent)
    }

    @Test
    fun addressMatchIgnoresCaseAndSpaces() {
        val host = row("Studio.local", at = 1L)
        val cards = YourPcCards.build(listOf(host), connected = PcEndpoint(" studio.LOCAL ", 5005), connecting = null)
        assertTrue(cards.single().isCurrent)
    }

    @Test
    fun theAddressBeingConnectedToSaysConnecting() {
        val cards =
            YourPcCards.build(
                listOf(lan, unpaired),
                connected = null,
                connecting = PcEndpoint("192.168.1.42", 5005),
            )
        assertEquals(YourPcStatus.Connecting, cards.first { it.key == "192.168.1.42" }.status)
        assertEquals(YourPcStatus.Ready, cards.first { it.key == "192.168.1.10" }.status)
    }

    @Test
    fun switchingPcs_showsTheOldOneConnectedAndTheNewOneConnecting() {
        val cards =
            YourPcCards.build(
                listOf(lan, unpaired),
                connected = PcEndpoint("192.168.1.10", 5005),
                connecting = PcEndpoint("192.168.1.42", 5005),
            )
        assertEquals(YourPcStatus.ConnectedNow, cards.first { it.key == "192.168.1.10" }.status)
        assertEquals(YourPcStatus.Connecting, cards.first { it.key == "192.168.1.42" }.status)
    }

    @Test
    fun noRows_noCards() {
        assertTrue(YourPcCards.build(emptyList(), connected = PcEndpoint("192.168.1.10", 5005), connecting = null).isEmpty())
    }
}
