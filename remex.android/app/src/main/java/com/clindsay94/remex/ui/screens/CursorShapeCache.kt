package com.clindsay94.remex.ui.screens

/**
 * Remote Desktop cursor shapes, keyed by the host's shape serial, least-recently-used first out
 * (perf audit P0-20).
 *
 * The host mints a new serial on every cursor-shape change, so a plain map keyed by serial grew for
 * the whole session - one decoded bitmap per shape change, never dropped. This keeps the reuse (a
 * serial the cursor state points back at is still a hit) while bounding the worst case at
 * [maxEntries]. The serial the cursor state is currently selecting is touched on every state update,
 * so it is always the most recently used entry and is never the one evicted.
 *
 * Pure JVM - no Android class in this file - so it runs under `testReleaseUnitTest` without a
 * Robolectric shadow. Every member is [Synchronized]: an access-ordered [LinkedHashMap] reorders on
 * `get`, so reads mutate too, and the shape and state collectors both reach in here.
 *
 * Evicted and cleared values are simply dropped, not recycled: Compose may still be drawing the last
 * one, and letting the GC reclaim it is the policy `recycleCurrentFrame()` already follows.
 */
class CursorShapeCache<T : Any>(private val maxEntries: Int = DEFAULT_MAX_ENTRIES) {
    init {
        require(maxEntries > 0) { "maxEntries must be positive, was $maxEntries" }
    }

    /** LRU by access order; [set] and [get] both count as an access. */
    private val entries =
            object : LinkedHashMap<Long, T>(16, 0.75f, /* accessOrder = */ true) {
                override fun removeEldestEntry(eldest: MutableMap.MutableEntry<Long, T>): Boolean =
                        size > maxEntries
            }

    @Synchronized operator fun get(serial: Long): T? = entries[serial]

    @Synchronized
    operator fun set(serial: Long, value: T) {
        entries[serial] = value
    }

    /** Drops every shape. Called on stream stop, disconnect and display switch. */
    @Synchronized
    fun clear() {
        entries.clear()
    }

    val size: Int
        @Synchronized get() = entries.size

    companion object {
        const val DEFAULT_MAX_ENTRIES = 32
    }
}
