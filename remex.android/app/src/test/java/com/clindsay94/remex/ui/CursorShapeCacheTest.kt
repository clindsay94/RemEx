package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.CursorShapeCache
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Covers the Remote Desktop cursor-shape cache's bound (perf audit P0-20).
 *
 * The host mints a new shape serial on every cursor change, so the old serial-keyed map grew for the
 * whole session. The cache must stay capped however many distinct serials arrive, and a serial the
 * cursor state keeps pointing at must survive the churn.
 */
class CursorShapeCacheTest {

    @Test
    fun `never exceeds the cap however many distinct shapes arrive`() {
        val cache = CursorShapeCache<String>()
        for (serial in 1L..1_000L) cache[serial] = "shape$serial"

        assertEquals(CursorShapeCache.DEFAULT_MAX_ENTRIES, cache.size)
        // The newest survive, the oldest are gone.
        assertEquals("shape1000", cache[1_000L])
        assertNull(cache[1L])
    }

    @Test
    fun `the least recently used shape is the one evicted`() {
        val cache = CursorShapeCache<String>(maxEntries = 3)
        cache[1L] = "a"
        cache[2L] = "b"
        cache[3L] = "c"

        // A cursor-state lookup touches 1, so 2 is now the eldest.
        assertEquals("a", cache[1L])
        cache[4L] = "d"

        assertNull(cache[2L])
        assertEquals("a", cache[1L])
        assertEquals("c", cache[3L])
        assertEquals("d", cache[4L])
    }

    @Test
    fun `a shape the cursor keeps selecting survives continuous churn`() {
        val cache = CursorShapeCache<String>(maxEntries = 4)
        cache[7L] = "active"
        for (serial in 100L..200L) {
            cache[serial] = "other"
            assertEquals("active", cache[7L])
        }
        assertEquals(4, cache.size)
    }

    @Test
    fun `re-sending a serial replaces it without growing`() {
        val cache = CursorShapeCache<String>(maxEntries = 2)
        cache[1L] = "old"
        cache[1L] = "new"

        assertEquals(1, cache.size)
        assertEquals("new", cache[1L])
    }

    @Test
    fun `clear drops every shape`() {
        val cache = CursorShapeCache<String>()
        for (serial in 1L..10L) cache[serial] = "s"

        cache.clear()

        assertEquals(0, cache.size)
        assertNull(cache[5L])
    }

    @Test(expected = IllegalArgumentException::class)
    fun `a non-positive cap is rejected`() {
        CursorShapeCache<String>(maxEntries = 0)
    }
}
