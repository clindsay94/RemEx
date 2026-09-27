package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineJson
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import com.clindsay94.remex.routines.model.RoutineSchema
import com.clindsay94.remex.routines.model.RoutineStepTypes
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import com.clindsay94.remex.routines.model.RoutineValidationContext
import com.clindsay94.remex.routines.model.RoutineValidator
import com.clindsay94.remex.routines.model.RoutineVerdict
import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject

/**
 * The `.remexroutines` export file (routines spec §1.9, §6.10; RemEx-pp0rt.11).
 *
 * **EXPORT IS A WHITELIST (T13).** Each object is rebuilt key by key from [ROUTINE_KEYS],
 * [TRIGGER_KEYS] and [STEP_KEYS], so a field added to the model later stays out of the file until
 * someone decides it belongs there. Never exported: the target PC's `hostIdentity`, the `homeId`,
 * a wake step's `mac` and `broadcastIp`, `enabled`, revisions and timestamps; NFC tag tokens, home
 * network facts and keys are not in a [Routine] at all. `RoutineExportWhitelistTest` pins this.
 *
 * **IMPORT RE-BINDS EVERYTHING.** A new id, the PC the user picked, that PC's MAC, the phone's own home
 * (or none), switched off. Whatever a hand-edited file carries for those fields is discarded.
 */
object RoutineExchange {
    const val FORMAT = "remex.routines"
    const val FORMAT_VERSION = 1
    const val EXTENSION = "remexroutines"

    /** The file name the create-document picker suggests. */
    const val SUGGESTED_NAME = "routines.$EXTENSION"

    val ROUTINE_KEYS: Set<String> = setOf("id", "name", "appearance", "trigger", "steps")
    val APPEARANCE_KEYS: Set<String> = setOf("icon", "color")
    val TRIGGER_KEYS: Set<String> =
        setOf(
            "type", "leaveDebounceSeconds", "sensorId", "sensorLabel", "direction", "threshold",
            "sustainSeconds", "idleMinutes", "ignoreWhileMediaPlaying", "sessionState",
        )
    val STEP_KEYS: Set<String> =
        setOf(
            "type", "port", "timeoutSeconds", "seconds", "verb", "delaySeconds", "appId", "appLabel",
            "mediaAction", "target", "title", "body",
        )

    /** The whole file: every well-formed routine, in list order, pretty-printed. */
    fun export(routines: List<Routine>, exportedAtUnixMs: Long, exportedBy: String): String {
        val list = JSONArray()
        routines.filter { !it.isMalformed }.forEach { list.put(exportRoutine(it)) }
        return JSONObject()
            .put("format", FORMAT)
            .put("formatVersion", FORMAT_VERSION)
            .put("schemaVersion", RoutineSchema.CURRENT_VERSION)
            .put("exportedAtUnixMs", exportedAtUnixMs)
            .put("exportedBy", exportedBy)
            .put("routines", list)
            .toString(2)
    }

    /** One routine, whitelisted. */
    fun exportRoutine(routine: Routine): JSONObject {
        val full = RoutineJson.write(routine)
        val out = pick(full, ROUTINE_KEYS - setOf("appearance", "trigger", "steps"))
        (full.opt("appearance") as? JSONObject)?.let { out.put("appearance", pick(it, APPEARANCE_KEYS)) }
        (full.opt("trigger") as? JSONObject)?.let { out.put("trigger", pick(it, TRIGGER_KEYS)) }
        (full.opt("steps") as? JSONArray)?.let { steps ->
            val clean = JSONArray()
            for (i in 0 until steps.length()) {
                val step = steps.opt(i) as? JSONObject
                clean.put(if (step == null) JSONObject.NULL else pick(step, STEP_KEYS))
            }
            out.put("steps", clean)
        }
        return out
    }

    private fun pick(source: JSONObject, keys: Set<String>): JSONObject {
        val out = JSONObject()
        for (key in keys) if (source.has(key)) out.put(key, source.get(key))
        return out
    }

    /** What reading a file produced. */
    sealed interface ReadResult {
        /** Every element of the file's list, in order; an unreadable one is [Routine.isMalformed]. */
        data class Ok(val routines: List<Routine>) : ReadResult

        /** Not JSON, not an object, not this format, or no routine list. */
        data object Unreadable : ReadResult

        /** A `formatVersion` newer than [FORMAT_VERSION]. */
        data object TooNew : ReadResult
    }

    /** Never throws: a bad file is [ReadResult.Unreadable], not a crash (§1.9). */
    fun read(text: String?): ReadResult {
        val obj =
            try {
                JSONObject(text ?: return ReadResult.Unreadable)
            } catch (e: JSONException) {
                return ReadResult.Unreadable
            }
        if (obj.optString("format") != FORMAT) return ReadResult.Unreadable
        val version = obj.opt("formatVersion") as? Number ?: return ReadResult.Unreadable
        if (version.toDouble() != Math.floor(version.toDouble()) || version.toLong() < 1) return ReadResult.Unreadable
        if (version.toLong() > FORMAT_VERSION) return ReadResult.TooNew
        val array = obj.opt("routines") as? JSONArray ?: return ReadResult.Unreadable
        return ReadResult.Ok((0 until array.length()).map { RoutineJson.readRoutine(array.opt(it)) })
    }

    /**
     * [imported] ready to store (§6.10 "On import"): [newId], bound to [hostIdentity], wake steps
     * carrying that PC's [mac], a home trigger bound to the phone's [homeId] (null when it has none),
     * switched off. Nothing the file said about any of those survives.
     */
    fun prepare(imported: Routine, newId: String, hostIdentity: String?, mac: String?, homeId: String?): Routine =
        Routine(
            id = newId,
            name = imported.name,
            hostIdentity = hostIdentity,
            enabled = false,
            appearance = imported.appearance,
            trigger =
                imported.trigger?.let { t ->
                    t.copy(homeId = if (t.type == RoutineTriggerTypes.HOME_ARRIVE || t.type == RoutineTriggerTypes.HOME_LEAVE) homeId else null)
                },
            steps =
                imported.steps?.map { step ->
                    step?.let { if (it.type == RoutineStepTypes.WAKE) it.copy(mac = mac, broadcastIp = null) else it.copy(mac = null, broadcastIp = null) }
                },
        )

    /** One routine of a file as the review sheet shows it. */
    data class Review(
        /** Position in the file. */
        val index: Int,
        /** Null for an element that could not be read at all. */
        val prepared: Routine?,
        /** The full validator's verdict on [prepared]; import skips an invalid one (§6.10). */
        val verdict: RoutineVerdict,
        /** An NFC routine: its tag must be written again after importing (the file has no tag). */
        val needsTag: Boolean,
        /** A home routine and this phone has no home yet. */
        val needsHome: Boolean,
        /** Another stored routine already has this name; both are kept. */
        val nameTaken: Boolean,
    ) {
        val importable: Boolean get() = prepared != null && verdict.isValid
    }

    /** Reviews every routine of a read file against the chosen PC, its MAC and the phone's home. */
    fun review(
        routines: List<Routine>,
        hostIdentity: String?,
        mac: String?,
        homeId: String?,
        existingNames: Collection<String>,
        newId: () -> String,
    ): List<Review> {
        val names = existingNames.map { it.trim().lowercase() }.toSet()
        val context = RoutineValidationContext(knownHomeIds = listOfNotNull(homeId))
        return routines.mapIndexed { i, routine ->
            if (routine.isMalformed) {
                Review(i, null, RoutineVerdict(RoutineReasonCodes.INVALID_FIELD, null), false, false, false)
            } else {
                val prepared = prepare(routine, newId(), hostIdentity, mac, homeId)
                val type = prepared.trigger?.type
                Review(
                    index = i,
                    prepared = prepared,
                    // The store assigns revision and timestamps on save; validate as it will.
                    verdict = RoutineValidator.validateRoutine(prepared.copy(revision = 1), context),
                    needsTag = type == RoutineTriggerTypes.NFC_TAP,
                    needsHome = (type == RoutineTriggerTypes.HOME_ARRIVE || type == RoutineTriggerTypes.HOME_LEAVE) && homeId == null,
                    nameTaken = prepared.name?.trim()?.lowercase()?.let { it in names } == true,
                )
            }
        }
    }
}
