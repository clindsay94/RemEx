package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.TelemetrySensor
import com.clindsay94.remex.ui.screens.selectSensor
import com.clindsay94.remex.ui.telemetry.MetricKind
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Which sensor a dashboard card binds to (RemEx-cite).
 *
 * **CHARACTERISATION TESTS, WRITTEN TO MAKE A REFACTOR SAFE RATHER THAN TO FIX A BUG.** `cite` item 4
 * wants the per-card linear scans replaced with a precomputed map, and this function had no tests at
 * all — so that refactor would have been a rewrite of the rule every card on the Home screen depends
 * on, with nothing to say whether the rule survived. These pin the behaviour as it is today. If item
 * 4 changes an answer, that is the point at which someone decides whether the change is wanted.
 *
 * **THE ORDERING IS THE PART A MAP GETS WRONG.** Three separate preferences are encoded here and all
 * three are first-match: kinds are tried in the listed priority order, then a same-id sensor that is
 * NOT the Unknown sink, then any same-id sensor. A naive `associateBy` keeps the LAST entry per key
 * and would silently invert every one of them, which is exactly the kind of regression that produces
 * a plausible-looking dashboard bound to the wrong readings.
 */
class SelectSensorTest {

    private fun sensor(
        id: String,
        kind: MetricKind = MetricKind.UNKNOWN,
        value: Double = 1.0,
        name: String = id,
    ) = TelemetrySensor(id = id, name = name, category = "", value = value, unit = "", kind = kind)

    @Test
    fun `a null card id selects nothing`() {
        assertNull(selectSensor(null, listOf(sensor("sensor:cpu", MetricKind.CPU_LOAD))))
    }

    @Test
    fun `a curated card prefers its kind over an id match`() {
        // The id match is deliberately FIRST in the list, so a function that searched by id would
        // return it. Kind wins - that is what makes a curated card survive a host that renames ids.
        val byId = sensor("sensor:cpu", MetricKind.UNKNOWN, value = 11.0)
        val byKind = sensor("cpu-package-load", MetricKind.CPU_LOAD, value = 22.0)

        assertEquals(22.0, selectSensor("sensor:cpu", listOf(byId, byKind))!!.value, 0.0)
    }

    @Test
    fun `kinds are tried in the order the card lists them, not in list order`() {
        // sensor:ram accepts RAM_USED_GB then RAM_LOAD. RAM_LOAD appears FIRST in the sensor list,
        // so anything that simply scanned for "an acceptable kind" would return it.
        val load = sensor("a", MetricKind.RAM_LOAD, value = 50.0)
        val usedGb = sensor("b", MetricKind.RAM_USED_GB, value = 8.0)

        assertEquals(8.0, selectSensor("sensor:ram", listOf(load, usedGb))!!.value, 0.0)
    }

    @Test
    fun `the first sensor of a kind wins when several share it`() {
        // FIRST, not last. This is the assertion a map built with associateBy fails, because that
        // keeps the last entry per key.
        val first = sensor("a", MetricKind.CPU_LOAD, value = 1.0)
        val second = sensor("b", MetricKind.CPU_LOAD, value = 2.0)

        assertEquals(1.0, selectSensor("sensor:cpu", listOf(first, second))!!.value, 0.0)
    }

    @Test
    fun `a curated card with no kind match prefers a same-id sensor that is not Unknown`() {
        // The documented fallback: a new host sent no kind-matched sensor, so fall back by id - but
        // skip the Unknown sink first, because binding a card to it shows a number that means
        // nothing rather than showing nothing.
        val unknownSink = sensor("sensor:gputemp", MetricKind.UNKNOWN, value = 0.0)
        val typed = sensor("sensor:gputemp", MetricKind.FAN_RPM, value = 1200.0)

        assertEquals(1200.0, selectSensor("sensor:gputemp", listOf(unknownSink, typed))!!.value, 0.0)
    }

    @Test
    fun `an old host with only Unknown kinds still binds by id`() {
        // The last fallback, and the reason the previous one cannot simply exclude Unknown outright:
        // a host that sends no kinds at all would otherwise bind nothing anywhere.
        val onlySink = sensor("sensor:cputemp", MetricKind.UNKNOWN, value = 65.0)

        assertEquals(65.0, selectSensor("sensor:cputemp", listOf(onlySink))!!.value, 0.0)
    }

    @Test
    fun `an uncurated card id matches by id alone`() {
        // Not one of the eight curated ids, so no kind list applies and it is a plain id lookup.
        val other = sensor("nvme-0-temp", MetricKind.TEMP_C, value = 41.0)

        assertEquals(41.0, selectSensor("nvme-0-temp", listOf(other, sensor("x")))!!.value, 0.0)
        assertNull(selectSensor("nvme-0-temp", listOf(sensor("x"))))
    }

    @Test
    fun `a curated card selects nothing when neither kind nor id is present`() {
        assertNull(selectSensor("sensor:gpu", listOf(sensor("unrelated", MetricKind.FAN_RPM))))
    }

    @Test
    fun `an empty sensor list selects nothing for any card`() {
        // Anti-vacuity for the suite: every assertion above depends on the list being consulted at
        // all, so a version that returned a constant would need to fail here.
        for (id in listOf("sensor:cpu", "sensor:ram", "sensor:nettotal", "anything-else")) {
            assertNull(selectSensor(id, emptyList()))
        }
    }
}
