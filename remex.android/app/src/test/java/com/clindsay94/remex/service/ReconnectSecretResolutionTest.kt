package com.clindsay94.remex.service

import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Which stored reconnect secret the binary `/ws/files` channel presents (RemEx-6bfyt).
 *
 * **THE DEFECT WAS A LOOKUP KEYED ON THE WRONG THING.** Pairing writes one secret under three
 * aliases — `hostId`, the host address, and the SPKI hash — each encrypted separately under its own
 * alias, so they are independent records rather than three views of one. Re-pairing over a different
 * address (LAN today, Tailscale tomorrow) refreshes the SPKI record and leaves a STALE secret under
 * the old address key. `ensureConnected` looked the secret up by address alone, found that stale one,
 * and computed a perfectly well-formed HMAC that could never verify.
 *
 * What made it expensive to find is that nothing said "wrong credential". The host rejected
 * `/ws/files` for proof-of-possession, so the channel never registered, and the download failed much
 * later with "The binary file channel is not connected." — a message about a socket. Meanwhile `/ws`
 * kept working, because the control channel resolves SPKI-first already (RemEx-060g) and so held the
 * fresh secret. A phone that was plainly connected could not transfer a single byte.
 *
 * The order is the fix, so the order is what is pinned here.
 */
class ReconnectSecretResolutionTest {

    private companion object {
        const val SPKI = "s0mEsPk1Hash="
        const val HOST = "192.168.1.10"
        const val FRESH = "fresh-secret-from-the-latest-pairing"
        const val STALE = "stale-secret-left-under-the-old-address"
    }

    @Test
    fun `the SPKI secret wins when an address-keyed one also exists`() = runBlocking {
        // THE BUG, EXACTLY. Both records exist and they disagree; the address one is the stale
        // survivor of an earlier pairing. A resolver that reads the address first returns STALE here
        // and every transfer fails proof-of-possession — which is what shipped.
        val store = mapOf(SPKI to FRESH, HOST to STALE)

        assertEquals(FRESH, resolveReconnectSecret(SPKI, HOST) { store[it] })
    }

    @Test
    fun `a pairing with no SPKI record still resolves by address`() {
        // Pairings made before the SPKI alias existed have ONLY the address key. Preferring SPKI must
        // not mean requiring it: dropping this fallback would refuse the channel outright for those
        // clients instead of letting them work until they happen to re-pair.
        val store = mapOf(HOST to STALE)

        assertEquals(STALE, runBlocking { resolveReconnectSecret(SPKI, HOST) { store[it] } })
    }

    @Test
    fun `the address alias is not consulted once the SPKI alias answers`() {
        // Ordering, proved by observation rather than by the returned value — a resolver that read
        // BOTH and then picked the SPKI one would satisfy the first test while still paying a
        // decrypt it does not need. Pinning the calls is what stops "prefer" degrading into "also".
        val asked = mutableListOf<String>()
        val store = mapOf(SPKI to FRESH, HOST to STALE)

        val resolved = runBlocking {
            resolveReconnectSecret(SPKI, HOST) { key ->
                asked += key
                store[key]
            }
        }

        assertEquals(FRESH, resolved)
        assertEquals(listOf(SPKI), asked)
    }

    @Test
    fun `no stored secret under either alias resolves to nothing`() {
        // Anti-vacuity, and it is load-bearing: the caller treats null as "refuse to connect and say
        // a re-pair is required". A resolver that invented a blank string instead would dial the
        // socket and fail the challenge silently, which is the failure this bead is about.
        assertNull(runBlocking { resolveReconnectSecret(SPKI, HOST) { null } })
    }
}
