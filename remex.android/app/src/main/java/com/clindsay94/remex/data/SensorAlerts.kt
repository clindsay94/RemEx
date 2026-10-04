package com.clindsay94.remex.data

import java.util.Locale
import org.json.JSONObject

/** Whether the reading must rise above or fall below the threshold. Wire names are the PC's, verbatim. */
enum class SensorAlertDirection(val wire: String) {
        ABOVE("Above"),
        BELOW("Below");

        companion object {
                fun fromWire(value: String?): SensorAlertDirection? = entries.firstOrNull { it.wire.equals(value, ignoreCase = true) }
        }
}

/** How urgently a firing is flagged. Wire names are the PC's, verbatim. */
enum class SensorAlertSeverity(val wire: String) {
        WARNING("Warning"),
        CRITICAL("Critical");

        companion object {
                fun fromWire(value: String?): SensorAlertSeverity? = entries.firstOrNull { it.wire.equals(value, ignoreCase = true) }
        }
}

/** One of the PC's alert rules, as the phone shows it. [currentValue] is the PC's latest reading, when it has one. */
data class SensorAlertRule(
        val sensorName: String,
        val displayName: String,
        val unit: String?,
        val currentValue: Double?,
        val threshold: Double,
        val direction: SensorAlertDirection,
        val severity: SensorAlertSeverity
)

/** The PC's whole rule list, from one `sensor_alert_rules`. */
data class SensorAlertRulesMessage(val rules: List<SensorAlertRule>, val revision: Long)

/** One `sensor_alert_fired` from the PC: a rule just crossed its threshold there. */
data class SensorAlertFired(
        val sensorName: String,
        val displayName: String,
        val value: Double,
        val unit: String?,
        val threshold: Double,
        val direction: SensorAlertDirection,
        val severity: SensorAlertSeverity,
        val firedAtUtc: String?
)

/**
 * The phone half of the phone telemetry alerts contract (docs/API_CONTRACTS.md section 10; RemEx-pp4cm.12).
 *
 * Pure JVM, like [HomePins]: no Android types, so the envelopes, the parsers and the rules are provable
 * off-device. **THE PC OWNS EVERY RULE AND EVALUATES THEM; THE PHONE NEVER COMPARES A READING WITH A
 * THRESHOLD.** It shows what [parseRules] returns, asks for one change at a time, and hears the result
 * as the next `sensor_alert_rules`.
 *
 * The name and threshold rules mirror `remex.core/Validation/SensorAlertValidation.cs`: a name is
 * non-blank, at most [MaxNameLength] characters and holds no control character; a threshold is a finite
 * number within [MaxThresholdMagnitude]; the PC holds at most [MaxRules]. The PC refuses anything else,
 * so the phone must never send it.
 */
object SensorAlerts {

        const val FiredType = "sensor_alert_fired"
        const val RulesType = "sensor_alert_rules"
        const val GetType = "sensor_alerts_get"
        const val SetType = "sensor_alert_set"
        const val RemoveType = "sensor_alert_remove"

        /** Every host -> phone type starts with this; the native router forwards the whole prefix. */
        const val TypePrefix = "sensor_alert_"

        /** The `RemexMessage` protocol version every control envelope carries; unchanged by this feature. */
        const val ProtocolVersion = 2

        const val MaxNameLength = 200
        const val MaxRules = 100
        const val MaxThresholdMagnitude = 1e12

        /** The `host_info` flag a PC that mirrors its alerts sets. Absent means an older PC. */
        const val CapabilityField = "supportsSensorAlerts"

        fun isValidSensorName(name: String?): Boolean = HomePins.isValidSensorName(name)

        fun isValidThreshold(value: Double): Boolean = value.isFinite() && Math.abs(value) <= MaxThresholdMagnitude

        /** Whether a rule may be sent: a usable name and a threshold the PC would accept. */
        fun isSendable(sensorName: String, threshold: Double): Boolean =
                isValidSensorName(sensorName) && isValidThreshold(threshold)

        /** `{"type":"sensor_alerts_get","protocolVersion":2}`. */
        fun buildGetEnvelope(): String =
                JSONObject().put("type", GetType).put("protocolVersion", ProtocolVersion).toString()

        /**
         * `{"type":"sensor_alert_set","protocolVersion":2,"sensorAlertChange":{...}}`, or null for a rule
         * the PC would refuse anyway (a blank name, a threshold that is not a usable number).
         */
        fun buildSetEnvelope(
                sensorName: String,
                threshold: Double,
                direction: SensorAlertDirection,
                severity: SensorAlertSeverity
        ): String? {
                if (!isSendable(sensorName, threshold)) return null
                val change =
                        JSONObject()
                                .put("sensorName", sensorName)
                                .put("threshold", threshold)
                                .put("direction", direction.wire)
                                .put("severity", severity.wire)
                return JSONObject()
                        .put("type", SetType)
                        .put("protocolVersion", ProtocolVersion)
                        .put("sensorAlertChange", change)
                        .toString()
        }

        /** `{"type":"sensor_alert_remove",...,"sensorAlertRemoval":{"sensorName":..}}`, or null for an unusable name. */
        fun buildRemoveEnvelope(sensorName: String): String? {
                if (!isValidSensorName(sensorName)) return null
                return JSONObject()
                        .put("type", RemoveType)
                        .put("protocolVersion", ProtocolVersion)
                        .put("sensorAlertRemoval", JSONObject().put("sensorName", sensorName))
                        .toString()
        }

        /**
         * Reads a whole `sensor_alert_rules` envelope as the native router forwards it. Null for anything
         * that is not that type with a `sensorAlertRules` object, or is not JSON. A rule the PC itself would
         * not send (bad name, threshold or enum) is dropped; duplicates (case-insensitive) keep the first.
         * Never throws: this runs on every message from the PC.
         */
        fun parseRules(json: String?): SensorAlertRulesMessage? {
                if (json.isNullOrBlank()) return null
                return runCatching {
                        val envelope = JSONObject(json)
                        if (envelope.optString("type") != RulesType) return@runCatching null
                        val payload = envelope.optJSONObject("sensorAlertRules") ?: return@runCatching null
                        val array = payload.optJSONArray("rules")
                        val seen = HashSet<String>()
                        val rules = ArrayList<SensorAlertRule>()
                        if (array != null) {
                                for (index in 0 until array.length()) {
                                        if (rules.size == MaxRules) break
                                        val rule = parseRule(array.optJSONObject(index)) ?: continue
                                        if (seen.add(rule.sensorName.uppercase(Locale.ROOT))) rules += rule
                                }
                        }
                        SensorAlertRulesMessage(rules, payload.optLong("revision", 0L))
                }.getOrNull()
        }

        /** Reads a whole `sensor_alert_fired` envelope. Null for anything else, or a firing the PC could not have sent. */
        fun parseFired(json: String?): SensorAlertFired? {
                if (json.isNullOrBlank()) return null
                return runCatching {
                        val envelope = JSONObject(json)
                        if (envelope.optString("type") != FiredType) return@runCatching null
                        val payload = envelope.optJSONObject("sensorAlertFired") ?: return@runCatching null
                        val name = payload.optString("sensorName").takeIf { isValidSensorName(it) } ?: return@runCatching null
                        val value = payload.opt("value") as? Number ?: return@runCatching null
                        val threshold = payload.opt("threshold") as? Number ?: return@runCatching null
                        if (!value.toDouble().isFinite() || !threshold.toDouble().isFinite()) return@runCatching null
                        SensorAlertFired(
                                sensorName = name,
                                displayName = optText(payload, "displayName") ?: name,
                                value = value.toDouble(),
                                unit = optText(payload, "unit"),
                                threshold = threshold.toDouble(),
                                direction = SensorAlertDirection.fromWire(payload.optString("direction")) ?: return@runCatching null,
                                severity = SensorAlertSeverity.fromWire(payload.optString("severity")) ?: return@runCatching null,
                                firedAtUtc = optText(payload, "firedAtUtc")
                        )
                }.getOrNull()
        }

        /** Whether a `host_info` says the PC mirrors its alerts. False when absent, not a boolean, or not JSON. */
        fun parseSupports(hostInfoJson: String?): Boolean {
                if (hostInfoJson.isNullOrBlank()) return false
                return runCatching { JSONObject(hostInfoJson).opt(CapabilityField) == true }.getOrDefault(false)
        }

        /**
         * A number as the alert screens and notifications write it: one decimal at most, none for a whole
         * number, and the unit tight against a percent sign but spaced from anything else (`92.4 °C`, `85%`).
         * Uses [locale]'s decimal separator.
         */
        fun formatValue(value: Double, unit: String?, locale: Locale = Locale.getDefault()): String {
                val number =
                        if (value == Math.rint(value) && Math.abs(value) < 1e9) String.format(locale, "%d", value.toLong())
                        else String.format(locale, "%.1f", value)
                val suffix = unit?.trim().orEmpty()
                return when {
                        suffix.isEmpty() -> number
                        suffix == "%" -> "$number%"
                        else -> "$number $suffix"
                }
        }

        /**
         * Parses what someone typed into the threshold box: a plain decimal with either `.` or `,` as the
         * separator, no grouping. Null for blank text, anything that is not a number, or a number the PC
         * would refuse.
         */
        fun parseThreshold(text: String): Double? {
                val cleaned = text.trim().replace(',', '.')
                if (cleaned.isEmpty() || cleaned.count { it == '.' } > 1) return null
                if (!cleaned.all { it.isDigit() || it == '.' || it == '-' }) return null
                if (cleaned.lastIndexOf('-') > 0) return null
                val value = cleaned.toDoubleOrNull() ?: return null
                return value.takeIf(::isValidThreshold)
        }

        private fun parseRule(obj: JSONObject?): SensorAlertRule? {
                if (obj == null) return null
                val name = obj.optString("sensorName").takeIf { isValidSensorName(it) } ?: return null
                val threshold = (obj.opt("threshold") as? Number)?.toDouble()?.takeIf(::isValidThreshold) ?: return null
                return SensorAlertRule(
                        sensorName = name,
                        displayName = optText(obj, "displayName") ?: name,
                        unit = optText(obj, "unit"),
                        currentValue = (obj.opt("currentValue") as? Number)?.toDouble()?.takeIf { it.isFinite() },
                        threshold = threshold,
                        direction = SensorAlertDirection.fromWire(obj.optString("direction")) ?: return null,
                        severity = SensorAlertSeverity.fromWire(obj.optString("severity")) ?: return null
                )
        }

        /** A string field, or null when absent, JSON null (which `optString` would turn into "null"), or blank. */
        private fun optText(obj: JSONObject, key: String): String? =
                (obj.opt(key) as? String)?.takeIf { it.isNotBlank() }
}
