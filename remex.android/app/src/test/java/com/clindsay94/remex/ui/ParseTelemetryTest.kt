package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.parseTelemetry
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The CPU, GPU and RAM headline percentages on the Home screen (RemEx-cite).
 *
 * **CHARACTERISATION TESTS WRITTEN TO MAKE A REFACTOR SAFE, NOT TO FIX A BUG.** `cite` item 2 folded
 * three separate scans of the telemetry array into the one pass that already built the card list.
 * Nothing here had any coverage, so that fold would have been an unwitnessed rewrite of the three
 * numbers a user looks at first. These were written against the three scans, watched to fail against
 * deliberate mutations of them, and only then pointed at the folded version — so they say the fold
 * preserved the behaviour rather than merely describing whatever it does now.
 *
 * **THE ONE THE FOLD TURNS ON IS `a blank name still counts as a fallback`.** The card list discards
 * any sensor with a blank name; the percentages never did, and only withhold the PREFERRED slot from
 * one. Fold the loops in the obvious order and a host that categorises its sensors correctly but
 * labels them poorly loses its gauges — silently, on the one screen where a wrong number looks
 * exactly like a right one. `parseTelemetry` offers each sensor to the pickers before the blank-name
 * skip for that reason alone.
 *
 * Absent keys rather than explicit JSON nulls throughout: json-java (here) returns the DEFAULT for an
 * explicit null and Android's runtime `org.json` returns the string `"null"`, so a test built on
 * explicit nulls would pin a behaviour the app does not have. That divergence is recorded on
 * RemEx-bcgr.
 */
class ParseTelemetryTest {

    private fun sensors(vararg entries: Map<String, Any>): JSONArray {
        val array = JSONArray()
        for (entry in entries) {
            val obj = JSONObject()
            for ((key, value) in entry) obj.put(key, value)
            array.put(obj)
        }
        return array
    }

    private fun sensor(
        category: String,
        name: String = "some sensor",
        value: Double = 50.0,
        unit: String = "%",
    ) = mapOf("category" to category, "name" to name, "value" to value, "unit" to unit)

    private fun cpu(json: JSONArray?) = parseTelemetry(json).cpu

    @Test
    fun `a null array reads zero rather than throwing`() {
        // The real caller passes an optJSONArray that is genuinely absent on an older host.
        val derived = parseTelemetry(null)

        assertEquals(0, derived.cpu)
        assertEquals(0, derived.gpu)
        assertEquals(0, derived.ram)
        assertEquals(emptyList<Any>(), derived.sensors)
    }

    @Test
    fun `a name containing every preferred token wins over an earlier percentage`() {
        // The generic one is FIRST, so anything that simply took the first "%" in the category would
        // return 11. Preference is what makes "CPU Total Usage" beat "CPU Core #1".
        val json = sensors(
            sensor("CPU", name = "CPU Core #1", value = 11.0),
            sensor("CPU", name = "CPU Total Usage", value = 77.0),
        )

        assertEquals(77, cpu(json))
    }

    @Test
    fun `a preferred match latches and a later one does not replace it`() {
        // The fold's one real behavioural risk. The old scan RETURNED on its first preferred match;
        // the folded loop cannot stop early because it is still building the card list, so it has to
        // refuse the second one explicitly. Drop that refusal and this reads 88.
        val json = sensors(
            sensor("CPU", name = "CPU Total Usage", value = 22.0),
            sensor("CPU", name = "CPU Total Usage", value = 88.0),
        )

        assertEquals(22, cpu(json))
    }

    @Test
    fun `all preferred tokens must be present, not just one`() {
        // "cpu" alone is not enough - every token has to appear, which is what stops a bare "CPU"
        // label from outranking the genuine usage reading. This one falls back instead.
        // THE SECOND NAME CARRIES BOTH TOKENS ON PURPOSE, AND THE FIRST DRAFT OF THIS TEST DID NOT.
        // With "Total Usage" there, `all` and `any` both answered 33 - so the test named after this
        // exact rule could not see the difference, and the all->any mutation it was supposed to
        // catch was actually being caught by the test above it. A reviewer spotted that. With "CPU
        // Total Usage" the partial match takes only the fallback slot and the full match still wins
        // at 44, where `any` would have latched 33 and stopped looking.
        val json = sensors(
            sensor("CPU", name = "CPU Package Power", value = 33.0),
            sensor("CPU", name = "CPU Total Usage", value = 44.0),
        )

        assertEquals(44, cpu(json))
    }

    @Test
    fun `the FIRST percentage in the category is the fallback, not the last`() {
        // First, not last - the same first-match rule selectSensor follows, and the same one an
        // accumulate-as-you-go rewrite inverts by default.
        val json = sensors(
            sensor("CPU", name = "Core #1", value = 10.0),
            sensor("CPU", name = "Core #2", value = 90.0),
        )

        assertEquals(10, cpu(json))
    }

    @Test
    fun `a blank name still counts as a fallback`() {
        // **THE ASYMMETRY THE FOLD HAD TO PRESERVE.** The card list drops a blank-named sensor
        // outright; the percentage keeps it as a fallback and only withholds the preferred slot,
        // because a host that gets its categories right and its labels wrong should still light up
        // the gauge. Move the three offer() calls below the blank-name skip and this reads 0.
        val json = sensors(sensor("CPU", name = "", value = 62.0))

        assertEquals(62, cpu(json))
    }

    @Test
    fun `a blank name is kept by the gauge and dropped by the card list in the same pass`() {
        // The asymmetry stated as one assertion rather than inferred from two tests - this is the
        // shape of payload that made it matter, and the card list must still refuse it.
        val json = sensors(
            sensor("CPU", name = "", value = 62.0),
            sensor("GPU", name = "GPU Total Usage", value = 30.0),
        )
        val derived = parseTelemetry(json)

        assertEquals(62, derived.cpu)
        assertEquals(30, derived.gpu)
        assertEquals(listOf("GPU Total Usage"), derived.sensors.map { it.name })
    }

    @Test
    fun `only percentage units are considered at all`() {
        // Not merely unpreferred - INVISIBLE to the gauge. A watts or megahertz reading in the CPU
        // category must not become a percentage just because nothing better turned up. It is still a
        // perfectly good card, which is why the sensor list keeps both.
        val json = sensors(
            sensor("CPU", name = "CPU Package", value = 65.0, unit = "W"),
            sensor("CPU", name = "CPU Clock", value = 4800.0, unit = "MHz"),
        )
        val derived = parseTelemetry(json)

        assertEquals(0, derived.cpu)
        assertEquals(2, derived.sensors.size)
    }

    @Test
    fun `the category comparison ignores case`() {
        // Hosts are not consistent about this and never have been.
        val json = sensors(sensor("cpu", name = "CPU Total Usage", value = 25.0))

        assertEquals(25, cpu(json))
    }

    @Test
    fun `other categories are not consulted`() {
        val json = sensors(
            sensor("GPU", name = "GPU Total Usage", value = 99.0),
            sensor("Memory", name = "Memory Load", value = 88.0),
        )

        assertEquals(0, cpu(json))
    }

    @Test
    fun `a missing value is skipped rather than read as zero`() {
        // A sensor the host declared but could not read must not drag the gauge to 0 - and must not
        // consume the fallback slot either, or the next usable reading never gets it.
        val json = sensors(
            mapOf("category" to "CPU", "name" to "Core #1", "unit" to "%"),
            sensor("CPU", name = "Core #2", value = 40.0),
        )
        val derived = parseTelemetry(json)

        assertEquals(40, derived.cpu)
        // AND IT NEVER REACHES THE CARDS EITHER. Without this the NaN skip could move below the card
        // append and every other test here would still pass, while a NaN value fed the sparkline
        // buffer updateTelemetryHistory keeps.
        assertEquals(listOf("Core #2"), derived.sensors.map { it.name })
    }

    @Test
    fun `readings are rounded and clamped into zero to one hundred`() {
        assertEquals(43, cpu(sensors(sensor("CPU", value = 42.6))))
        assertEquals(100, cpu(sensors(sensor("CPU", value = 140.0))))
        // The negative reading is paired with a later valid one on purpose: on its own, a 0 cannot
        // tell "clamped to the floor" apart from "never matched at all".
        assertEquals(0, cpu(sensors(sensor("CPU", value = -12.0), sensor("CPU", value = 70.0))))
    }

    @Test
    fun `all three gauges come off one payload in a single pass`() {
        // The three scans this replaced, now genuinely simultaneous - the closest thing here to the
        // screen a user sees.
        val json = sensors(
            sensor("CPU", name = "CPU Total Usage", value = 12.0),
            sensor("GPU", name = "GPU Core Usage", value = 34.0),
            sensor("Memory", name = "Memory Load", value = 56.0),
        )
        val derived = parseTelemetry(json)

        assertEquals(12, derived.cpu)
        assertEquals(34, derived.gpu)
        assertEquals(56, derived.ram)
        assertEquals(3, derived.sensors.size)
    }

    @Test
    fun `an empty array reads zero on every gauge`() {
        // Anti-vacuity: every assertion above depends on the array being walked, so a version that
        // returned constants would have to fail here.
        val derived = parseTelemetry(JSONArray())

        assertEquals(0, derived.cpu)
        assertEquals(0, derived.gpu)
        assertEquals(0, derived.ram)
        assertEquals(emptyList<Any>(), derived.sensors)
    }
}
