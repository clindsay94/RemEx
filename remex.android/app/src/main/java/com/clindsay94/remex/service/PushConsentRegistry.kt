package com.clindsay94.remex.service

import java.util.Collections
import java.util.UUID
import org.json.JSONArray

/**
 * Roughly how many characters of file names a consent prompt should carry.
 *
 * A soft budget, not a hard truncation: the name that crosses it is still shown whole, because a
 * half-written file name is worse than a long one. Sized so an ordinary multi-file share lists every
 * name — the cases this cannot show are the ones no prompt could show usefully anyway.
 */
internal const val OFFERED_NAMES_BUDGET = 240

/**
 * Joins the offered names for a consent prompt, saying how many are not shown (RemEx-7iub).
 *
 * **THE PROMPT USED TO NAME FIVE AND AUTHORISE ALL OF THEM.** Beyond the fifth it appended a bare
 * "…", so a ten-file offer asked the user to approve five files they could read and five they could
 * not — while the grant covers every one. That mattered more once each id was bound to the name it
 * was minted for (RemEx-tutz), because a binding is only as meaningful as what the person was shown.
 *
 * Budgeting by LENGTH rather than by count is what closes it for real offers: five was arbitrary and
 * a share of eight photos is ordinary, so this lists them all. When a genuinely huge offer still
 * overflows, the remainder is stated as a NUMBER rather than an ellipsis — "+37" is a fact the user
 * can weigh, where "…" is only an admission that something was hidden.
 *
 * Deliberately no words. This is the data half of the prompt and the localized chrome wraps it, so
 * prose here would be untranslated English in eight of the nine languages. Kept character-identical
 * to the PC's `FileTransferHandler.JoinOfferedNames` so both ends of the same protocol describe an
 * offer the same way.
 */
internal fun joinOfferedNames(names: List<String>): String {
    if (names.isEmpty()) return ""

    val builder = StringBuilder()
    var shown = 0
    for (name in names) {
        if (shown > 0 && builder.length >= OFFERED_NAMES_BUDGET) break
        if (shown > 0) builder.append(", ")
        builder.append(name)
        shown++
    }

    if (shown < names.size) builder.append(", +").append(names.size - shown)
    return builder.toString()
}

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
internal fun mintPushGrants(filesArr: JSONArray): LinkedHashMap<String, GrantedFile> {
    val minted = LinkedHashMap<String, GrantedFile>(filesArr.length())
    for (i in 0 until filesArr.length()) {
        val id = UUID.randomUUID().toString().replace("-", "")
        val entry = filesArr.optJSONObject(i)
        minted[id] =
            GrantedFile(
                name = entry?.optString("name").orEmpty(),
                size = entry?.optLong("size", -1L) ?: -1L,
            )
    }
    return minted
}

/**
 * What the user agreed to receive under one transfer id — the name AND the size they were shown.
 *
 * Both are checked, because authorising a name alone still leaves the interesting half open: a PC can
 * prompt `holiday.jpg (1.0 KB)`, take the grant, and then negotiate the same id and the same name
 * carrying five gigabytes. The receiver does enforce the transfer's declared size as a ceiling on the
 * stream — but it was declaring its OWN number, and nothing compared that against the figure the user
 * saw (RemEx-ccqb).
 *
 * A [size] of -1 means the offer did not state one, and cannot match a transfer offer: `handleOffer`
 * refuses a negative size outright before any grant is consulted. That refusal is what makes the
 * sentinel safe — remex.core declares `Size` as a bare `long` with no validator, so a peer is free to
 * STATE -1 in both messages, and without the guard the two would have matched.
 *
 * Zero is deliberately NOT the sentinel: an empty file is a legitimate transfer, and the serializer
 * writes `"size":0` for one rather than omitting the field (`WhenWritingNull`, not
 * `WhenWritingDefault`), so absent and zero are distinguishable on the wire.
 */
data class GrantedFile(val name: String, val size: Long)

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
    private val granted: MutableMap<String, GrantedFile> =
        Collections.synchronizedMap(LinkedHashMap())

    /**
     * Records ids minted for a push the user accepted, each against the file name it was offered for.
     *
     * [idsToFiles] must describe the offer the user answered — `describePushFiles` renders its prompt
     * from the same array `mintPushGrants` walks, in the same order, so the two are aligned by
     * construction. Binding anything else would make the check below theatre.
     *
     * Not quite the same as "what the user read", and the gaps are worth naming. The prompt shows at
     * most FIVE names before eliding the rest, so an offer of ten binds five names the user saw and
     * five they did not (RemEx-7iub). It also shows one TOTAL size rather than a size per file, so at
     * counts above one the per-file figure bound here was never displayed on its own. Both bind
     * STRICTLY MORE than was shown, so both fail closed; neither is a licence to describe this as
     * exactly what the user read.
     */
    fun grant(idsToFiles: Map<String, GrantedFile>) {
        synchronized(granted) {
            for ((id, file) in idsToFiles) {
                // A blank name or an unstated size is dropped for the same reason a blank id is: it
                // could only ever be matched by an offer that is itself refused a step earlier, so
                // storing one occupies a capacity slot to authorise nothing — and can evict a live
                // grant to do it, since eviction is oldest-first.
                if (id.isNotBlank() && file.name.isNotBlank() && file.size >= 0) granted[id] = file
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
     * True when this id came from an accepted push offer AND describes the file that offer described.
     *
     * **THE ID ALONE AUTHORISES A TRANSFER, NOT A FILE, AND BOTH HALVES USED TO BE MISSING.** A
     * paired PC could take an id minted for `cat.jpg` and negotiate it carrying `resume.pdf`
     * (RemEx-tutz), or keep the name and inflate `holiday.jpg (1.0 KB)` into five gigabytes
     * (RemEx-ccqb). Either way the phone accepted without prompting again, and the user had agreed to
     * receive something else. The id proves a prompt was answered; the name and size prove it was
     * answered about THIS.
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
    fun isGrantedFor(id: String, fileName: String, size: Long): Boolean =
        granted[id] == GrantedFile(fileName, size)

    /**
     * Whether this id was granted at all, whatever file it was granted for.
     *
     * Distinguishes the two ways [isGrantedFor] fails, which are not the same event: an id nobody
     * granted came from a peer inventing one, while a granted id carrying the wrong file means this
     * device DID accept something and the offer no longer matches it. Only the second is worth telling
     * anybody about — reporting the first would let a peer post notifications at will (RemEx-gipu).
     *
     * NOT an authorisation check, and nothing may decide to receive a file on this alone.
     */
    fun hasGrant(id: String): Boolean = granted.containsKey(id)

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
