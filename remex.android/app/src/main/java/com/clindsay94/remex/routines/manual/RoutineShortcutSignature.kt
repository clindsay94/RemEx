package com.clindsay94.remex.routines.manual

import java.security.MessageDigest
import java.util.Base64
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * The `sig` extra of a routine shortcut (routines spec §8.3.3, T1): HMAC-SHA256 of the routine id
 * under the phone's own shortcut key, which never leaves the encrypted secrets store.
 *
 * The shortcut activity is already non-exported; the signature is the second lock, so an intent that
 * somehow reaches it (a launcher bug, a stale shortcut for a deleted-and-reused id) still runs
 * nothing unless RemEx itself made it. Pure JVM.
 */
object RoutineShortcutSignature {
    private const val ALGORITHM = "HmacSHA256"
    private const val DOMAIN = "remex-routine-shortcut/v1:"

    fun sign(key: ByteArray, routineId: String): String {
        val mac = Mac.getInstance(ALGORITHM).apply { init(SecretKeySpec(key, ALGORITHM)) }
        val digest = mac.doFinal((DOMAIN + routineId).toByteArray(Charsets.UTF_8))
        return Base64.getUrlEncoder().withoutPadding().encodeToString(digest)
    }

    /** Constant-time check of [sig]; a missing or malformed one is simply false. */
    fun verify(key: ByteArray, routineId: String?, sig: String?): Boolean {
        if (routineId.isNullOrEmpty() || sig.isNullOrEmpty() || sig.length > 64) return false
        val expected = sign(key, routineId)
        return MessageDigest.isEqual(expected.toByteArray(Charsets.US_ASCII), sig.toByteArray(Charsets.US_ASCII))
    }
}
