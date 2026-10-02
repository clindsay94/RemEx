package com.clindsay94.remex.ui.screens.sensors

import com.clindsay94.remex.ui.screens.HomeCardType
import com.clindsay94.remex.ui.screens.TelemetryDisplayMode
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Free-form canvas (layout schema 1) to grid (schema 2), RemEx-wqo7a.7: no TELEMETRY card is lost,
 * the PC Status and Wake cards go, reading order is recovered from positions, the migration is
 * idempotent, and a schema-2 save is still readable by the old schema-1 reader (a downgraded APK).
 */
class SensorGridMigrationTest {

    private fun card(id: String, type: String, x: Double, y: Double, w: Double, h: Double, extra: JSONObject.() -> Unit = {}) =
            JSONObject()
                    .put("id", id)
                    .put("title", id.uppercase())
                    .put("type", type)
                    .put("sensorId", if (type == "TELEMETRY") id else JSONObject.NULL)
                    .put("xDp", x)
                    .put("yDp", y)
                    .put("widthDp", w)
                    .put("heightDp", h)
                    .put("pinned", true)
                    .apply(extra)

    /** A realistic v1 save: hero cards, a mixed spread of telemetry cards, saved out of reading order. */
    private val v1 =
            JSONObject()
                    .put("schemaVersion", 1)
                    .put(
                            "cards",
                            JSONArray()
                                    .put(card("pc_status", "PC_STATUS", 12.0, 12.0, 220.0, 140.0))
                                    .put(card("wake_pc", "WAKE_ON_LAN", 244.0, 12.0, 160.0, 140.0))
                                    .put(card("sensor:ram", "TELEMETRY", 20.0, 326.0, 170.0, 150.0))
                                    .put(card("sensor:gpu", "TELEMETRY", 200.0, 170.0, 170.0, 150.0) { put("displayMode", "GAUGE") })
                                    .put(card("sensor:cpu", "TELEMETRY", 20.0, 168.0, 260.0, 230.0) { put("customTitle", "Main CPU") })
                                    .put(card("sensor:net", "TELEMETRY", 400.0, 160.0, 150.0, 300.0))
                                    .put(card("fan1", "TELEMETRY", 20.0, 900.0, 300.0, 150.0))
                    )
                    .toString()

    @Test
    fun `every telemetry card survives and the hero cards are dropped`() {
        val cards = SensorLayoutCodec.toCards(SensorLayoutCodec.decode(v1))
        assertEquals(setOf("sensor:ram", "sensor:gpu", "sensor:cpu", "sensor:net", "fan1"), cards.map { it.id }.toSet())
        assertTrue(cards.all { it.type == HomeCardType.TELEMETRY })
    }

    @Test
    fun `reading order comes from 48dp bands, then x`() {
        val cards = SensorLayoutCodec.toCards(SensorLayoutCodec.decode(v1))
        // y 160/168/170 share the 144..192 band -> by x: cpu(20), gpu(200), net(400); then ram (326), fan1 (900).
        assertEquals(listOf("sensor:cpu", "sensor:gpu", "sensor:net", "sensor:ram", "fan1"), cards.map { it.id })
    }

    @Test
    fun `sizes become spans, tall narrow cards become one cell`() {
        val spans = SensorLayoutCodec.toCards(SensorLayoutCodec.decode(v1)).associate { it.id to it.span }
        assertEquals(CardSpan.TWO_BY_TWO, spans["sensor:cpu"])   // 260 x 230
        assertEquals(CardSpan.ONE_BY_ONE, spans["sensor:gpu"])   // 170 x 150
        assertEquals(CardSpan.ONE_BY_ONE, spans["sensor:net"])   // 150 x 300: tall and narrow
        assertEquals(CardSpan.TWO_BY_ONE, spans["fan1"])         // 300 x 150
    }

    @Test
    fun `per-card settings are carried`() {
        val cards = SensorLayoutCodec.toCards(SensorLayoutCodec.decode(v1)).associateBy { it.id }
        assertEquals("Main CPU", cards.getValue("sensor:cpu").customTitle)
        assertEquals(TelemetryDisplayMode.RING_GAUGE, cards.getValue("sensor:gpu").displayMode)
    }

    @Test
    fun `migrating is idempotent - a migrated save reads back unchanged`() {
        val once = SensorLayoutCodec.toCards(SensorLayoutCodec.decode(v1))
        val saved = SensorLayoutCodec.decode(SensorLayoutCodec.encode(once))
        assertEquals(SensorLayoutCodec.SCHEMA_VERSION, saved.schemaVersion)
        val twice = SensorLayoutCodec.toCards(saved)
        assertEquals(once, twice)
        assertEquals(twice, SensorLayoutCodec.toCards(SensorLayoutCodec.decode(SensorLayoutCodec.encode(twice))))
    }

    @Test
    fun `a pre-2_0 bare array migrates too`() {
        val bare = JSONArray().put(card("sensor:cpu", "TELEMETRY", 0.0, 0.0, 170.0, 150.0)).toString()
        val saved = SensorLayoutCodec.decode(bare)
        assertEquals(0, saved.schemaVersion)
        assertEquals(listOf("sensor:cpu"), SensorLayoutCodec.toCards(saved).map { it.id })
    }

    @Test
    fun `the enabled list loses only the hero ids`() {
        assertEquals(setOf("sensor:cpu"), SensorGridMigration.migrateEnabled(setOf("pc_status", "wake_pc", "sensor:cpu")))
    }

    /**
     * The schema-1 reader as it shipped (DashboardViewModel.loadSavedHomeLayout before RemEx-wqo7a.7),
     * reduced to what decides whether a card shows and where: an id, a type and the x/y/width/height.
     */
    private data class V1Card(val id: String, val type: String, val x: Double, val y: Double, val w: Double, val h: Double)

    private fun readAsV1(json: String): List<V1Card> {
        val trimmed = json.trimStart()
        val array = if (trimmed.startsWith("[")) JSONArray(json) else JSONObject(json).optJSONArray("cards") ?: JSONArray()
        val cards = ArrayList<V1Card>()
        for (i in 0 until array.length()) {
            val obj = array.optJSONObject(i) ?: continue
            val id = obj.optString("id")
            if (id.isBlank()) continue
            val type = when (obj.optString("type")) { "PC_STATUS" -> "PC_STATUS"; "WAKE_ON_LAN" -> "WAKE_ON_LAN"; else -> "TELEMETRY" }
            cards += V1Card(id, type, obj.optDouble("xDp", 12.0), obj.optDouble("yDp", 12.0), obj.optDouble("widthDp", 160.0), obj.optDouble("heightDp", 140.0))
        }
        return cards
    }

    @Test
    fun `a schema-2 save read by the old reader shows every card, none overlapping`() {
        val migrated = SensorLayoutCodec.toCards(SensorLayoutCodec.decode(v1))
        val v1View = readAsV1(SensorLayoutCodec.encode(migrated))
        assertEquals(migrated.map { it.id }, v1View.map { it.id })
        assertTrue(v1View.all { it.type == "TELEMETRY" })
        for (i in v1View.indices) for (j in i + 1 until v1View.size) {
            val a = v1View[i]
            val b = v1View[j]
            val overlap = a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h
            assertFalse("$a overlaps $b in the downgrade layout", overlap)
        }
        assertTrue("the old reader's minimum card size", v1View.all { it.w >= 140.0 && it.h >= 120.0 })
    }

    @Test
    fun `a schema-2 save keeps its order and drops the old position anchor`() {
        val migrated = SensorLayoutCodec.toCards(SensorLayoutCodec.decode(v1)).reversed()
        val json = JSONObject(SensorLayoutCodec.encode(migrated))
        val first = json.getJSONArray("cards").getJSONObject(0)
        assertFalse(first.has("pinned"))
        assertEquals(0, first.getInt("order"))
        assertEquals(migrated.map { it.id }, SensorLayoutCodec.toCards(SensorLayoutCodec.decode(json.toString())).map { it.id })
    }
}
