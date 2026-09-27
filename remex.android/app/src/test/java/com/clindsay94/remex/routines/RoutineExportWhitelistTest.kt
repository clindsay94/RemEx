package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineAppearance
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import com.clindsay94.remex.routines.model.RoutineStep
import com.clindsay94.remex.routines.model.RoutineTrigger
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Android's org.json has no keySet(). */
private fun JSONObject.keySet(): Set<String> = keys().asSequence().toSet()

/**
 * T13 (routines spec §9, §6.10): the `.remexroutines` export never carries a token, a key, a home,
 * the target PC's `hostIdentity` or a MAC. The export is a whitelist, so this pins the key sets rather
 * than hunting for the fields that must be absent (RemEx-pp0rt.11).
 */
class RoutineExportWhitelistTest {
    private val homeId = "b7e14c0a-52c1-4e4f-8f43-0d7c2f9a6e11"

    private fun homeRoutine() =
        Routine(
            id = uuid(),
            name = "Home",
            hostIdentity = HOST,
            enabled = true,
            revision = 7,
            createdAtUnixMs = 11,
            updatedAtUnixMs = 12,
            appearance = RoutineAppearance(icon = "home", color = "#FF0000"),
            trigger = RoutineTrigger(type = "home.arrive", homeId = homeId),
            steps = listOf(RoutineStep(type = "wake", mac = "0A:1B:2C:3D:4E:5F", broadcastIp = "192.168.1.255", port = 9), waitOnline(), lock()),
        )

    private fun exported(vararg routines: Routine): JSONObject =
        JSONObject(RoutineExchange.export(routines.toList(), exportedAtUnixMs = 1_790_000_000_000, exportedBy = "RemEx Android test"))

    @Test
    fun `the file header is the versioned remex routines format`() {
        val file = exported(pcIdleRoutine())
        assertEquals("remex.routines", file.getString("format"))
        assertEquals(1, file.getInt("formatVersion"))
        assertEquals(1, file.getInt("schemaVersion"))
        assertEquals(1_790_000_000_000, file.getLong("exportedAtUnixMs"))
        assertEquals(setOf("format", "formatVersion", "schemaVersion", "exportedAtUnixMs", "exportedBy", "routines"), file.keySet())
    }

    @Test
    fun `every object in the file uses only whitelisted keys`() {
        val file = exported(homeRoutine(), pcIdleRoutine(), manualRoutine(wake(), waitOnline(), notifyPhone()))
        val routines = file.getJSONArray("routines")
        assertEquals(3, routines.length())
        for (i in 0 until routines.length()) {
            val routine = routines.getJSONObject(i)
            assertTrue(routine.keySet().toString(), RoutineExchange.ROUTINE_KEYS.containsAll(routine.keySet()))
            routine.optJSONObject("appearance")?.let { assertTrue(RoutineExchange.APPEARANCE_KEYS.containsAll(it.keySet())) }
            routine.optJSONObject("trigger")?.let { assertTrue(it.keySet().toString(), RoutineExchange.TRIGGER_KEYS.containsAll(it.keySet())) }
            val steps = routine.getJSONArray("steps")
            for (s in 0 until steps.length()) {
                val step = steps.getJSONObject(s)
                assertTrue(step.keySet().toString(), RoutineExchange.STEP_KEYS.containsAll(step.keySet()))
            }
        }
    }

    @Test
    fun `no host identity, home, MAC, broadcast address, switch state or revision reaches the file`() {
        val text = RoutineExchange.export(listOf(homeRoutine()), 0, "test")
        for (secret in listOf(HOST, homeId, "0A:1B:2C:3D:4E:5F", "192.168.1.255")) assertFalse(secret, text.contains(secret))
        for (key in listOf("hostIdentity", "homeId", "mac", "broadcastIp", "enabled", "revision", "createdAtUnixMs", "updatedAtUnixMs")) {
            assertFalse(key, text.contains("\"$key\""))
        }
        // The whitelist itself never names them.
        val all = RoutineExchange.ROUTINE_KEYS + RoutineExchange.TRIGGER_KEYS + RoutineExchange.STEP_KEYS + RoutineExchange.APPEARANCE_KEYS
        assertTrue(all.none { it in setOf("hostIdentity", "homeId", "mac", "broadcastIp", "token", "key", "tagToken", "enabled") })
    }

    @Test
    fun `a malformed stored routine is left out rather than exported raw`() {
        val file = exported(Routine(id = uuid(), isMalformed = true, rawJson = """{"hostIdentity":"$HOST"}"""), pcIdleRoutine())
        assertEquals(1, file.getJSONArray("routines").length())
        assertFalse(file.toString().contains(HOST))
    }

    @Test
    fun `import round trip re-binds id, PC, MAC and home and arrives switched off`() {
        val original = homeRoutine()
        val read = RoutineExchange.read(RoutineExchange.export(listOf(original, pcIdleRoutine()), 0, "test")) as RoutineExchange.ReadResult.Ok
        assertEquals(2, read.routines.size)
        val reviews =
            RoutineExchange.review(read.routines, hostIdentity = OTHER_HOST, mac = "AA:BB:CC:DD:EE:FF", homeId = homeId, existingNames = listOf("home"), newId = ::uuid)
        assertTrue(reviews.all { it.importable })
        val home = reviews[0].prepared!!
        assertNotEquals(original.id, home.id)
        assertEquals(OTHER_HOST, home.hostIdentity)
        assertFalse(home.enabled)
        assertEquals(homeId, home.trigger?.homeId)
        assertEquals("AA:BB:CC:DD:EE:FF", home.steps!![0]!!.mac)
        assertNull(home.steps!![0]!!.broadcastIp)
        assertEquals(original.name, home.name)
        assertEquals(original.appearance, home.appearance)
        assertEquals(original.steps!!.drop(1), home.steps!!.drop(1))
        assertTrue("case-insensitive name clash", reviews[0].nameTaken)
        assertEquals(RoutineReasonCodes.OK, reviews[1].verdict.reasonCode)
    }

    @Test
    fun `a home routine without a home on this phone and a wake without a MAC are not importable`() {
        val read = RoutineExchange.read(RoutineExchange.export(listOf(homeRoutine(), manualRoutine(wake(), lock())), 0, "t")) as RoutineExchange.ReadResult.Ok
        val reviews = RoutineExchange.review(read.routines, hostIdentity = HOST, mac = null, homeId = null, existingNames = emptyList(), newId = ::uuid)
        assertTrue(reviews[0].needsHome)
        assertFalse(reviews[0].importable)
        assertEquals(RoutineReasonCodes.WAKE_NO_MAC, reviews[1].verdict.reasonCode)
        assertFalse(reviews[1].importable)
    }

    @Test
    fun `an NFC routine is flagged for a tag rewrite`() {
        val nfc = Routine(id = uuid(), name = "Desk", hostIdentity = HOST, enabled = true, trigger = RoutineTrigger(type = "nfc.tap"), steps = listOf(lock()))
        val read = RoutineExchange.read(RoutineExchange.export(listOf(nfc), 0, "t")) as RoutineExchange.ReadResult.Ok
        val review = RoutineExchange.review(read.routines, HOST, null, null, emptyList(), ::uuid).single()
        assertTrue(review.needsTag)
        assertTrue(review.importable)
    }

    @Test
    fun `hand-edited secrets in an imported file are discarded`() {
        val routine =
            JSONObject()
                .put("id", uuid())
                .put("name", "Sneaky")
                .put("hostIdentity", "ffffffffffffffff")
                .put("enabled", true)
                .put("trigger", JSONObject().put("type", "manual"))
                .put("steps", JSONArray().put(JSONObject().put("type", "power").put("verb", "LOCK").put("mac", "11:22:33:44:55:66")))
        val file = JSONObject().put("format", "remex.routines").put("formatVersion", 1).put("routines", JSONArray().put(routine)).toString()
        val read = RoutineExchange.read(file) as RoutineExchange.ReadResult.Ok
        val prepared = RoutineExchange.review(read.routines, HOST, null, null, emptyList(), ::uuid).single().prepared!!
        assertEquals(HOST, prepared.hostIdentity)
        assertFalse(prepared.enabled)
        assertNull(prepared.steps!![0]!!.mac)
    }

    @Test
    fun `malformed files are refused with a reason, never a crash`() {
        for (bad in listOf(null, "", "not json", "[]", "{}", """{"format":"remex.palette","formatVersion":1,"routines":[]}""",
            """{"format":"remex.routines","routines":[]}""", """{"format":"remex.routines","formatVersion":"1","routines":[]}""",
            """{"format":"remex.routines","formatVersion":1.5,"routines":[]}""", """{"format":"remex.routines","formatVersion":1,"routines":{}}""")) {
            assertEquals(bad.toString(), RoutineExchange.ReadResult.Unreadable, RoutineExchange.read(bad))
        }
        assertEquals(RoutineExchange.ReadResult.TooNew, RoutineExchange.read("""{"format":"remex.routines","formatVersion":2,"routines":[]}"""))
    }

    @Test
    fun `an unreadable element is listed and skipped while the rest import`() {
        val file = """{"format":"remex.routines","formatVersion":1,"extra":true,"routines":[42,{"name":"Bad","trigger":{"type":7}},""" +
            JSONObject(RoutineExchange.export(listOf(pcIdleRoutine()), 0, "t")).getJSONArray("routines").get(0).toString() + "]}"
        val read = RoutineExchange.read(file) as RoutineExchange.ReadResult.Ok
        val reviews = RoutineExchange.review(read.routines, HOST, null, null, emptyList(), ::uuid)
        assertEquals(listOf(false, false, true), reviews.map { it.importable })
        assertNull(reviews[0].prepared)
    }
}
