package com.clindsay94.remex.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Pins the LRU eviction, in-flight de-duplication, and eviction bookkeeping in [MediaArtworkCache]. */
class MediaArtworkCacheTest {

    @Test
    fun `putting a fifth entry into a 4-slot cache evicts the least recently used`() {
        val cache = MediaArtworkCache<String>(maxEntries = 4)
        cache.put("a", "A")
        cache.put("b", "B")
        cache.put("c", "C")
        cache.put("d", "D")

        // Touching "a" makes "b" the least recently used of the four.
        assertEquals("A", cache.get("a"))

        cache.put("e", "E")

        assertNull("the least recently used entry must be evicted", cache.get("b"))
        assertEquals("A", cache.get("a"))
        assertEquals("C", cache.get("c"))
        assertEquals("D", cache.get("d"))
        assertEquals("E", cache.get("e"))
        assertEquals(4, cache.size)
    }

    @Test
    fun `tryBeginRequest is true once then false until cleared`() {
        val cache = MediaArtworkCache<String>()

        assertTrue(cache.tryBeginRequest("id1", nowElapsedMs = 0L))
        assertFalse(
                "a second request for the same id while the first is still in flight must be refused",
                cache.tryBeginRequest("id1", nowElapsedMs = 100L)
        )

        cache.clearInFlight("id1")
        assertTrue(cache.tryBeginRequest("id1", nowElapsedMs = 200L))
    }

    @Test
    fun `tryBeginRequest is true again once the in-flight TTL elapses`() {
        val cache = MediaArtworkCache<String>(inFlightTtlMs = 1000L)

        assertTrue(cache.tryBeginRequest("id1", nowElapsedMs = 0L))
        assertFalse(cache.tryBeginRequest("id1", nowElapsedMs = 999L))
        assertTrue(
                "a reply that never arrives must not block a request forever",
                cache.tryBeginRequest("id1", nowElapsedMs = 1000L)
        )
    }

    @Test
    fun `putting a value clears its in-flight marker so a cached id is never re-requested`() {
        val cache = MediaArtworkCache<String>()
        assertTrue(cache.tryBeginRequest("id1", nowElapsedMs = 0L))

        cache.put("id1", "bytes")

        assertFalse(
                "an id that is now cached must not be requested again",
                cache.tryBeginRequest("id1", nowElapsedMs = 1L)
        )
    }

    @Test
    fun `markEvicted makes tryBeginRequest false forever and clears in-flight`() {
        val cache = MediaArtworkCache<String>()
        assertTrue(cache.tryBeginRequest("id1", nowElapsedMs = 0L))

        cache.markEvicted("id1")

        assertFalse(cache.tryBeginRequest("id1", nowElapsedMs = 1L))
        assertFalse(
                "an evicted id must stay refused however much time passes",
                cache.tryBeginRequest("id1", nowElapsedMs = 1_000_000L)
        )
        assertNull(cache.get("id1"))
    }

    @Test
    fun `the cache has no reset method, so its contents outlive a simulated disconnect`() {
        // There is no clear()/reset() to call here — that absence IS the assertion. Per contract,
        // RemexClientManager resets `mediaArtwork` on disconnect but never this cache, because
        // content-addressed artwork is still valid after a reconnect to the same host.
        val cache = MediaArtworkCache<String>()
        cache.put("id1", "bytes")

        assertEquals("bytes", cache.get("id1"))
        assertTrue(
                "MediaArtworkCache must expose no clear()/reset() method for a disconnect handler " +
                        "to call — the cache is not reset on disconnect by design",
                MediaArtworkCache::class.java.declaredMethods.none {
                    it.name == "clear" || it.name == "reset"
                }
        )
    }
}
