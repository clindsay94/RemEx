package com.clindsay94.remex.service

import java.util.Collections

/**
 * The transfer ids this device minted after the user consented to an incoming push.
 *
 * A `file_push_offer` is consent-gated: the user is prompted, and on acceptance this device mints
 * one fresh transfer id per offered file and returns them to the PC. The PC then negotiates each
 * file with `file_transfer_offer(mode="push")` carrying its assigned id.
 *
 * Nothing used to tie those two steps together. `handleOffer` accepted any PUSH offer whose
 * destination root happened to be writable, so a paired PC could skip `file_push_offer` entirely,
 * invent its own transfer id, and write files to a shared folder with no prompt ever shown
 * (RemEx-z6lh). The consent dialog was a step a well-behaved client chose to take rather than one
 * the protocol required.
 *
 * This registry is what makes the id unforgeable: only [grant] puts ids in, only `handlePushOffer`
 * calls it, and only after the user said yes.
 *
 * PUSH ONLY, deliberately. An UPLOAD lands in a folder the user has already shared for writing, so
 * the share IS the consent; requiring a second grant would break every ordinary upload.
 */
class PushConsentRegistry(private val capacity: Int = DEFAULT_CAPACITY) {

    // Insertion-ordered so the oldest grant is the one evicted at capacity. Synchronized rather
    // than a concurrent set because eviction has to read size and remove as one step.
    private val granted: MutableSet<String> = Collections.synchronizedSet(LinkedHashSet())

    /** Records ids minted for a push the user accepted. */
    fun grant(ids: Collection<String>) {
        synchronized(granted) {
            for (id in ids) {
                if (id.isNotBlank()) granted.add(id)
            }
            // A grant only happens after a person taps Accept, so this cannot be driven by a remote
            // peer alone - but an unbounded set that only ever grows is still the wrong shape for
            // something a peer can influence at all.
            while (granted.size > capacity) {
                val oldest = granted.first()
                granted.remove(oldest)
            }
        }
    }

    /** True when this id came from an accepted push offer. */
    fun isGranted(id: String): Boolean = granted.contains(id)

    /**
     * Forgets an id once its transfer is finished or abandoned.
     *
     * Deliberately NOT called when the offer is first accepted: a resumable transfer legitimately
     * re-offers the same id after an interruption, and consuming the grant on first use would make
     * resume fail with a consent error the user cannot act on.
     */
    fun release(id: String) {
        granted.remove(id)
    }

    /** Test seam: how many grants are outstanding. */
    internal val size: Int
        get() = granted.size

    private companion object {
        const val DEFAULT_CAPACITY = 256
    }
}
