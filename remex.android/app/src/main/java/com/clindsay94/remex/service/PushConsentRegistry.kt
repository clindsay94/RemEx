package com.clindsay94.remex.service

import java.util.Collections
import java.util.UUID
import org.json.JSONArray

/**
 * Mints one transfer id per offered file, each bound to that file's name (RemEx-tutz).
 *
 * Extracted from `AndroidFileTransferHost.handlePushOffer` so it can be tested. It was the untested
 * half of the binding, and the ways it can silently fail all look identical from the outside: read
 * the wrong JSON key and every value is `""`, read a fixed index and every id carries file one's
 * name. Either turns the name check into theatre — worse, into a check that refuses EVERY push while
 * the whole suite stays green, because nothing else exercises this loop.
 *
 * Insertion-ordered, and the caller emits `transferIds` from these keys, so the ids the PC receives
 * are index-aligned with the `files` array it sent — which is the alignment the protocol relies on
 * and the reason the name recorded for each id is the right one.
 *
 * A missing or non-object entry binds `""`. That is unusable rather than permissive: a later offer
 * would have to carry a blank `fileName` to match it, and the blank-name guard in
 * `FileHostHandler.beginHostReceive` refuses that anyway.
 */
internal fun mintPushGrants(filesArr: JSONArray): LinkedHashMap<String, String> {
    val minted = LinkedHashMap<String, String>(filesArr.length())
    for (i in 0 until filesArr.length()) {
        val id = UUID.randomUUID().toString().replace("-", "")
        minted[id] = filesArr.optJSONObject(i)?.optString("name").orEmpty()
    }
    return minted
}

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
    // than a concurrent map because eviction has to read size and remove as one step.
    //
    // Maps each id to the FILE NAME it was minted for. It used to be a bare set of ids, which made a
    // grant an authorisation to send *something* rather than to send the thing the user agreed to
    // (RemEx-tutz).
    private val granted: MutableMap<String, String> =
        Collections.synchronizedMap(LinkedHashMap())

    /**
     * Records ids minted for a push the user accepted, each against the file name it was offered for.
     *
     * [idsToFileNames] must be the names the offer the user answered was BUILT FROM —
     * `describePushFiles` renders its prompt from the same array `mintPushGrants` walks, in the same
     * order, so the two are aligned by construction. Binding anything else would make the check below
     * theatre.
     *
     * Not quite the same as "the names the user read", and the gap is worth naming: that prompt shows
     * at most FIVE names before it elides the rest, so an offer of ten binds five names the user saw
     * and five they did not. The substitution this guards against is closed either way — an id still
     * only ever carries the file it was minted for — but a prompt that names everything it is
     * authorising is RemEx-7iub. The offered SIZE is likewise shown and not bound: RemEx-ccqb.
     */
    fun grant(idsToFileNames: Map<String, String>) {
        synchronized(granted) {
            for ((id, fileName) in idsToFileNames) {
                // A blank name is dropped for the same reason a blank id is: it could only ever be
                // matched by an offer carrying a blank fileName, which beginHostReceive refuses
                // outright. Storing one occupies a capacity slot to authorise nothing.
                if (id.isNotBlank() && fileName.isNotBlank()) granted[id] = fileName
            }
            // A grant only happens after a person taps Accept, so this cannot be driven by a remote
            // peer alone - but an unbounded map that only ever grows is still the wrong shape for
            // something a peer can influence at all.
            while (granted.size > capacity) {
                val oldest = granted.keys.first()
                granted.remove(oldest)
            }
        }
    }

    /**
     * True when this id came from an accepted push offer AND names the file that offer described.
     *
     * **THE NAME IS HALF THE CHECK, AND IT USED TO BE MISSING (RemEx-tutz).** Matching on the id
     * alone authorises a transfer, not a file: a paired PC could take an id minted for `cat.jpg` and
     * negotiate it carrying `resume.pdf`, and the phone would accept without prompting again — the
     * user having agreed to receive something else entirely. The id proves a prompt was answered;
     * the name proves it was answered about THIS.
     *
     * Exact comparison, deliberately — and the reason is a PC-side invariant rather than anything
     * about these two strings arriving together, which they do NOT: they come in two separate
     * messages, `file_push_offer` and `file_transfer_offer`, potentially minutes apart. What makes
     * equality the right test is that `PingPongHandler` passes ONE local to both legs, unmodified, so
     * a legitimate pair is byte-identical with no filesystem round trip in between to renormalise
     * anything. Unicode form, trailing spaces and case therefore cannot legitimately differ, and
     * relaxing any of them would only widen what a crafted offer can pass.
     *
     * That invariant is held by one line in one handler and has been broken before — the comment
     * there records a version that re-derived the name from the path, which showed the user one name
     * while the transfer carried another. If it breaks again this check starts refusing honest
     * pushes, which is the safe direction to fail but an unhelpful one to debug.
     */
    fun isGrantedFor(id: String, fileName: String): Boolean = granted[id] == fileName

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
