package com.clindsay94.remex.service

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Which host a binary file channel is connected to (RemEx-5t4k9).
 *
 * **THE DEFECT WAS A CALLER ASKING THE WRONG QUESTION.** `AndroidFileTransferHost.ensureBinaryChannel`
 * shortcut on a bare `isOpen` — is ANY socket up — before it had even read the configured host, so a
 * channel still open to a PC the user had stopped using answered yes, and `ensureConnected`, which
 * does compare the host, was never reached. The transfer was then negotiated over a connection to the
 * wrong machine.
 *
 * What is pinned here is the key itself: two places compare against it, and if they ever disagreed
 * about what identifies a connection, one of them would believe the channel goes somewhere the other
 * does not — which is the same silent wrong-machine transfer by a different route.
 */
class ChannelKeyTest {

    @Test
    fun `every field that identifies a connection changes the key`() {
        val base = ChannelTarget("192.168.1.10", 8765, "abc")

        // ALL THREE, NOT JUST THE HOST. Two PCs behind one address on different ports is ordinary on
        // a lab machine, and the client id is what the host keys its own side on - a key that ignored
        // either would call two different connections the same one.
        assertNotEquals(base, ChannelTarget("192.168.1.99", 8765, "abc"))
        assertNotEquals(base, ChannelTarget("192.168.1.10", 9000, "abc"))
        assertNotEquals(base, ChannelTarget("192.168.1.10", 8765, "xyz"))

        assertEquals("the same connection must produce the same key", base, ChannelTarget("192.168.1.10", 8765, "abc"))
    }

    @Test
    fun `the shared predicate only says yes to an open channel to the same machine`() {
        // **THE RULE THE BEAD IS ABOUT, AND THE ONLY PLACE IT CAN BE MUTATION-TESTED.** Two callers
        // decide "is the channel I want already up" - the shortcut inside ensureConnected and
        // isOpenTo - and both delegate here. The state they read is private to an object no unit test
        // in this module can reach, so extracting the decision is what makes the rule provable at all
        // rather than merely written down once.
        val target = ChannelTarget("192.168.1.10", 8765, "abc")

        assertTrue(channelMatches(open = true, current = target, target = target))

        // Open, but to a different machine - the whole defect. A version that ignored `current`
        // would pass the line above and fail this one.
        assertFalse(channelMatches(true, ChannelTarget("192.168.1.99", 8765, "abc"), target))
        assertFalse(channelMatches(true, ChannelTarget("192.168.1.10", 9000, "abc"), target))
        assertFalse(channelMatches(true, ChannelTarget("192.168.1.10", 8765, "xyz"), target))

        // Right machine, but nothing open. Dropping the `open` conjunct passes everything above.
        assertFalse(channelMatches(false, target, target))
        assertFalse(channelMatches(true, null, target))
    }

    @Test
    fun `a channel that was never opened is open to nobody`() {
        // Anti-vacuity for the caller side: isOpenTo has to answer false before anything connects, or
        // the shortcut it guards would wave every offer through on a fresh process.
        assertFalse(FileTransferChannelClient.isOpenTo("192.168.1.10", 8765, "abc"))
    }
}
