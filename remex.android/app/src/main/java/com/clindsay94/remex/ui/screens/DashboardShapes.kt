package com.clindsay94.remex.ui.screens

import com.clindsay94.remex.ui.theme.CardShapes

/**
 * Category-driven card shapes (locked decision #5). Resolution order at render is:
 * per-card/group override -> legacy coarse class preset -> built-in per-category default.
 * Kept as the single shape authority so the layout envelope, migration and picker never diverge.
 *
 * The Sensors grid (RemEx-wqo7a.7) draws one tile shape for every card, so it resolves with the
 * per-card override set aside; the override is still stored and still read by a downgraded APK.
 */
object DashboardShapes {
    const val SHAPE_PRESET_INHERIT = -1f // "no explicit shape - resolve from category default"
    const val LEGACY_CLOVER_INDEX = 18f // materialShapesList[18] = Clover4Leaf
    const val ROUNDED_RECTANGLE_INDEX = CardShapes.ROUNDED_RECTANGLE

    enum class CardCategory { PC_STATUS, CPU, GPU, RAM, TEMPERATURE, NETWORK, ACTION, OTHER }

    fun categoryOf(card: HomeCardState): CardCategory = when (card.type) {
        HomeCardType.PC_STATUS -> CardCategory.PC_STATUS
        HomeCardType.WAKE_ON_LAN -> CardCategory.ACTION
        HomeCardType.TELEMETRY -> classifyTelemetry(card.sensorId ?: card.id)
    }

    // Ordered so "temp" wins before "cpu"/"gpu" (sensor:cputemp -> TEMPERATURE, not CPU).
    private fun classifyTelemetry(rawKey: String): CardCategory {
        val k = rawKey.lowercase()
        return when {
            k.contains("temp") -> CardCategory.TEMPERATURE
            k.contains("net") || k.contains("mbps") || k.contains("bandwidth") || k.contains("through") ->
                CardCategory.NETWORK
            k.contains("cpu") -> CardCategory.CPU
            k.contains("gpu") -> CardCategory.GPU
            k.contains("ram") || k.contains("mem") -> CardCategory.RAM
            else -> CardCategory.OTHER
        }
    }

    /**
     * Every category defaults to the rounded rectangle (RemEx-kq10x.5, Connor 2026-10-02: "rounded
     * default, keep as option"). The per-category morph defaults (Gem for temperatures, Oval for RAM,
     * Pill for network...) clipped labels and values; the setting stays, the default is now safe.
     */
    @Suppress("UNUSED_PARAMETER") // kept so callers still state which category they resolve for
    fun defaultShapeFor(category: CardCategory): Float = CardShapes.ROUNDED_RECTANGLE

    /**
     * Resolves a card's shape, most specific setting first:
     *
     * 1. the per-card / group override,
     * 2. the user's per-CATEGORY choice ("all RAM cards"),
     * 3. the user's coarse CLASS preset ("all telemetry cards"),
     * 4. the built-in per-category default.
     *
     * Category beats class deliberately, because it is the narrower statement: a user who sets RAM
     * to Oval and telemetry to Square means the RAM cards to be Oval. Ordering these the other way
     * round would silently discard the more specific of the user's two choices.
     *
     * `ACTION` still never reads a class preset - it has no class slider - but it now has a
     * category one, which is what made it reachable at all.
     */
    fun resolveShapeIndex(
        card: HomeCardState,
        pcClassPreset: Float,
        telemetryClassPreset: Float,
        categoryPresets: Map<CardCategory, Float> = emptyMap(),
    ): Float = CardShapes.sanitize(resolveStoredShape(card, pcClassPreset, telemetryClassPreset, categoryPresets))

    // The raw winner of the precedence chain, before [CardShapes.sanitize] maps a removed shape
    // (a saved Gem, Clover, mid-morph 5.3...) to the rounded rectangle.
    private fun resolveStoredShape(
        card: HomeCardState,
        pcClassPreset: Float,
        telemetryClassPreset: Float,
        categoryPresets: Map<CardCategory, Float>,
    ): Float {
        if (card.shapePreset != SHAPE_PRESET_INHERIT) return card.shapePreset
        val category = categoryOf(card)

        val categoryOverride = categoryPresets[category] ?: SHAPE_PRESET_INHERIT
        if (categoryOverride != SHAPE_PRESET_INHERIT) return categoryOverride

        val classOverride = when (category) {
            CardCategory.PC_STATUS -> pcClassPreset
            CardCategory.CPU, CardCategory.GPU, CardCategory.RAM,
            CardCategory.TEMPERATURE, CardCategory.NETWORK, CardCategory.OTHER -> telemetryClassPreset
            CardCategory.ACTION -> SHAPE_PRESET_INHERIT
        }
        if (classOverride != SHAPE_PRESET_INHERIT) return classOverride

        return defaultShapeFor(category)
    }
}
