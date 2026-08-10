package com.clindsay94.remex

import com.clindsay94.remex.data.DiscoveredHost
import com.clindsay94.remex.data.DiscoveredHostList
import com.clindsay94.remex.data.NsdDiscoverySession
import com.clindsay94.remex.data.NsdPlatform
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The mDNS discovery session: find, resolve, lose, stop (RemEx-tslc1).
 *
 * **WRITTEN AGAINST A FAKE PLATFORM BECAUSE THE REAL ONE CANNOT BE REACHED FROM HERE.** NsdManager
 * is framework-bound and this module has no Robolectric, so the alternative to these tests is not
 * different tests — it is none, on a feature whose only other verification needs two PCs on one LAN.
 * The fake drives the callbacks by hand, which also lets it produce orderings a real network would
 * only manage occasionally: a resolve landing after its service was lost, a second announcement for
 * a name already resolving, a service withdrawn before it ever resolved.
 *
 * The reduction rules themselves belong to DiscoveredHostList and are tested there. What is asserted
 * here is the session around them.
 */
class NsdDiscoverySessionTest {

    /** Records what the session asked of the platform, and lets a test answer. */
    private class FakePlatform : NsdPlatform {
        var listener: NsdPlatform.Listener? = null
        var discoveryStarted = 0
        var discoveryStopped = 0
        val resolving = mutableMapOf<String, (String, Int) -> Unit>()
        val cancelled = mutableListOf<String>()

        /**
         * Set to have cancelResolve report a loss synchronously.
         *
         * **THE CONTRACT PERMITS THIS; `NsdManagerPlatform` DOES NOT DO IT.** Its `cancelResolve`
         * only unregisters, and it maps `ServiceInfoCallback.onServiceLost` to nothing. So the
         * re-entrancy this drives is defence against the [NsdPlatform] interface rather than a shape
         * reproduced from the framework — worth knowing before anyone narrows the interface and
         * concludes the guard is dead weight.
         */
        var cancelAlsoReportsLost = false

        /** Set to fail discovery instead of starting it, as NsdManager's onStartDiscoveryFailed does. */
        var failDiscovery = false

        override fun startDiscovery(listener: NsdPlatform.Listener) {
            discoveryStarted++
            this.listener = listener
            stopped = false
            if (failDiscovery) listener.onDiscoveryFailed()
        }

        /**
         * Whether a real `stopServiceDiscovery` would have taken effect yet.
         *
         * **IT IS ASYNCHRONOUS, AND THE FIRST VERSION OF THIS FAKE PRETENDED OTHERWISE.** Modelling
         * stop as instant meant the fake could never deliver the queued announcement that arrives
         * after teardown — so it validated the implementation's assumptions back at it rather than
         * NsdManager's behaviour, and hid a defect that permanently broke the second screen visit.
         */
        var stopped = false

        /**
         * Set to have stopDiscovery report a loss for everything in flight.
         *
         * NsdManager does not synthesise losses on stop - it delivers onDiscoveryStopped. What IS
         * real is a genuine loss already queued when the stop is issued, arriving during teardown;
         * this drives that shape by a mechanism the framework does not use.
         */
        var stopAlsoReportsLost = false

        override fun stopDiscovery() {
            discoveryStopped++
            stopped = true
            if (stopAlsoReportsLost) {
                for (name in resolving.keys.toList()) listener?.onServiceLost(name)
            }
        }

        override fun startResolve(serviceName: String, onResolved: (String, Int) -> Unit) {
            // REFUSED WHILE STOPPED, WHICH IS WHAT A REAL ADAPTER DOES - it has no executor to
            // register a callback against once discovery has been torn down. Without this the fake
            // accepted a resolve on a stopped platform and remembered it, which is a thing that
            // cannot happen and which hid a real defect: a session that recorded a name as resolving
            // after a re-entrant stop looked identical to one that had not.
            if (stopped) return
            resolving[serviceName] = onResolved
        }

        override fun cancelResolve(serviceName: String) {
            cancelled.add(serviceName)
            resolving.remove(serviceName)
            if (cancelAlsoReportsLost) listener?.onServiceLost(serviceName)
        }

        /** Answers a resolve the session started. Fails loudly if it never started one. */
        fun resolve(serviceName: String, host: String, port: Int) {
            val cb = resolving[serviceName] ?: error("no resolve in flight for $serviceName")
            cb(host, port)
        }
    }

    private val emissions = mutableListOf<List<DiscoveredHost>>()
    private val platform = FakePlatform()
    private val session = NsdDiscoverySession(platform) { emissions.add(it) }

    private fun names(list: List<DiscoveredHost>) = list.map { it.serviceName }

    @Test
    fun `two PCs on one LAN both end up in the list`() {
        // THE WHOLE REASON THIS EXISTS. The path this replaces resolved the first answer and stopped,
        // so the second PC was never shown and the user had no way to learn it was there.
        session.start()
        platform.listener!!.onServiceFound("Desk PC")
        platform.listener!!.onServiceFound("Laptop")
        platform.resolve("Desk PC", "192.168.1.10", 8765)
        platform.resolve("Laptop", "192.168.1.22", 8765)

        assertEquals(listOf("Desk PC", "Laptop"), names(session.current))
        assertEquals(2, DiscoveredHostList.usableOnly(session.current).size)
    }

    @Test
    fun `a service enters the list before it resolves, and is hidden until it does`() {
        // Both halves matter. It has to ENTER, or a service withdrawn before its SRV record arrives
        // has no entry for onServiceLost to remove. It has to stay HIDDEN, or the user gets a row
        // that does nothing when tapped, which reads as the app being broken.
        session.start()
        platform.listener!!.onServiceFound("Desk PC")

        assertEquals(listOf("Desk PC"), names(session.current))
        assertTrue("an unresolved row must not be offered", DiscoveredHostList.usableOnly(session.current).isEmpty())

        platform.resolve("Desk PC", "192.168.1.10", 8765)
        assertEquals(1, DiscoveredHostList.usableOnly(session.current).size)
    }

    @Test
    fun `resolving updates the row in place rather than adding a second one`() {
        session.start()
        platform.listener!!.onServiceFound("Desk PC")
        platform.resolve("Desk PC", "192.168.1.10", 8765)

        assertEquals(1, session.current.size)
        assertEquals("192.168.1.10", session.current.single().host)

        // AND A RE-RESOLVE TO A NEW ADDRESS IS STILL ONE PC. A DHCP renewal or a hop from Wi-Fi to
        // Ethernet changes the address of the same machine; appending would show it twice.
        platform.resolve("Desk PC", "192.168.1.99", 8765)

        assertEquals(1, session.current.size)
        assertEquals("192.168.1.99", session.current.single().host)
    }

    @Test
    fun `a repeated announcement does not start a second resolve`() {
        // mDNS re-announces, and every extra resolve is a callback somebody has to unregister - the
        // leak shape that crashed the app when a late event landed on a dead executor (RemEx-0ov).
        session.start()
        platform.listener!!.onServiceFound("Desk PC")
        val first = platform.resolving["Desk PC"]
        platform.listener!!.onServiceFound("Desk PC")

        assertEquals(1, platform.resolving.size)
        assertTrue("the in-flight resolve must be left alone", first === platform.resolving["Desk PC"])
    }

    @Test
    fun `a lost service is removed and its resolve is cancelled`() {
        session.start()
        platform.listener!!.onServiceFound("Desk PC")
        platform.listener!!.onServiceFound("Laptop")
        platform.resolve("Desk PC", "192.168.1.10", 8765)
        platform.resolve("Laptop", "192.168.1.22", 8765)

        platform.listener!!.onServiceLost("Desk PC")

        assertEquals(listOf("Laptop"), names(session.current))
        assertEquals(listOf("Desk PC"), platform.cancelled)
    }

    @Test
    fun `a service lost before it ever resolved is still removed`() {
        // The case that needs an unresolved entry to exist at all. The loss callback carries only a
        // name - no address, no port - so matching on the whole object would remove nothing and
        // leave a dead PC in the list.
        session.start()
        platform.listener!!.onServiceFound("Desk PC")
        platform.listener!!.onServiceLost("Desk PC")

        assertTrue(session.current.isEmpty())
    }

    @Test
    fun `a resolve that lands after its service was lost does not resurrect it`() {
        // Reachable in production: unregistering a resolve is asynchronous, so a final update can
        // already be in flight when the loss arrives. Resurrecting the PC here would be permanent -
        // the lost callback has already been and gone, and nothing would remove it a second time.
        session.start()
        platform.listener!!.onServiceFound("Desk PC")
        val late = platform.resolving["Desk PC"]!!
        platform.listener!!.onServiceLost("Desk PC")

        late("192.168.1.10", 8765)

        assertTrue("a lost PC must not come back", session.current.isEmpty())
    }

    @Test
    fun `stop releases every resolve still in flight, not just discovery`() {
        // STOPPING DISCOVERY SAYS NOTHING ABOUT THE PER-SERVICE CALLBACKS ALREADY REGISTERED. Each
        // one outlives the session and eventually posts to an executor that is gone, which is
        // exactly the crash RemEx-0ov's DiscardPolicy was added to survive.
        session.start()
        platform.listener!!.onServiceFound("Desk PC")
        platform.listener!!.onServiceFound("Laptop")
        platform.resolve("Desk PC", "192.168.1.10", 8765)

        session.stop()

        assertEquals(1, platform.discoveryStopped)
        assertEquals(setOf("Desk PC", "Laptop"), platform.cancelled.toSet())
    }

    @Test
    fun `stop is idempotent and start does not restart a running session`() {
        // A screen can be left more than one way, and a second stop must not re-enter the platform.
        session.start()
        session.start()
        session.stop()
        session.stop()

        assertEquals(1, platform.discoveryStarted)
        assertEquals(1, platform.discoveryStopped)
    }

    @Test
    fun `a cancel that reports a loss synchronously does not break the stop it happened during`() {
        // WHAT THIS NOW GUARDS IS THE started FLAG, NOT THE COPY, and the first comment here said the
        // opposite. stop() sets started false BEFORE walking the set, so the re-entrant onServiceLost
        // returns without touching it - removing the .toList() copy no longer fails anything, which I
        // confirmed by mutation rather than assuming. What would still fail is taking the loss during
        // teardown: each name would be cancelled twice.
        platform.cancelAlsoReportsLost = true
        session.start()
        platform.listener!!.onServiceFound("Desk PC")
        platform.listener!!.onServiceFound("Laptop")

        session.stop()

        // ASSERTED AS A LIST, NOT A SET, BECAUSE THE MULTIPLICITY IS THE INTERESTING PART. A set
        // would hide a second cancel per name, which is exactly what the re-entrant path would
        // produce if the session took the loss it is being handed mid-teardown. Each name once.
        assertEquals(listOf("Desk PC", "Laptop"), platform.cancelled)
    }

    @Test
    fun `nothing is emitted when the list did not actually change`() {
        // The consumer is a StateFlow feeding a list with entrance animations. Re-emitting an equal
        // list costs a recomposition for no reason, and a repeated announcement is the common case.
        session.start()
        platform.listener!!.onServiceFound("Desk PC")
        platform.resolve("Desk PC", "192.168.1.10", 8765)
        val afterResolve = emissions.size

        platform.listener!!.onServiceFound("Desk PC")
        platform.resolve("Desk PC", "192.168.1.10", 8765)

        assertEquals(afterResolve, emissions.size)
    }

    @Test
    fun `an empty service name is ignored rather than entered as a blank row`() {
        session.start()
        platform.listener!!.onServiceFound("")

        assertTrue(session.current.isEmpty())
        assertTrue(platform.resolving.isEmpty())
    }

    @Test
    fun `a session stopped and started again discovers everything afresh`() {
        // THE LIFECYCLE A SCREEN ACTUALLY HAS. What this proves is the SESSION half: the list is
        // dropped on the way out and every service resolves again on the way back in.
        //
        // IT DOES NOT COVER THE ADAPTER HALF, and the first version of this comment claimed it did.
        // The same lifecycle broke NsdManagerPlatform far worse - it held one executor for the
        // object's life and shut it down on stop, so the second visit handed NsdManager a terminated
        // executor, which silently discards rather than throwing, and no PC ever resolved again.
        // FakePlatform has no executor and cannot have one, so restoring that defect leaves this
        // green. That half rests on reading, as the adapter's own KDoc says outright.
        session.start()
        platform.listener!!.onServiceFound("Desk PC")
        platform.resolve("Desk PC", "192.168.1.10", 8765)
        session.stop()

        // AND THE LIST IS DROPPED ON THE WAY OUT. Keeping it would show PCs that left the LAN while
        // the screen was closed: discovery was not running, so no loss callback arrived for them.
        assertTrue("a stopped session must not hold a stale list", session.current.isEmpty())

        session.start()
        platform.listener!!.onServiceFound("Desk PC")
        platform.resolve("Desk PC", "192.168.1.10", 8765)

        assertEquals(1, DiscoveredHostList.usableOnly(session.current).size)
    }

    @Test
    fun `an announcement that arrives after stop is ignored`() {
        // stopServiceDiscovery is asynchronous, so a queued announcement can still be delivered.
        // Accepting it did lasting damage: the name was recorded as resolving for a resolve that
        // never started, so on the NEXT visit it was skipped as already in flight and stayed a blank
        // row for the life of the process - the user's desk PC simply never appeared again.
        session.start()
        session.stop()
        platform.listener!!.onServiceFound("Desk PC")

        assertTrue(session.current.isEmpty())
        assertTrue("no resolve may be started after stop", platform.resolving.isEmpty())

        // The proof that it did no lasting harm: the same PC resolves normally on the next visit.
        session.start()
        platform.listener!!.onServiceFound("Desk PC")
        platform.resolve("Desk PC", "192.168.1.10", 8765)

        assertEquals(1, DiscoveredHostList.usableOnly(session.current).size)
    }

    @Test
    fun `losses reported while discovery is stopping do not dribble out one at a time`() {
        // A platform is free to report every service lost as discovery stops, and stop() calls
        // stopDiscovery BEFORE it clears the list - so without the started flag those losses would
        // each emit an intermediate list to a consumer that is already tearing down, and the screen
        // would watch its PCs vanish one by one on the way out. Teardown is one emission: empty.
        platform.stopAlsoReportsLost = true
        session.start()
        platform.listener!!.onServiceFound("Desk PC")
        platform.listener!!.onServiceFound("Laptop")
        platform.resolve("Desk PC", "192.168.1.10", 8765)
        platform.resolve("Laptop", "192.168.1.22", 8765)
        val beforeStop = emissions.size

        session.stop()

        assertEquals(beforeStop + 1, emissions.size)
        assertTrue(emissions.last().isEmpty())
    }

    @Test
    fun `a synchronous loss from cancelResolve does not recurse while the session is running`() {
        // The re-entrancy this guards is NOT the one during stop() - that is now also covered by the
        // started flag, which would mask it. This is the live path: a loss arrives, the session
        // cancels the resolve, and the platform reports the loss again from inside that cancel.
        platform.cancelAlsoReportsLost = true
        session.start()
        platform.listener!!.onServiceFound("Desk PC")
        platform.resolve("Desk PC", "192.168.1.10", 8765)

        platform.listener!!.onServiceLost("Desk PC")

        assertTrue(session.current.isEmpty())
        // Cancelled exactly once: the re-entry finds nothing resolving and nothing known, and stops.
        assertEquals(listOf("Desk PC"), platform.cancelled)
    }

    @Test
    fun `a discovery that fails to start is distinguishable from a LAN with no PCs on it`() {
        // BOTH LOOK LIKE AN EMPTY LIST, AND THAT IS THE PROBLEM. Without a failure signal the screen
        // shows nothing for as long as it is open and the user goes looking for a network fault that
        // is not there. The single-resolve path this replaces already resolved to null on the same
        // event, so the streaming path would have been the weaker of the two.
        platform.failDiscovery = true
        session.start()

        assertTrue("a failed start must be recorded", session.failed)
        assertTrue(session.current.isEmpty())

        // AND THE EMPTY-LAN CASE MUST NOT SET IT, or the flag says nothing. A clean start that simply
        // finds no PCs looks identical in the list and must not look identical here.
        val ok = NsdDiscoverySession(FakePlatform()) {}
        ok.start()
        assertFalse("finding nothing is not a failure", ok.failed)
    }

    @Test
    fun `retrying after a failure clears the flag`() {
        // Otherwise the screen would keep saying the search failed after a retry that worked.
        platform.failDiscovery = true
        session.start()
        session.stop()

        platform.failDiscovery = false
        session.start()
        platform.listener!!.onServiceFound("Desk PC")
        platform.resolve("Desk PC", "192.168.1.10", 8765)

        assertFalse(session.failed)
        assertEquals(1, session.usable.size)
    }

    @Test
    fun `usable is the filtered view and current is the raw accumulator`() {
        // The obvious thing to bind a list to should also be the correct one. current holds a service
        // from the moment it is announced, which is a row that does nothing when tapped.
        session.start()
        platform.listener!!.onServiceFound("Desk PC")

        assertEquals(1, session.current.size)
        assertTrue("an unresolved row must not be offered", session.usable.isEmpty())

        platform.resolve("Desk PC", "192.168.1.10", 8765)
        assertEquals(1, session.usable.size)
    }

    @Test
    fun `a consumer that stops the session from inside an emission does not poison it`() {
        // **THE WIRING THIS FEATURE IS MOST LIKELY TO GET.** DiscoveredHostList.shouldAutofill exists
        // so a single result can be filled in automatically, and the natural way to write that is to
        // autofill and stop from inside the emission. That re-enters stop() while onServiceFound is
        // still on the stack: stop clears `resolving`, then onServiceFound carried on and put the
        // name back for a resolve it started on a stopped platform. The name stayed pinned in
        // `resolving` forever, so on the next visit that PC was skipped as already resolving and
        // never appeared again - the same permanent invisibility the post-stop guard exists to
        // prevent, reached through the consumer instead, and with no second thread involved.
        val platform = FakePlatform()
        lateinit var session: NsdDiscoverySession
        // STOPS ON THE FIRST EMISSION ONLY. The unsafe window is the PLACEHOLDER emission - the row
        // that enters before a resolve - so that is where the re-entrant stop has to land. Two
        // earlier drafts of this test missed it: one filtered with usableOnly first, so the count
        // never reached one until the resolve had already happened and the stop landed somewhere
        // harmless; the other stopped on every emission, so the second visit stopped on its own
        // placeholder too and nothing could resolve either way. Both passed against the bug.
        var stops = 0
        session = NsdDiscoverySession(platform) { if (stops++ == 0) session.stop() }

        session.start()
        platform.listener!!.onServiceFound("Desk PC")

        // The session must come out of that clean. Leave the name recorded as resolving and it is
        // permanent: the resolve it belongs to was started on a platform that had already stopped.
        session.start()
        platform.listener!!.onServiceFound("Desk PC")

        assertTrue(
            "a PC seen before an autofill stop must still resolve on the next visit",
            platform.resolving.containsKey("Desk PC"),
        )
    }

    @Test
    fun `nothing reaches the platform before start`() {
        // Anti-vacuity for the suite: every test above drives the listener the session handed over
        // in start(), so a session that wired itself up in its constructor would satisfy them all
        // while leaving discovery running from the moment it was built.
        assertEquals(0, platform.discoveryStarted)
        assertEquals(null, platform.listener)
    }
}
