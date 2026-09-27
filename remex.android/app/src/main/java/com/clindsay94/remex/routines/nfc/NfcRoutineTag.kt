package com.clindsay94.remex.routines.nfc

import com.clindsay94.remex.routines.NfcTokens
import java.security.MessageDigest

/**
 * The URI a RemEx routine tag carries (routines spec §8.3.2): `remex://routine/<routineId>?t=<token>`.
 * The tag's second record is an Android Application Record for this package, so a tap always lands
 * in RemEx, and on a phone without RemEx it opens the store listing.
 *
 * Pure JVM, so the parser can be proven off-device; a tag's contents are untrusted input.
 */
data class NfcRoutineTag(val routineId: String, val token: String) {
    fun toUri(): String = "$SCHEME://$HOST/$routineId?$TOKEN_PARAM=$token"

    companion object {
        const val SCHEME = "remex"
        const val HOST = "routine"
        private const val TOKEN_PARAM = "t"
        private const val MAX_URI_LENGTH = 200
        private val routineIdPattern = Regex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")

        /** The routine and token a tag's URI names, or null when it is not a well-formed RemEx tag. */
        fun parse(uri: String?): NfcRoutineTag? {
            if (uri == null || uri.length > MAX_URI_LENGTH) return null
            val prefix = "$SCHEME://$HOST/"
            if (!uri.startsWith(prefix, ignoreCase = true)) return null
            val rest = uri.substring(prefix.length)
            val query = rest.indexOf('?')
            if (query <= 0) return null
            val routineId = rest.substring(0, query)
            if (!routineIdPattern.matches(routineId)) return null
            val params = rest.substring(query + 1).split('&')
            val tokens = params.filter { it.startsWith("$TOKEN_PARAM=") }.map { it.substringAfter('=') }
            val token = tokens.singleOrNull() ?: return null
            if (!NfcTokens.isWellFormed(token)) return null
            return NfcRoutineTag(routineId, token)
        }

        /** Whether [uri] is a RemEx routine tag at all, even a malformed or stale one. */
        fun looksLikeRoutineTag(uri: String?): Boolean = uri?.startsWith("$SCHEME://$HOST/", ignoreCase = true) == true
    }
}

/** What a tag tap may do (spec §8.3.2 checks, T2, T3). */
sealed interface NfcTapVerdict {
    /** Run the routine. */
    data class Run(val routineId: String) : NfcTapVerdict

    /**
     * Refused with `nfc_unknown_tag`: the routine is unknown here or the token does not match (a tag
     * for another phone, an old tag after a rewrite, a clone of one). [routineId] is set when the
     * routine exists on this phone, so the refusal is recorded against it.
     */
    data class UnknownTag(val routineId: String?) : NfcTapVerdict

    /** Refused with `nfc_device_locked`: the token is right but the phone is locked. */
    data class DeviceLocked(val routineId: String) : NfcTapVerdict

    /** A second tap within the debounce window (a double tap); ignored silently. */
    data class Debounced(val routineId: String) : NfcTapVerdict

    /** Not a RemEx routine tag. */
    data object NotRoutineTag : NfcTapVerdict
}

/**
 * The tap checks (spec §8.3.2): the token is compared in constant time, the phone must be unlocked,
 * and one routine runs at most once per [debounceMs] (10 s) however often the tag is tapped.
 *
 * The token is checked FIRST, so a locked phone never tells a stranger's tag apart from a real one,
 * and only a verified tap starts the debounce window.
 */
class NfcTokenVerifier(private val debounceMs: Long = DEBOUNCE_MS) {
    private val lastRunAt = HashMap<String, Long>()

    @Synchronized
    fun verify(
        tag: NfcRoutineTag?,
        storedToken: String?,
        routineExists: Boolean,
        deviceLocked: Boolean,
        nowElapsedMs: Long,
    ): NfcTapVerdict {
        if (tag == null) return NfcTapVerdict.NotRoutineTag
        if (!routineExists) return NfcTapVerdict.UnknownTag(null)
        if (storedToken == null || !tokensEqual(tag.token, storedToken)) return NfcTapVerdict.UnknownTag(tag.routineId)
        if (deviceLocked) return NfcTapVerdict.DeviceLocked(tag.routineId)
        val last = lastRunAt[tag.routineId]
        if (last != null && nowElapsedMs - last in 0 until debounceMs) return NfcTapVerdict.Debounced(tag.routineId)
        lastRunAt[tag.routineId] = nowElapsedMs
        return NfcTapVerdict.Run(tag.routineId)
    }

    /** "Test it" in the write sheet (spec 1.5): the id and token are checked and nothing runs. */
    fun matches(tag: NfcRoutineTag?, routineId: String, storedToken: String?): Boolean =
        tag != null && tag.routineId == routineId && storedToken != null && tokensEqual(tag.token, storedToken)

    companion object {
        const val DEBOUNCE_MS = 10_000L

        fun tokensEqual(a: String, b: String): Boolean =
            MessageDigest.isEqual(a.toByteArray(Charsets.US_ASCII), b.toByteArray(Charsets.US_ASCII))
    }
}
