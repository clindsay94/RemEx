package com.clindsay94.remex.ui.screens

import com.clindsay94.remex.data.HomePinsSource
import com.clindsay94.remex.data.HomePinsState
import com.clindsay94.remex.data.KnownHost
import com.clindsay94.remex.data.KnownPcEntry
import com.clindsay94.remex.routines.RoutineHistoryStore
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunOutcomes
import com.clindsay94.remex.routines.model.RoutinePowerVerbs
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Home's rules (RemEx-wqo7a.6): uptime, the quick actions, pinned tiles, the pin sheet, Recent activity. */
class HomeLogicTest {

    @Test
    fun `uptime parses the host's format`() {
        assertEquals(PcUptime(3, 4, 5), HomeLogic.parseUptime("3d 4h 5m"))
        assertEquals(PcUptime(0, 0, 0), HomeLogic.parseUptime("0d 0h 0m"))
        assertEquals(PcUptime(120, 23, 59), HomeLogic.parseUptime(" 120d 23h 59m "))
    }

    @Test
    fun `uptime is hidden for anything else`() {
        assertNull(HomeLogic.parseUptime("N/A"))
        assertNull(HomeLogic.parseUptime(""))
        assertNull(HomeLogic.parseUptime(null))
        assertNull(HomeLogic.parseUptime("3 days"))
        assertNull(HomeLogic.parseUptime("3d 4h"))
        assertNull(HomeLogic.parseUptime("3d4h5m"))
        assertNull(HomeLogic.parseUptime("99999999999d 1h 1m"))
    }

    @Test
    fun `uptime shows the two largest units and drops zeros`() {
        val d = UptimeUnit.DAYS
        val h = UptimeUnit.HOURS
        val m = UptimeUnit.MINUTES
        assertEquals(listOf(h to 3, m to 5), HomeLogic.uptimeParts(PcUptime(0, 3, 5)))
        assertEquals(listOf(d to 2, h to 4), HomeLogic.uptimeParts(PcUptime(2, 4, 59)))
        assertEquals(listOf(d to 2), HomeLogic.uptimeParts(PcUptime(2, 0, 5)))
        assertEquals(listOf(h to 3), HomeLogic.uptimeParts(PcUptime(0, 3, 0)))
        assertEquals(listOf(m to 7), HomeLogic.uptimeParts(PcUptime(0, 0, 7)))
        assertEquals(listOf(m to 0), HomeLogic.uptimeParts(PcUptime(0, 0, 0)))
    }

    @Test
    fun `the uptime line joins unit strings into one placeholder in every locale`() {
        val res = java.io.File("src/main/res")
        for (dir in listOf("values", "values-es", "values-fr", "values-hi", "values-in", "values-pl", "values-pt-rBR", "values-tr", "values-uk")) {
            val xml = java.io.File(res, "$dir/strings.xml").readText(Charsets.UTF_8)
            fun value(key: String) =
                    Regex("""<string name="$key">(.*?)</string>""").find(xml)?.groupValues?.get(1)
            assertEquals("$dir home_pc_uptime", 1, Regex("""%1\${'$'}s""").findAll(value("home_pc_uptime").orEmpty()).count())
            for (key in listOf("home_pc_uptime_days", "home_pc_uptime_hours", "home_pc_uptime_minutes")) {
                assertTrue("$dir $key", value(key)?.contains("%1\$d") == true)
            }
        }
    }

    @Test
    fun `uptimeText is read from the telemetry payload`() {
        assertEquals("1d 2h 3m", HomeLogic.uptimeTextOf("""{"sensors":[],"uptimeText":"1d 2h 3m"}"""))
        assertNull(HomeLogic.uptimeTextOf("""{"sensors":[]}"""))
        assertNull(HomeLogic.uptimeTextOf("nope"))
    }

    @Test
    fun `Sleep is hidden when the PC advertises verbs without it`() {
        val noSleep = listOf(RoutinePowerVerbs.LOCK, RoutinePowerVerbs.SHUTDOWN)
        assertFalse(HomeLogic.isQuickActionOffered("Sleep", noSleep))
        assertTrue(HomeLogic.isQuickActionOffered("Lock", noSleep))
    }

    @Test
    fun `Sleep shows when the PC says nothing or lists it`() {
        assertTrue(HomeLogic.isQuickActionOffered("Sleep", null))
        assertTrue(HomeLogic.isQuickActionOffered("Sleep", listOf(RoutinePowerVerbs.SLEEP)))
    }

    @Test
    fun `Lock and Sleep follow the Commands grid's confirmation policy`() {
        HomeLogic.QUICK_ACTIONS.forEach { assertEquals(actionDiscardsWork(it), HomeLogic.needsConfirmation(it)) }
        assertFalse(HomeLogic.needsConfirmation("Lock"))
        assertFalse(HomeLogic.needsConfirmation("Sleep"))
    }

    private fun sensor(name: String, group: String = "", category: String = "CPU") =
            TelemetrySensor(id = "id:$name", name = name, category = category, value = 1.0, unit = "%", group = group)

    @Test
    fun `pinned tiles are the reporting pins, in pin order, matched case-insensitively`() {
        val sensors = listOf(sensor("CPU Load"), sensor("GPU Load"), sensor("RAM"))
        val tiles = HomeLogic.pinnedTiles(listOf("ram", "Fan 9", "CPU Load"), sensors)
        assertEquals(listOf("RAM", "CPU Load"), tiles.map { it.name })
    }

    @Test
    fun `the pin sheet on a syncing PC lists what it can pin, grouped, with stale pins last`() {
        val state =
                HomePinsState(
                        pinned = listOf("CPU Load", "Old Sensor"),
                        pinnable = listOf("CPU Load", "GPU Load"),
                        syncSupported = true,
                        source = HomePinsSource.HOST,
                        connected = true,
                )
        val sensors = listOf(sensor("CPU Load", group = "Processor"), sensor("GPU Load", group = "Graphics"), sensor("Fan 1", group = "Cooling"))
        val groups = HomeLogic.pinSheet(state, sensors)
        assertEquals(listOf("Processor", "Graphics", null), groups.map { it.label })
        assertEquals(PinSheetEntry("CPU Load", pinned = true, canPin = true), groups[0].entries.single())
        assertEquals(PinSheetEntry("GPU Load", pinned = false, canPin = true), groups[1].entries.single())
        assertEquals(PinSheetEntry("Old Sensor", pinned = true, canPin = false), groups[2].entries.single())
        assertTrue("Fan 1 has no PC card, so it is not offered", groups.flatMap { it.entries }.none { it.name == "Fan 1" })
    }

    @Test
    fun `the pin sheet for an older PC offers every reporting sensor`() {
        val state = HomePinsState(pinned = emptyList(), syncSupported = false, connected = true)
        val groups = HomeLogic.pinSheet(state, listOf(sensor("CPU Load", category = "CPU"), sensor("Fan 1", category = "Fans")))
        assertEquals(listOf("CPU", "Fans"), groups.map { it.label })
        assertTrue(groups.flatMap { it.entries }.all { it.canPin })
    }

    private fun entry(name: String, at: Long) =
            KnownPcEntry(
                    address = "10.0.0.$at",
                    port = 5005,
                    nickname = name,
                    lastConnectedAtMillis = at,
                    knownHost = null,
                    isTrusted = true,
            )

    @Test
    fun `recent activity is newest first and skips running runs and store events`() {
        val history =
                listOf(
                        RoutineRun(routineId = "r1", routineName = "Gaming", outcome = RoutineRunOutcomes.RUNNING, startedAtUnixMs = 900),
                        RoutineRun(routineId = RoutineHistoryStore.STORE_EVENTS_ID, routineName = "store", outcome = RoutineRunOutcomes.FAILED, startedAtUnixMs = 800),
                        RoutineRun(routineId = "r2", routineName = "Morning", outcome = RoutineRunOutcomes.SUCCEEDED, startedAtUnixMs = 100, endedAtUnixMs = 300),
                )
        val rows = HomeLogic.recentActivity(history, listOf(entry("Tower", 200), entry("Laptop", 50)))
        assertEquals(2, rows.size)
        assertEquals(RecentActivity.RoutineRan("Morning", succeeded = true, reasonCode = null, reasonArgs = null, atMillis = 300), rows[0])
        assertEquals(RecentActivity.Connected("Tower", 200), rows[1])
    }

    @Test
    fun `recent activity is empty when there is nothing to show`() {
        assertTrue(HomeLogic.recentActivity(emptyList(), listOf(entry("Never", 0))).isEmpty())
    }

    @Test
    fun `the PC card name prefers the Known PCs name, then the reported machine name, then the address`() {
        val named = KnownPcEntry("10.0.0.5", 5005, "Desk", 1, null, true)
        assertEquals("Desk", HomeLogic.pcName("10.0.0.5", listOf(named), "TOWER-01"))
        val unnamed = KnownPcEntry("10.0.0.6", 5005, "", 1, KnownHost("id", "", listOf("10.0.0.6"), 5005, 1, machineName = ""), true)
        assertEquals("TOWER-01", HomeLogic.pcName("10.0.0.6", listOf(unnamed), "TOWER-01"))
        assertEquals("10.0.0.7", HomeLogic.pcName("10.0.0.7", emptyList(), " "))
    }

    @Test
    fun `the disconnected PC card offers Wake only when a PC is paired (RemEx-pp4cm_3 home-firstrun-card)`() {
        // A fresh install has nothing to wake; the attempt's error sent a new user to a MAC setting.
        assertFalse(HomeLogic.connectCardOffersWake(0))
        assertTrue(HomeLogic.connectCardOffersWake(1))
        assertTrue(HomeLogic.connectCardOffersWake(3))
    }
}
