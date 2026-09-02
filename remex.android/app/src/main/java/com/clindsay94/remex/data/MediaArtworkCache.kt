package com.clindsay94.remex.data

/**
 * Content-addressed cache for decoded artwork, keyed by the wire's `artworkId` (RemEx-vtorl.4).
 *
 * Pure JVM — no Android class anywhere in this file — so it runs under `testReleaseUnitTest`
 * without a Robolectric shadow, and so [RemexClientManager] can hold one typed over [android.graphics.Bitmap]
 * without this file needing to know that. Every member is [Synchronized]: [onMediaState] reconciles on
 * the JNI delivery thread while [onMediaArtwork]'s decode result lands from a coroutine, and both call
 * in here.
 *
 * Deliberately has **no reset/clear method**. `RemexClientManager` must not reset this cache on
 * disconnect — content-addressed artwork is still valid after a reconnect to the same host, and the
 * whole point of caching by hash rather than by connection is that a reconnect need not re-fetch it.
 * `MediaArtworkCacheTest` pins the absence of such a method so that constraint cannot be "fixed" back in.
 */
class MediaArtworkCache<T : Any>(
        private val maxEntries: Int = 4,
        private val inFlightTtlMs: Long = 10_000L
) {
    /** LRU by access order; [put] and [get] both count as an access. */
    private val entries =
            object : LinkedHashMap<String, T>(16, 0.75f, /* accessOrder = */ true) {
                override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, T>): Boolean =
                        size > maxEntries
            }

    /** id -> the elapsedRealtime a request for it was begun. */
    private val inFlightSince = mutableMapOf<String, Long>()

    /**
     * Ids the host has said it no longer holds. Bounded so a chatty host asking for a large rotation
     * of ids cannot grow this without limit; oldest dropped first.
     */
    private val evicted = LinkedHashSet<String>()

    @Synchronized fun get(id: String): T? = entries[id]

    @Synchronized
    fun put(id: String, value: T) {
        entries[id] = value
        inFlightSince.remove(id)
    }

    /**
     * True exactly when [id] is worth asking the host for: not already cached, not already in flight
     * (or in flight long enough ago that the reply must have been lost), and not an id the host has
     * told us it evicted. Marks it in flight when it returns true, so a caller need not track that
     * separately.
     */
    @Synchronized
    fun tryBeginRequest(id: String, nowElapsedMs: Long): Boolean {
        if (id in evicted) return false
        if (entries.containsKey(id)) return false
        val since = inFlightSince[id]
        if (since != null && nowElapsedMs - since < inFlightTtlMs) return false
        inFlightSince[id] = nowElapsedMs
        return true
    }

    @Synchronized
    fun clearInFlight(id: String) {
        inFlightSince.remove(id)
    }

    /**
     * Clears every in-flight marker. Not a general reset — [entries] and [evicted] are untouched,
     * only requests outstanding on a connection that no longer exists are forgotten.
     */
    @Synchronized
    fun clearAllInFlight() {
        inFlightSince.clear()
    }

    @Synchronized
    fun markEvicted(id: String) {
        inFlightSince.remove(id)
        if (evicted.add(id) && evicted.size > MAX_EVICTED) {
            val oldest = evicted.iterator()
            oldest.next()
            oldest.remove()
        }
    }

    val size: Int
        @Synchronized get() = entries.size

    private companion object {
        const val MAX_EVICTED = 32
    }
}
