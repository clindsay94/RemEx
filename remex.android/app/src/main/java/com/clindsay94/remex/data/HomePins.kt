package com.clindsay94.remex.data

import java.util.Locale
import org.json.JSONArray
import org.json.JSONObject

/** Where the phone's copy of a PC's pinned-sensor list came from (RemEx-wqo7a.6). */
enum class HomePinsSource(val wire: String) {
        /** The PC sent it in a `home_pins_sync`: the PC owns the list. */
        HOST("host"),

        /** The phone made it itself, for a PC too old to keep the list (no `supportsHomePinsSync`). */
        LOCAL("local");

        companion object {
                fun fromWire(value: String?): HomePinsSource = entries.firstOrNull { it.wire == value } ?: LOCAL
        }
}

/** One `home_pins_sync` from the PC, already normalised by [HomePins.parseSync]. */
data class HomePinsSyncMessage(
        val sensorNames: List<String>,
        val pinnableSensorNames: List<String>,
        val revision: Long
)

/** What the phone remembers per PC between connections: `{names, pinnable, source}`. */
data class HomePinsCache(
        val names: List<String>,
        val pinnable: List<String>,
        val source: HomePinsSource
)

/**
 * The phone half of the Home pinned-sensors contract (docs/API_CONTRACTS.md section 9; RemEx-wqo7a.5).
 *
 * Pure JVM, like [ThemeSync]: no Android types, so the envelope, the parser and the name rules are
 * provable off-device. Sensor NAMES are the key (the PC's `SensorReading.Name`, the phone's
 * `TelemetrySensor.name`), compared case-insensitively, because the phone's sensor ids are slugs the
 * PC has never heard of.
 *
 * The name rules mirror `remex.core/Validation/HomePinsValidation.cs` exactly: a name is non-blank, at
 * most [MaxNameLength] characters and holds no control character; a list holds at most [MaxNames],
 * duplicates removed case-insensitively keeping the first. The PC drops anything else, so the phone
 * must never send it and never trust it.
 */
object HomePins {

        const val SyncType = "home_pins_sync"
        const val ChangeType = "home_pins_change"

        /** The `RemexMessage` protocol version every control envelope carries; unchanged by this feature. */
        const val ProtocolVersion = 2

        const val MaxNameLength = 200
        const val MaxNames = 100

        /** The `host_info` flag a PC that keeps the list sets. Absent means an older PC. */
        const val CapabilityField = "supportsHomePinsSync"

        /**
         * Preference key for one PC's cache: `homePins_<identity>`, keyed by
         * [com.clindsay94.remex.security.HostIdentity] like the `knownHost_` keys, never by address.
         * Its own prefix rather than a `knownHost_` field, so [KnownHosts.parseRecords] and the
         * known-host migration never see it. Not in `SettingsExport.ExportableKeys`: which sensors
         * someone watches on which PC is not a setting to carry to another phone.
         */
        const val CacheKeyPrefix = "homePins_"

        fun cacheKeyName(identity: String): String = "$CacheKeyPrefix$identity"

        fun isValidSensorName(name: String?): Boolean =
                !name.isNullOrBlank() && name.length <= MaxNameLength && name.none { it.isISOControl() }

        /** The PC's `NormalizeNames`: valid names only, case-insensitive dedupe keeping the first, capped. */
        fun normalizeNames(names: List<String?>?): List<String> {
                if (names == null) return emptyList()
                val seen = HashSet<String>()
                val result = ArrayList<String>()
                for (name in names) {
                        if (result.size == MaxNames) break
                        if (isValidSensorName(name) && seen.add(foldKey(name!!))) result += name
                }
                return result
        }

        fun containsName(names: List<String>, name: String): Boolean = names.any { it.equals(name, ignoreCase = true) }

        /**
         * [names] with [name] pinned (appended when absent) or unpinned (every case-insensitive match
         * removed). Appending keeps the user's order: the newest pin goes last, as on the PC.
         */
        fun withPin(names: List<String>, name: String, pinned: Boolean): List<String> =
                if (pinned) {
                        if (containsName(names, name)) names else normalizeNames(names + name)
                } else {
                        names.filterNot { it.equals(name, ignoreCase = true) }
                }

        /**
         * `{"type":"home_pins_change","protocolVersion":2,"homePinChange":{"sensorName":..,"pinned":..}}`,
         * or null for a name the PC would drop anyway. ONE sensor per message, never a whole list: the
         * PC applies changes in arrival order, which is what lets two edits to different sensors from
         * two places both survive.
         */
        fun buildChangeEnvelope(sensorName: String, pinned: Boolean): String? {
                if (!isValidSensorName(sensorName)) return null
                val change = JSONObject().put("sensorName", sensorName).put("pinned", pinned)
                return JSONObject()
                        .put("type", ChangeType)
                        .put("protocolVersion", ProtocolVersion)
                        .put("homePinChange", change)
                        .toString()
        }

        /**
         * Reads a whole `home_pins_*` envelope as the native router forwards it. Null for anything that
         * is not a `home_pins_sync` with a `homePins` object, or is not JSON. Lenient inside the payload,
         * like the PC's converter: an absent or wrong-typed list reads as empty, a missing revision as 0.
         * Never throws: this runs on every message from the PC.
         */
        fun parseSync(json: String?): HomePinsSyncMessage? {
                if (json.isNullOrBlank()) return null
                return runCatching {
                        val envelope = JSONObject(json)
                        if (envelope.optString("type") != SyncType) return@runCatching null
                        val payload = envelope.optJSONObject("homePins") ?: return@runCatching null
                        HomePinsSyncMessage(
                                sensorNames = normalizeNames(stringList(payload.optJSONArray("sensorNames"))),
                                pinnableSensorNames = normalizeNames(stringList(payload.optJSONArray("pinnableSensorNames"))),
                                revision = payload.optLong("revision", 0L)
                        )
                }.getOrNull()
        }

        /** Whether a `host_info` says the PC keeps the list. False when absent, not a boolean, or not JSON. */
        fun parseSupportsSync(hostInfoJson: String?): Boolean {
                if (hostInfoJson.isNullOrBlank()) return false
                return runCatching { JSONObject(hostInfoJson).opt(CapabilityField) == true }.getOrDefault(false)
        }

        fun encodeCache(cache: HomePinsCache): String =
                JSONObject()
                        .put("names", JSONArray(cache.names))
                        .put("pinnable", JSONArray(cache.pinnable))
                        .put("source", cache.source.wire)
                        .toString()

        /** Null for a missing or unreadable cache; a stored list is re-normalised, never trusted. */
        fun decodeCache(json: String?): HomePinsCache? {
                if (json.isNullOrBlank()) return null
                return runCatching {
                        val obj = JSONObject(json)
                        HomePinsCache(
                                names = normalizeNames(stringList(obj.optJSONArray("names"))),
                                pinnable = normalizeNames(stringList(obj.optJSONArray("pinnable"))),
                                source = HomePinsSource.fromWire(obj.optString("source"))
                        )
                }.getOrNull()
        }

        private fun stringList(array: JSONArray?): List<String?> {
                if (array == null) return emptyList()
                return (0 until array.length()).map { index -> array.opt(index) as? String }
        }

        /** OrdinalIgnoreCase's key: the invariant upper-case form. */
        private fun foldKey(name: String): String = name.uppercase(Locale.ROOT)
}
