package com.clindsay94.remex.routines

import java.security.SecureRandom
import java.util.Base64
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONObject

/** One routine's NFC token and when a tag was last written with it (spec §6.8, §8.3.2). */
data class RoutineNfcBinding(val token: String, val writtenAtUnixMs: Long)

/**
 * The `remex_routine_secrets` store (routines spec §6.8): NFC tokens, the shortcut HMAC key, the
 * home-fingerprint HMAC key and which routine each home-screen widget runs. Every value is its own
 * AEAD blob bound to its key (`"remex_routine_secrets/<key>"`), so a blob moved to another key does
 * not open.
 *
 * Excluded from backup and device transfer with the other routine stores (T12), and cleared with them
 * when the routine keyset is lost ([TinkRoutineCipherSource]). A lost secret is safe: tags and
 * shortcuts stop working and the user writes or pins them again; nothing runs.
 *
 * Never log a value read from here (T11).
 */
class RoutineSecretStore(
    private val kv: RoutineKeyValueStore,
    private val cipherSource: RoutineCipherSource,
    private val random: SecureRandom = SecureRandom(),
) {
    private val mutex = Mutex()

    suspend fun nfcBinding(routineId: String): RoutineNfcBinding? {
        val text = read(nfcKey(routineId)) ?: return null
        return runCatching {
            val obj = JSONObject(text)
            val token = obj.getString("t").takeIf { NfcTokens.isWellFormed(it) } ?: return null
            RoutineNfcBinding(token, obj.optLong("w", 0L))
        }.getOrNull()
    }

    /**
     * Stores [token] as [routineId]'s tag token. A token different from the stored one is a rotation:
     * every tag written with the old one stops working (spec §8.3.2 "Rewrite tag").
     */
    suspend fun commitNfcToken(routineId: String, token: String, writtenAtUnixMs: Long) {
        require(NfcTokens.isWellFormed(token)) { "not an NFC token" }
        write(nfcKey(routineId), JSONObject().put("t", token).put("w", writtenAtUnixMs).toString())
    }

    /** Forgets everything bound to a deleted routine: its tag token. */
    suspend fun removeRoutine(routineId: String) {
        mutex.withLock { kv.remove(nfcKey(routineId)) }
    }

    /** The 256-bit shortcut key, created on first use (spec §8.3.3). */
    suspend fun shortcutKey(): ByteArray = key(SHORTCUT_KEY)

    /** The 256-bit key the home fingerprint tokens are made with (spec D9, §8.3.1). */
    suspend fun homeKey(): ByteArray = key(HOME_KEY)

    suspend fun widgetRoutine(appWidgetId: Int): String? = read(widgetKey(appWidgetId))

    suspend fun setWidgetRoutine(appWidgetId: Int, routineId: String) = write(widgetKey(appWidgetId), routineId)

    suspend fun removeWidget(appWidgetId: Int) {
        mutex.withLock { kv.remove(widgetKey(appWidgetId)) }
    }

    private suspend fun key(name: String): ByteArray =
        mutex.withLock {
            val cipher = cipherSource.cipher()
            val existing = kv.get(name)?.let { cipher.open(it, associatedData(name)) }?.let(::decodeKey)
            existing ?: ByteArray(KEY_BYTES).also { fresh ->
                random.nextBytes(fresh)
                kv.put(name, cipher.seal(Base64.getEncoder().encodeToString(fresh), associatedData(name)))
            }
        }

    private fun decodeKey(text: String): ByteArray? =
        runCatching { Base64.getDecoder().decode(text) }.getOrNull()?.takeIf { it.size == KEY_BYTES }

    private suspend fun read(name: String): String? =
        mutex.withLock {
            val sealed = kv.get(name) ?: return null
            cipherSource.cipher().open(sealed, associatedData(name))
        }

    private suspend fun write(name: String, value: String) {
        mutex.withLock { kv.put(name, cipherSource.cipher().seal(value, associatedData(name))) }
    }

    companion object {
        const val SHORTCUT_KEY = "shortcutKey"
        const val HOME_KEY = "homeKey"
        private const val KEY_BYTES = 32

        fun nfcKey(routineId: String): String = "nfc/$routineId"

        fun widgetKey(appWidgetId: Int): String = "widget/$appWidgetId"

        fun associatedData(key: String): String = "remex_routine_secrets/$key"
    }
}

/** NFC tag tokens (spec §8.3.2): 128-bit `SecureRandom`, base64url without padding, 22 characters. */
object NfcTokens {
    private const val TOKEN_BYTES = 16
    const val LENGTH = 22
    private val alphabet = Regex("^[A-Za-z0-9_-]{$LENGTH}$")

    fun mint(random: SecureRandom = SecureRandom()): String {
        val bytes = ByteArray(TOKEN_BYTES).also(random::nextBytes)
        return Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)
    }

    fun isWellFormed(token: String?): Boolean = token != null && alphabet.matches(token)
}
