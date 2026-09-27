package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineJson
import com.clindsay94.remex.routines.model.RoutineSchema
import com.clindsay94.remex.routines.model.RoutineSet
import org.json.JSONObject
import org.json.JSONTokener

/**
 * Per-PC sync bookkeeping inside the phone store (routines spec §6.8, §7.4.1).
 *
 * Every edit that changes this PC's PC-run subset or the pause flag BUMPS [localRevision] in the same
 * transaction (§7.4.1 step 1); run reports advance [runCursor]. The S4 sync client
 * ([RoutineSyncClient]) sends `routines_sync` and stores the answer in [ackedRevision] and
 * [lastResultJson], which [RoutineSyncStates] turns into what the screens show.
 */
data class RoutineHostSync(
    val localRevision: Long = 0,
    val ackedRevision: Long = 0,
    /** The last `routine_sync_result` from this PC, verbatim. */
    val lastResultJson: String? = null,
    /** Highest PC run `seq` stored on this phone (§7.3.6). */
    val runCursor: Long = 0,
    /** When the phone last authenticated to this PC from a network that is not home (§8.3.1). */
    val reachableAwayAtUnixMs: Long? = null,
    /**
     * When this PC's run history on the phone was last brought up to date (a sync answer or a run
     * report page). The history shows "As of" this time while the PC is not connected (R-UX-29).
     */
    val historyAsOfUnixMs: Long? = null,
    /** Keys this build does not know, kept verbatim (§6.7). */
    val unknownFieldsJson: String? = null,
)

/**
 * The decrypted body of the `remex_routines` store (§6.8): one document, so every edit is one
 * transactional write.
 *
 * @property homeJson the `Home` record (§6.6), owned by the S3 presence slice. Kept verbatim here.
 * @property storeResetAtUnixMs set when a keystore loss forced a reset (§6.8); the routines screen
 *   shows the `store_reset` banner until the user dismisses it, which clears this.
 * @property unknownFieldsJson top-level keys this build does not know, written back unchanged.
 */
data class RoutineStoreDocument(
    val schemaVersion: Int = RoutineSchema.CURRENT_VERSION,
    val pausedAll: Boolean = false,
    /** List order is the user's order (R-UX-18). */
    val routines: List<Routine> = emptyList(),
    val homeJson: String? = null,
    val hostSync: Map<String, RoutineHostSync> = emptyMap(),
    val storeResetAtUnixMs: Long? = null,
    val unknownFieldsJson: String? = null,
) {
    /** A document from a newer RemEx: shown, never edited, never written back (§6.7). */
    val isNewerThanReader: Boolean get() = schemaVersion > RoutineSchema.CURRENT_VERSION
}

/**
 * JSON for [RoutineStoreDocument]. Pure JVM.
 *
 * **[decode] RETURNS NULL RATHER THAN A DEFAULT, AND THAT IS THE WHOLE POINT.** A document that does
 * not parse is reported as unreadable and the store refuses to write over it (spec §6.7,
 * REGRESSION-GUARDS "a fallback profile must never be persisted"): turning a transient parse
 * failure into an empty document that then gets saved is permanent loss of the user's routines.
 * For the same reason a known key holding the wrong JSON type makes the whole document unreadable
 * instead of being coerced to a default and silently rewritten.
 */
object RoutineStoreCodec {
    private val KNOWN_KEYS = setOf("schemaVersion", "pausedAll", "routines", "home", "hostSync", "storeResetAtUnixMs")
    private val KNOWN_HOST_KEYS =
        setOf("localRevision", "ackedRevision", "lastResultJson", "runCursor", "reachableAwayAtUnixMs", "historyAsOfUnixMs")

    fun encode(doc: RoutineStoreDocument): String {
        val obj = doc.unknownFieldsJson?.let(::objectOrNull) ?: JSONObject()
        val set = RoutineJson.write(RoutineSet(schemaVersion = doc.schemaVersion, routines = doc.routines))
        obj.put("schemaVersion", set.get("schemaVersion"))
        obj.put("routines", set.get("routines"))
        obj.put("pausedAll", doc.pausedAll)
        doc.homeJson?.let { home -> obj.put("home", JSONTokener(home).nextValue()) }
        val sync = JSONObject()
        for ((host, state) in doc.hostSync.toSortedMap()) {
            val entry = state.unknownFieldsJson?.let(::objectOrNull) ?: JSONObject()
            entry.put("localRevision", state.localRevision)
            entry.put("ackedRevision", state.ackedRevision)
            entry.putOpt("lastResultJson", state.lastResultJson)
            entry.put("runCursor", state.runCursor)
            entry.putOpt("reachableAwayAtUnixMs", state.reachableAwayAtUnixMs)
            entry.putOpt("historyAsOfUnixMs", state.historyAsOfUnixMs)
            sync.put(host, entry)
        }
        obj.put("hostSync", sync)
        obj.putOpt("storeResetAtUnixMs", doc.storeResetAtUnixMs)
        return obj.toString()
    }

    /** The document, or null when it cannot be read faithfully. Never throws. */
    fun decode(text: String?): RoutineStoreDocument? =
        runCatching { decodeOrThrow(text ?: return null) }.getOrNull()

    private fun decodeOrThrow(text: String): RoutineStoreDocument? {
        val obj = objectOrNull(text) ?: return null
        val set = RoutineJson.readSet(obj) ?: return null
        if (obj.has("routines") && !obj.isNull("routines") && set.routines == null) return null

        val pausedAll = optTyped<Boolean>(obj, "pausedAll") ?: return null
        val home =
            when {
                !obj.has("home") || obj.isNull("home") -> null
                obj.opt("home") is JSONObject -> obj.getJSONObject("home").toString()
                else -> return null
            }
        val reset =
            when {
                !obj.has("storeResetAtUnixMs") || obj.isNull("storeResetAtUnixMs") -> null
                else -> (obj.opt("storeResetAtUnixMs") as? Number)?.toLong() ?: return null
            }
        val hostSync = LinkedHashMap<String, RoutineHostSync>()
        if (obj.has("hostSync") && !obj.isNull("hostSync")) {
            val sync = obj.opt("hostSync") as? JSONObject ?: return null
            for (host in sync.keys()) {
                val entry = sync.opt(host) as? JSONObject ?: return null
                hostSync[host] = readHostSync(entry) ?: return null
            }
        }
        return RoutineStoreDocument(
            schemaVersion = set.schemaVersion,
            pausedAll = pausedAll.value ?: false,
            routines = set.routines ?: emptyList(),
            homeJson = home,
            hostSync = hostSync,
            storeResetAtUnixMs = reset,
            unknownFieldsJson = unknownFields(obj, KNOWN_KEYS),
        )
    }

    private fun readHostSync(entry: JSONObject): RoutineHostSync? {
        val local = optLong(entry, "localRevision") ?: return null
        val acked = optLong(entry, "ackedRevision") ?: return null
        val cursor = optLong(entry, "runCursor") ?: return null
        val away = optLong(entry, "reachableAwayAtUnixMs") ?: return null
        val last = optTyped<String>(entry, "lastResultJson") ?: return null
        val asOf = optLong(entry, "historyAsOfUnixMs") ?: return null
        return RoutineHostSync(
            localRevision = local.value ?: 0,
            ackedRevision = acked.value ?: 0,
            lastResultJson = last.value,
            runCursor = cursor.value ?: 0,
            reachableAwayAtUnixMs = away.value,
            historyAsOfUnixMs = asOf.value,
            unknownFieldsJson = unknownFields(entry, KNOWN_HOST_KEYS),
        )
    }

    /** A present-or-absent value; null itself means "present with the wrong JSON type". */
    private class Field<T>(val value: T?)

    private inline fun <reified T> optTyped(obj: JSONObject, key: String): Field<T>? {
        if (!obj.has(key) || obj.isNull(key)) return Field(null)
        return (obj.opt(key) as? T)?.let { Field(it) }
    }

    private fun optLong(obj: JSONObject, key: String): Field<Long>? {
        if (!obj.has(key) || obj.isNull(key)) return Field(null)
        return when (val v = obj.opt(key)) {
            is Int -> Field(v.toLong())
            is Long -> Field(v)
            else -> null
        }
    }

    private fun unknownFields(obj: JSONObject, known: Set<String>): String? {
        val extra = JSONObject()
        for (key in obj.keys()) if (key !in known) extra.put(key, obj.opt(key))
        return if (extra.length() == 0) null else extra.toString()
    }

    private fun objectOrNull(text: String): JSONObject? =
        runCatching { JSONTokener(text).nextValue() as? JSONObject }.getOrNull()
}
