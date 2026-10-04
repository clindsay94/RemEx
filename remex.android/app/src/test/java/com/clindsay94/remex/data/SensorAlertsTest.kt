package com.clindsay94.remex.data

import java.util.Locale
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The phone half of the phone-alerts wire contract (RemEx-pp4cm.12, docs/API_CONTRACTS.md section 10):
 * what the phone sends is exactly what the PC's validator accepts, and what it reads is lenient the way
 * the PC's converters are, never throwing on a malformed message.
 */
class SensorAlertsTest {

    private fun rules(vararg rule: JSONObject, revision: Long = 3): String =
            JSONObject()
                    .put("type", "sensor_alert_rules")
                    .put("sensorAlertRules", JSONObject().put("rules", JSONArray(rule.toList())).put("revision", revision))
                    .toString()

    private fun rule(name: String = "CPU Package", threshold: Any = 90, direction: String = "Above", severity: String = "Critical") =
            JSONObject()
                    .put("sensorName", name)
                    .put("displayName", "CPU temperature")
                    .put("unit", "°C")
                    .put("currentValue", 61.5)
                    .put("threshold", threshold)
                    .put("direction", direction)
                    .put("severity", severity)

    // ── What the phone sends ──

    @Test
    fun `a set carries the fields the PC reads under the slot it reads`() {
        val envelope = JSONObject(SensorAlerts.buildSetEnvelope("CPU Package", 90.5, SensorAlertDirection.BELOW, SensorAlertSeverity.CRITICAL)!!)

        assertEquals("sensor_alert_set", envelope.getString("type"))
        assertEquals(2, envelope.getInt("protocolVersion"))
        val change = envelope.getJSONObject("sensorAlertChange")
        assertEquals("CPU Package", change.getString("sensorName"))
        assertEquals(90.5, change.getDouble("threshold"), 0.0)
        assertEquals("Below", change.getString("direction"))
        assertEquals("Critical", change.getString("severity"))
    }

    @Test
    fun `a remove and a get have the shapes the PC reads`() {
        val remove = JSONObject(SensorAlerts.buildRemoveEnvelope("GPU Temp")!!)
        assertEquals("sensor_alert_remove", remove.getString("type"))
        assertEquals("GPU Temp", remove.getJSONObject("sensorAlertRemoval").getString("sensorName"))

        val get = JSONObject(SensorAlerts.buildGetEnvelope())
        assertEquals("sensor_alerts_get", get.getString("type"))
        assertEquals(2, get.getInt("protocolVersion"))
    }

    @Test
    fun `a rule the PC would refuse is never built`() {
        val above = SensorAlertDirection.ABOVE
        val warning = SensorAlertSeverity.WARNING
        assertNull(SensorAlerts.buildSetEnvelope("   ", 90.0, above, warning))
        assertNull(SensorAlerts.buildSetEnvelope("CPU\u0007", 90.0, above, warning))
        assertNull(SensorAlerts.buildSetEnvelope("x".repeat(201), 90.0, above, warning))
        assertNull(SensorAlerts.buildSetEnvelope("CPU", Double.NaN, above, warning))
        assertNull(SensorAlerts.buildSetEnvelope("CPU", Double.POSITIVE_INFINITY, above, warning))
        assertNull(SensorAlerts.buildSetEnvelope("CPU", 1e13, above, warning))
        assertNull(SensorAlerts.buildRemoveEnvelope(""))
        assertNotNull(SensorAlerts.buildSetEnvelope("CPU", -40.0, above, warning))
    }

    // ── What the phone reads ──

    @Test
    fun `a rule list is read with every field`() {
        val message = SensorAlerts.parseRules(rules(rule(), rule(name = "Fan 1", threshold = 300, direction = "Below", severity = "Warning"), revision = 9))!!

        assertEquals(9L, message.revision)
        assertEquals(2, message.rules.size)
        val first = message.rules[0]
        assertEquals("CPU Package", first.sensorName)
        assertEquals("CPU temperature", first.displayName)
        assertEquals("°C", first.unit)
        assertEquals(61.5, first.currentValue!!, 0.0)
        assertEquals(90.0, first.threshold, 0.0)
        assertEquals(SensorAlertDirection.ABOVE, first.direction)
        assertEquals(SensorAlertSeverity.CRITICAL, first.severity)
        assertEquals(SensorAlertDirection.BELOW, message.rules[1].direction)
        assertEquals(SensorAlertSeverity.WARNING, message.rules[1].severity)
    }

    @Test
    fun `a rule the PC could not have sent is dropped and the rest are kept`() {
        val noUnit = rule(name = "Fan 2").apply { remove("unit"); remove("currentValue"); remove("displayName") }
        val message =
                SensorAlerts.parseRules(
                        rules(
                                rule(name = "  "),
                                rule(name = "Bad", direction = "Sideways"),
                                rule(name = "Worse", severity = "Panic"),
                                rule(name = "Text", threshold = "hot"),
                                rule(name = "cpu package"),
                                rule(name = "CPU PACKAGE", threshold = 5),
                                noUnit
                        )
                )!!

        assertEquals(listOf("cpu package", "Fan 2"), message.rules.map { it.sensorName })
        val fan = message.rules[1]
        assertNull("an absent unit is absent, not the word null", fan.unit)
        assertNull(fan.currentValue)
        assertEquals("a missing display name falls back to the sensor name", "Fan 2", fan.displayName)
    }

    @Test
    fun `an explicit JSON null unit is absent and not the string null`() {
        val withNull = rule().put("unit", JSONObject.NULL).put("displayName", JSONObject.NULL)
        val read = SensorAlerts.parseRules(rules(withNull))!!.rules.single()

        assertNull(read.unit)
        assertEquals("CPU Package", read.displayName)
    }

    @Test
    fun `a fired alert is read with every field`() {
        val json =
                JSONObject()
                        .put("type", "sensor_alert_fired")
                        .put(
                                "sensorAlertFired",
                                JSONObject()
                                        .put("sensorName", "CPU Package")
                                        .put("displayName", "CPU temperature")
                                        .put("value", 92.4)
                                        .put("unit", "°C")
                                        .put("threshold", 90)
                                        .put("direction", "Above")
                                        .put("severity", "Critical")
                                        .put("firedAtUtc", "2026-10-04T09:30:00+00:00")
                        )
                        .toString()

        val fired = SensorAlerts.parseFired(json)!!

        assertEquals("CPU Package", fired.sensorName)
        assertEquals("CPU temperature", fired.displayName)
        assertEquals(92.4, fired.value, 0.0)
        assertEquals("°C", fired.unit)
        assertEquals(90.0, fired.threshold, 0.0)
        assertEquals(SensorAlertDirection.ABOVE, fired.direction)
        assertEquals(SensorAlertSeverity.CRITICAL, fired.severity)
        assertEquals("2026-10-04T09:30:00+00:00", fired.firedAtUtc)
    }

    @Test
    fun `parsers never throw and return null for anything that is not theirs`() {
        for (junk in listOf(null, "", "   ", "not json", "[]", "{}", """{"type":"home_pins_sync"}""", """{"type":"sensor_alert_rules"}""",
                """{"type":"sensor_alert_rules","sensorAlertRules":"x"}""", """{"type":"sensor_alert_fired","sensorAlertFired":{"sensorName":"CPU"}}""",
                """{"type":"sensor_alert_fired","sensorAlertFired":{"sensorName":"CPU","value":"hot","threshold":1,"direction":"Above","severity":"Critical"}}""")) {
            assertNull("fired from $junk", SensorAlerts.parseFired(junk))
            if (junk?.contains("sensor_alert_rules") != true) assertNull("rules from $junk", SensorAlerts.parseRules(junk))
        }
        // A rules envelope with no list is an empty list, not a failure: the PC then holds none.
        assertEquals(emptyList<SensorAlertRule>(), SensorAlerts.parseRules("""{"type":"sensor_alert_rules","sensorAlertRules":{"revision":2}}""")!!.rules)
    }

    @Test
    fun `a fired alert with an unusable value or enum is refused`() {
        fun fired(value: Any, direction: String = "Above", severity: String = "Critical") =
                JSONObject()
                        .put("type", "sensor_alert_fired")
                        .put(
                                "sensorAlertFired",
                                JSONObject().put("sensorName", "CPU").put("value", value).put("threshold", 1).put("direction", direction).put("severity", severity)
                        )
                        .toString()

        assertNotNull(SensorAlerts.parseFired(fired(5)))
        assertNull(SensorAlerts.parseFired(fired(5, direction = "Sideways")))
        assertNull(SensorAlerts.parseFired(fired(5, severity = "Panic")))
        assertNull(SensorAlerts.parseFired(fired("5")))
    }

    @Test
    fun `host info support is read as a real boolean`() {
        assertTrue(SensorAlerts.parseSupports("""{"supportsSensorAlerts":true}"""))
        assertFalse(SensorAlerts.parseSupports("""{"supportsSensorAlerts":false}"""))
        assertFalse(SensorAlerts.parseSupports("""{"supportsSensorAlerts":"true"}"""))
        assertFalse(SensorAlerts.parseSupports("""{}"""))
        assertFalse(SensorAlerts.parseSupports(null))
        assertFalse(SensorAlerts.parseSupports("garbage"))
    }

    // ── Numbers ──

    @Test
    fun `a value is written with at most one decimal and the unit tight to a percent sign`() {
        assertEquals("92.4 °C", SensorAlerts.formatValue(92.4, "°C", Locale.US))
        assertEquals("90 °C", SensorAlerts.formatValue(90.0, "°C", Locale.US))
        assertEquals("85%", SensorAlerts.formatValue(85.0, "%", Locale.US))
        assertEquals("1200 RPM", SensorAlerts.formatValue(1200.0, "RPM", Locale.US))
        assertEquals("72", SensorAlerts.formatValue(72.0, null, Locale.US))
        assertEquals("72", SensorAlerts.formatValue(72.0, "  ", Locale.US))
        assertEquals("92,4 °C", SensorAlerts.formatValue(92.4, "°C", Locale.FRANCE))
    }

    @Test
    fun `a typed threshold is read with either separator and refused when it is not a plain number`() {
        assertEquals(85.0, SensorAlerts.parseThreshold("85")!!, 0.0)
        assertEquals(72.5, SensorAlerts.parseThreshold("72.5")!!, 0.0)
        assertEquals(72.5, SensorAlerts.parseThreshold(" 72,5 ")!!, 0.0)
        assertEquals(-40.0, SensorAlerts.parseThreshold("-40")!!, 0.0)
        assertNull(SensorAlerts.parseThreshold(""))
        assertNull(SensorAlerts.parseThreshold("   "))
        assertNull(SensorAlerts.parseThreshold("hot"))
        assertNull(SensorAlerts.parseThreshold("1,234.5"))
        assertNull(SensorAlerts.parseThreshold("7-5"))
        assertNull(SensorAlerts.parseThreshold("1e3"))
        assertNull(SensorAlerts.parseThreshold("."))
        assertNull(SensorAlerts.parseThreshold("9999999999999"))
    }

    @Test
    fun `the wire names are the PCs names verbatim`() {
        assertEquals(listOf("Above", "Below"), SensorAlertDirection.entries.map { it.wire })
        assertEquals(listOf("Warning", "Critical"), SensorAlertSeverity.entries.map { it.wire })
        assertEquals("sensor_alert_", SensorAlerts.TypePrefix)
        assertTrue(SensorAlerts.FiredType.startsWith(SensorAlerts.TypePrefix))
        assertTrue(SensorAlerts.RulesType.startsWith(SensorAlerts.TypePrefix))
    }
}
