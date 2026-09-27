package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.RoutineJson
import com.clindsay94.remex.routines.model.RoutineRun
import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject

// The storage seams of the phone routine store (routines spec §6.8, RemEx-pp0rt.5). Everything here
// is pure JVM: the Android side supplies a DataStore-backed [RoutineKeyValueStore] and a Tink-backed
// [RoutineCipherSource] (RoutineAndroidStorage.kt), the JVM tests supply in-memory ones.

/** Authenticated encryption of one value under one piece of associated data. */
interface RoutineCipher {
    /** [plainText] sealed and base64-encoded. */
    fun seal(plainText: String, associatedData: String): String

    /** The plain text, or null when [sealed] cannot be opened (other key, tampered, not base64). */
    fun open(sealed: String, associatedData: String): String?
}

/**
 * Where the routine cipher comes from.
 *
 * Building it can find the keyset unusable (a keystore key invalidated by a lock-screen change, or a
 * restore onto another device). The Android source then discards the keyset AND every routine store
 * encrypted under it, and leaves a durable marker ([pendingKeyLossAtUnixMs]) so the reset is REPORTED
 * (`store_reset`, §6.8) instead of presenting as routines that quietly vanished.
 */
interface RoutineCipherSource {
    suspend fun cipher(): RoutineCipher

    /** When a keyset loss forced a reset that has not been reported yet, else null. */
    suspend fun pendingKeyLossAtUnixMs(): Long?

    /** Called once the reset has been recorded in the store and the history. */
    suspend fun clearKeyLossMarker()
}

/** One Preferences DataStore file as string keys and values. */
interface RoutineKeyValueStore {
    suspend fun get(key: String): String?

    suspend fun getAll(): Map<String, String>

    suspend fun put(key: String, value: String)

    suspend fun remove(key: String)
}

/** What reading the `remex_routines` document found. */
sealed interface RoutineDocumentLoad {
    /** Nothing stored yet: a first run, or the store after a reported reset. */
    data object Empty : RoutineDocumentLoad

    data class Loaded(val document: RoutineStoreDocument) : RoutineDocumentLoad

    /**
     * Something is stored and it cannot be read. NEVER to be replaced by an empty document without
     * the user asking (§6.7). [decryptedText] is the plain text when it decrypted but did not parse,
     * which is what "Export raw" offers; null when it did not even decrypt.
     */
    data class Unreadable(val decryptedText: String?) : RoutineDocumentLoad
}

/** The whole-set document is capped (§6.8) so one write can never grow without bound. */
class RoutineDocumentTooLargeException(bytes: Int) :
    IllegalStateException("Routine document is $bytes bytes; the cap is ${RoutineDocumentStore.MAX_BYTES}.")

/**
 * The `remex_routines` store: one key, `doc`, holding the AEAD-sealed [RoutineStoreDocument]
 * (associated data `remex_routines/doc/v1`, §6.8).
 */
class RoutineDocumentStore(private val kv: RoutineKeyValueStore, private val cipherSource: RoutineCipherSource) {

    suspend fun load(): RoutineDocumentLoad {
        val cipher = cipherSource.cipher()
        val sealed = kv.get(KEY) ?: return RoutineDocumentLoad.Empty
        val text = cipher.open(sealed, ASSOCIATED_DATA) ?: return RoutineDocumentLoad.Unreadable(null)
        val doc = RoutineStoreCodec.decode(text) ?: return RoutineDocumentLoad.Unreadable(text)
        return RoutineDocumentLoad.Loaded(doc)
    }

    suspend fun save(document: RoutineStoreDocument) {
        val text = RoutineStoreCodec.encode(document)
        val bytes = text.toByteArray(Charsets.UTF_8).size
        if (bytes > MAX_BYTES) throw RoutineDocumentTooLargeException(bytes)
        kv.put(KEY, cipherSource.cipher().seal(text, ASSOCIATED_DATA))
    }

    /** Only for the user's explicit "Reset" on an unreadable store. */
    suspend fun clear() {
        kv.remove(KEY)
    }

    companion object {
        const val KEY = "doc"
        const val ASSOCIATED_DATA = "remex_routines/doc/v1"
        const val MAX_BYTES = 256 * 1024
    }
}

/**
 * The `remex_routine_history` store (§6.8): one AEAD blob per routine under `run/<routineId>`, so
 * recording a run rewrites that routine's history and nothing else. Store-level events (the
 * `store_reset` record) live under [STORE_EVENTS_ID].
 *
 * A blob that cannot be opened is skipped on read and logged, not surfaced: history is RemEx's own
 * record rather than something the user wrote, and the next run of that routine replaces it.
 */
class RoutineHistoryStore(private val kv: RoutineKeyValueStore, private val cipherSource: RoutineCipherSource) {

    suspend fun loadAll(): Map<String, List<RoutineRun>> {
        val cipher = cipherSource.cipher()
        val result = LinkedHashMap<String, List<RoutineRun>>()
        for ((key, sealed) in kv.getAll()) {
            if (!key.startsWith(PREFIX)) continue
            val routineId = key.removePrefix(PREFIX)
            val runs = cipher.open(sealed, associatedData(key))?.let(::decodeRuns)
            if (runs == null) {
                RoutineLog.w("History for routine ${RoutineLog.id(routineId)} could not be read; it will be replaced.")
                continue
            }
            result[routineId] = runs
        }
        return result
    }

    suspend fun write(routineId: String, runs: List<RoutineRun>) {
        val key = PREFIX + routineId
        if (runs.isEmpty()) {
            kv.remove(key)
            return
        }
        kv.put(key, cipherSource.cipher().seal(encodeRuns(runs), associatedData(key)))
    }

    suspend fun remove(routineId: String) {
        kv.remove(PREFIX + routineId)
    }

    companion object {
        const val PREFIX = "run/"

        /** Not a UUID, so it can never collide with a routine id. */
        const val STORE_EVENTS_ID = "_store"

        fun associatedData(key: String): String = "remex_routine_history/$key"

        internal fun encodeRuns(runs: List<RoutineRun>): String {
            val array = JSONArray()
            runs.forEach { array.put(RoutineJson.write(it)) }
            return JSONObject().put("runs", array).toString()
        }

        /** Unreadable records are dropped one by one; an unreadable document is null. */
        internal fun decodeRuns(text: String): List<RoutineRun>? =
            try {
                val array = JSONObject(text).optJSONArray("runs") ?: return null
                (0 until array.length()).mapNotNull { RoutineJson.readRun(array.opt(it)) }
            } catch (e: JSONException) {
                RoutineLog.w("A history document did not parse.", e)
                null
            }
    }
}
