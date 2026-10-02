package com.clindsay94.remex.ui.screens.sensors

import com.clindsay94.remex.ui.screens.DashboardShapes
import com.clindsay94.remex.ui.screens.HomeCardState
import com.clindsay94.remex.ui.screens.HomeCardType
import com.clindsay94.remex.ui.screens.TelemetryDisplayMode
import kotlin.math.floor
import org.json.JSONArray
import org.json.JSONObject

/** A saved card's free-form canvas geometry (layout schema 0 and 1), read only to migrate it. */
data class LegacyGeometry(val xDp: Float, val yDp: Float, val widthDp: Float, val heightDp: Float)

/** One card as stored: the card, its old geometry, and its grid position when the save has one. */
data class SavedCard(val card: HomeCardState, val geometry: LegacyGeometry, val order: Int?)

/** A decoded `homeLayout` value. [schemaVersion] 0 is the pre-2.0 bare array. */
data class SavedLayout(val schemaVersion: Int, val cards: List<SavedCard>)

/**
 * Free-form canvas (schema 1) to aligned grid (schema 2), RemEx-wqo7a.7. Pure, run once on load.
 *
 * - The PC Status and Wake cards are dropped: Home owns both now.
 * - Every TELEMETRY card is kept. Reading order is recovered from the old positions: top to bottom
 *   in [BAND_DP] bands (two cards a few dp apart in height still read as one row), then left to
 *   right, then the saved order as the final tie-break, so the result is deterministic.
 * - Size becomes a span: at least [WIDE_MIN_WIDTH_DP] wide is two columns, at least
 *   [TALL_MIN_HEIGHT_DP] tall is two rows, and a tall narrow card (no 1x2 exists) is one cell.
 * - The old `pinned` position anchor means nothing on a grid and is not carried.
 */
object SensorGridMigration {

    const val BAND_DP = 48f
    const val WIDE_MIN_WIDTH_DP = 240f
    const val TALL_MIN_HEIGHT_DP = 210f

    /** Card ids that existed only on the canvas and now live on Home. */
    val REMOVED_CARD_IDS: Set<String> = setOf("pc_status", "wake_pc")

    fun spanFor(geometry: LegacyGeometry): CardSpan {
        val wide = geometry.widthDp >= WIDE_MIN_WIDTH_DP
        val tall = geometry.heightDp >= TALL_MIN_HEIGHT_DP
        return when {
            wide && tall -> CardSpan.TWO_BY_TWO
            wide -> CardSpan.TWO_BY_ONE
            else -> CardSpan.ONE_BY_ONE
        }
    }

    fun migrate(cards: List<SavedCard>): List<HomeCardState> =
            cards.withIndex()
                    .filter { it.value.card.type == HomeCardType.TELEMETRY && it.value.card.id !in REMOVED_CARD_IDS }
                    .sortedWith(
                            compareBy<IndexedValue<SavedCard>>(
                                    { floor(it.value.geometry.yDp / BAND_DP) },
                                    { it.value.geometry.xDp },
                                    { it.index }
                            )
                    )
                    .map { it.value.card.copy(span = spanFor(it.value.geometry)) }

    fun migrateEnabled(enabled: Set<String>): Set<String> = enabled - REMOVED_CARD_IDS
}

/**
 * Reads and writes the Sensors layout (`SettingsManager.homeLayoutJsonFlow`): the envelope
 * `{schemaVersion, cards:[...]}`, or the pre-2.0 bare array. Lenient like the old reader: a card with
 * no id is skipped, an unknown field or value falls back to its default, nothing throws on a field.
 *
 * Schema 2 writes `order`, `colSpan` and `rowSpan` AND the old `xDp/yDp/widthDp/heightDp`, laid out
 * as two [LEGACY_CELL_DP] columns, so an older APK installed over this one still shows every card in
 * a sane non-overlapping layout rather than a pile at the origin.
 */
object SensorLayoutCodec {

    const val SCHEMA_VERSION = 2

    const val LEGACY_CELL_DP = 160f
    const val LEGACY_GUTTER_DP = 12f
    const val LEGACY_MARGIN_DP = 12f

    fun decode(json: String): SavedLayout {
        val trimmed = json.trimStart()
        if (trimmed.isEmpty()) return SavedLayout(SCHEMA_VERSION, emptyList())
        val (version, array) =
                if (trimmed.startsWith("[")) {
                    0 to JSONArray(json)
                } else {
                    val envelope = JSONObject(json)
                    envelope.optInt("schemaVersion", 1) to (envelope.optJSONArray("cards") ?: JSONArray())
                }

        val cards = ArrayList<SavedCard>()
        for (i in 0 until array.length()) {
            val obj = array.optJSONObject(i) ?: continue
            val id = obj.optString("id")
            if (id.isBlank()) continue
            val type =
                    when (obj.optString("type")) {
                        "PC_STATUS" -> HomeCardType.PC_STATUS
                        "WAKE_ON_LAN" -> HomeCardType.WAKE_ON_LAN
                        else -> HomeCardType.TELEMETRY
                    }
            val card =
                    HomeCardState(
                            id = id,
                            title = obj.optString("title").ifBlank { id },
                            type = type,
                            sensorId = obj.optString("sensorId").takeIf { it.isNotBlank() && it != "null" },
                            span = CardSpan.of(obj.optInt("colSpan", 1), obj.optInt("rowSpan", 1)),
                            displayMode = displayModeOf(obj.optString("displayMode")),
                            secondarySensorId = obj.optString("secondarySensorId").takeIf { it.isNotBlank() && it != "null" },
                            shapePreset =
                                    obj.optDouble("shapePreset", DashboardShapes.SHAPE_PRESET_INHERIT.toDouble()).toFloat(),
                            customTitle = obj.optString("customTitle").takeIf { it.isNotBlank() && it != "null" },
                            showValueOverlay = obj.optBoolean("showValueOverlay", false)
                    )
            val geometry =
                    LegacyGeometry(
                            xDp = obj.optDouble("xDp", 12.0).toFloat(),
                            yDp = obj.optDouble("yDp", 12.0).toFloat(),
                            widthDp = obj.optDouble("widthDp", 160.0).toFloat(),
                            heightDp = obj.optDouble("heightDp", 140.0).toFloat()
                    )
            val order = if (obj.has("order")) obj.optInt("order", i) else null
            cards += SavedCard(card, geometry, order)
        }
        return SavedLayout(version, cards)
    }

    /** The grid's cards, in grid order. A pre-grid save is migrated; a grid save is read as is. */
    fun toCards(saved: SavedLayout): List<HomeCardState> =
            if (saved.schemaVersion < SCHEMA_VERSION) {
                SensorGridMigration.migrate(saved.cards)
            } else {
                saved.cards.withIndex()
                        .filter { it.value.card.type == HomeCardType.TELEMETRY && it.value.card.id !in SensorGridMigration.REMOVED_CARD_IDS }
                        .sortedWith(compareBy({ it.value.order ?: Int.MAX_VALUE }, { it.index }))
                        .map { it.value.card }
            }

    fun encode(cards: List<HomeCardState>): String {
        val legacyRow = SensorGrid.rowHeight(LEGACY_CELL_DP)
        val legacy =
                SensorGrid.pack(cards.map { it.id to it.span }, SensorGrid.COMPACT_COLUMNS).associateBy { it.id }
        val array = JSONArray()
        cards.forEachIndexed { index, card ->
            val placement = legacy[card.id]
            val obj =
                    JSONObject().apply {
                        put("id", card.id)
                        put("title", card.title)
                        put("type", card.type.name)
                        put("sensorId", card.sensorId)
                        put("order", index)
                        put("colSpan", card.span.cols)
                        put("rowSpan", card.span.rows)
                        if (placement != null) {
                            put("xDp", LEGACY_MARGIN_DP + SensorGrid.x(placement.col, LEGACY_CELL_DP, LEGACY_GUTTER_DP))
                            put("yDp", LEGACY_MARGIN_DP + SensorGrid.y(placement.row, legacyRow, LEGACY_GUTTER_DP))
                            put("widthDp", SensorGrid.extent(placement.colSpan, LEGACY_CELL_DP, LEGACY_GUTTER_DP))
                            put("heightDp", SensorGrid.extent(placement.rowSpan, legacyRow, LEGACY_GUTTER_DP))
                        }
                        put("displayMode", card.displayMode.name)
                        put("secondarySensorId", card.secondarySensorId)
                        put("shapePreset", card.shapePreset)
                        put("customTitle", card.customTitle)
                        put("showValueOverlay", card.showValueOverlay)
                    }
            array.put(obj)
        }
        return JSONObject().put("schemaVersion", SCHEMA_VERSION).put("cards", array).toString()
    }

    /**
     * Saved display mode to enum. Legacy GAUGE/CIRCLE_GAUGE map onto their visually closest 2.0
     * replacement; anything unrecognised (including absent) becomes AUTO.
     */
    private fun displayModeOf(value: String): TelemetryDisplayMode =
            when (value) {
                "VALUE" -> TelemetryDisplayMode.VALUE
                "VALUE_SPARK" -> TelemetryDisplayMode.VALUE_SPARK
                "GAUGE", "RING_GAUGE" -> TelemetryDisplayMode.RING_GAUGE
                "CIRCLE_GAUGE", "ARC_GAUGE" -> TelemetryDisplayMode.ARC_GAUGE
                "LINE" -> TelemetryDisplayMode.LINE
                "AREA" -> TelemetryDisplayMode.AREA
                "BAR" -> TelemetryDisplayMode.BAR
                "HUE_PULSE" -> TelemetryDisplayMode.HUE_PULSE
                "LED_METER" -> TelemetryDisplayMode.LED_METER
                "DUAL_METRIC" -> TelemetryDisplayMode.DUAL_METRIC
                else -> TelemetryDisplayMode.AUTO
            }
}
