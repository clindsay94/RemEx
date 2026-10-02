package com.clindsay94.remex.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.SheetValue
import androidx.compose.material3.Text
import androidx.compose.material3.rememberBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import android.view.HapticFeedbackConstants
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.theme.CardShapes
import com.clindsay94.remex.ui.theme.cardShape

/**
 * Category-driven card shapes (locked decision #5). Resolution order at render is:
 * per-card/group override -> legacy coarse class preset -> built-in per-category default.
 * Kept as the single shape authority so the layout envelope, migration and picker never diverge.
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


@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ShapePickerSheet(
    cornerRadiusDp: Int,
    onDismiss: () -> Unit,
    onPick: (Float) -> Unit
) {
    val view = LocalView.current
    val sheetState = rememberBottomSheetState(SheetValue.Hidden)

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState) {
        Text(
            stringResource(R.string.shape_picker_title),
            style = MaterialTheme.typography.titleMedium,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
            textAlign = TextAlign.Start
        )
        LazyVerticalGrid(
            columns = GridCells.Adaptive(72.dp),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
            modifier = Modifier.fillMaxWidth().padding(16.dp)
        ) {
            item {
                ShapePickerCell(
                    label = stringResource(R.string.personalization_shape_auto),
                    shapeIndex = null,
                    cornerRadiusDp = cornerRadiusDp
                ) {
                    view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                    onPick(DashboardShapes.SHAPE_PRESET_INHERIT)
                }
            }
            // Only the content-safe shapes (RemEx-kq10x.5) - see CardShapes.
            items(CardShapes.options) { index ->
                ShapePickerCell(
                    label = stringResource(CardShapes.nameRes(index)),
                    shapeIndex = index,
                    cornerRadiusDp = cornerRadiusDp
                ) {
                    view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                    onPick(index)
                }
            }
        }
    }
}

@Composable
private fun ShapePickerCell(
    label: String,
    shapeIndex: Float?,
    cornerRadiusDp: Int,
    onClick: () -> Unit
) {
    Column(
        modifier = Modifier.clickable(onClick = onClick),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Box(
            modifier = Modifier.fillMaxWidth().aspectRatio(1f),
            contentAlignment = Alignment.Center
        ) {
            val shape = if (shapeIndex == null) CircleShape else cardShape(shapeIndex, cornerRadiusDp)
            Box(
                modifier = Modifier.size(44.dp)
                    .clip(shape)
                    .background(MaterialTheme.colorScheme.primary)
            )
        }
        Text(
            label,
            style = MaterialTheme.typography.labelSmall,
            textAlign = TextAlign.Center,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
    }
}
