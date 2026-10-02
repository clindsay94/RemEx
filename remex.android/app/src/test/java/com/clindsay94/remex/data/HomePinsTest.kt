package com.clindsay94.remex.data

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The phone half of the `home_pins_*` wire contract (docs/API_CONTRACTS.md section 9; RemEx-wqo7a.6):
 * the change envelope, the sync parser and the name rules, which must match
 * `remex.core/Validation/HomePinsValidation.cs` or the PC drops what the phone sends.
 */
class HomePinsTest {

    @Test
    fun `the change envelope is one sensor, protocol 2, under homePinChange`() {
        val json = JSONObject(HomePins.buildChangeEnvelope("CPU Package", pinned = true)!!)
        assertEquals("home_pins_change", json.getString("type"))
        assertEquals(2, json.getInt("protocolVersion"))
        val change = json.getJSONObject("homePinChange")
        assertEquals("CPU Package", change.getString("sensorName"))
        assertTrue(change.getBoolean("pinned"))
        assertEquals("one sensor name and one flag, never a list", 2, change.length())
    }

    @Test
    fun `no envelope for a name the PC would drop`() {
        assertNull(HomePins.buildChangeEnvelope("", pinned = true))
        assertNull(HomePins.buildChangeEnvelope("   ", pinned = true))
        assertNull(HomePins.buildChangeEnvelope("a".repeat(201), pinned = false))
        assertNull(HomePins.buildChangeEnvelope("CPU\u0007", pinned = true))
        assertNotNull(HomePins.buildChangeEnvelope("a".repeat(200), pinned = true))
    }

    @Test
    fun `a sync is parsed and normalised`() {
        val json =
                """{"type":"home_pins_sync","protocolVersion":2,"homePins":{
                    "sensorNames":["CPU","cpu","GPU Hot Spot","","bad\u0001"],
                    "pinnableSensorNames":["CPU","GPU Hot Spot","RAM"],
                    "revision":7,"updatedUtc":"2026-10-02T10:00:00Z"}}"""
        val sync = HomePins.parseSync(json)!!
        assertEquals(listOf("CPU", "GPU Hot Spot"), sync.sensorNames)
        assertEquals(listOf("CPU", "GPU Hot Spot", "RAM"), sync.pinnableSensorNames)
        assertEquals(7L, sync.revision)
    }

    @Test
    fun `a sync with absent or wrong-typed lists reads as empty, not as a failure`() {
        val sync = HomePins.parseSync("""{"type":"home_pins_sync","homePins":{"sensorNames":"CPU"}}""")!!
        assertEquals(emptyList<String>(), sync.sensorNames)
        assertEquals(emptyList<String>(), sync.pinnableSensorNames)
        assertEquals(0L, sync.revision)
    }

    @Test
    fun `anything that is not a home_pins_sync with a payload is ignored`() {
        assertNull(HomePins.parseSync(null))
        assertNull(HomePins.parseSync("not json"))
        assertNull(HomePins.parseSync("""{"type":"home_pins_change","homePins":{}}"""))
        assertNull(HomePins.parseSync("""{"type":"home_pins_sync"}"""))
        assertNull(HomePins.parseSync("""{"type":"home_pins_sync","homePins":null}"""))
    }

    @Test
    fun `normalising dedupes case-insensitively keeping the first and caps at 100`() {
        assertEquals(listOf("CPU", "gpu"), HomePins.normalizeNames(listOf("CPU", "cpu", null, "gpu", "GPU")))
        val many = (1..150).map { "Sensor $it" }
        assertEquals(100, HomePins.normalizeNames(many).size)
        assertEquals("Sensor 100", HomePins.normalizeNames(many).last())
    }

    @Test
    fun `withPin appends a new pin last and unpins every case-insensitive match`() {
        assertEquals(listOf("CPU", "RAM"), HomePins.withPin(listOf("CPU"), "RAM", pinned = true))
        assertEquals(listOf("CPU"), HomePins.withPin(listOf("CPU"), "cpu", pinned = true))
        assertEquals(listOf("RAM"), HomePins.withPin(listOf("CPU", "RAM"), "cpu", pinned = false))
    }

    @Test
    fun `the capability is true only for a boolean true`() {
        assertTrue(HomePins.parseSupportsSync("""{"supportsHomePinsSync":true}"""))
        assertFalse(HomePins.parseSupportsSync("""{"machineName":"Tower"}"""))
        assertFalse(HomePins.parseSupportsSync("""{"supportsHomePinsSync":"true"}"""))
        assertFalse(HomePins.parseSupportsSync("garbage"))
        assertFalse(HomePins.parseSupportsSync(null))
    }

    @Test
    fun `the per-PC cache round-trips and unreadable caches read as none`() {
        val cache = HomePinsCache(listOf("CPU", "RAM"), listOf("CPU", "RAM", "GPU"), HomePinsSource.LOCAL)
        assertEquals(cache, HomePins.decodeCache(HomePins.encodeCache(cache)))
        assertEquals(HomePinsSource.HOST, HomePins.decodeCache("""{"names":[],"source":"host"}""")!!.source)
        assertNull(HomePins.decodeCache("{oops"))
        assertNull(HomePins.decodeCache(""))
        assertEquals("homePins_abc123", HomePins.cacheKeyName("abc123"))
    }
}
