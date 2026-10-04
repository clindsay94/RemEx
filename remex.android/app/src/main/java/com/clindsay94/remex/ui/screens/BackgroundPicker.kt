package com.clindsay94.remex.ui.screens

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.selection.selectable
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.data.BackgroundIntensity
import com.clindsay94.remex.data.BackgroundStyles
import com.clindsay94.remex.ui.components.RemexHapticEvent
import com.clindsay94.remex.ui.components.RemexHaptics
import com.clindsay94.remex.ui.components.rememberRemexHaptics
import com.clindsay94.remex.ui.theme.BackgroundCanvas
import com.clindsay94.remex.ui.theme.opaqueBackgroundColor
import kotlin.math.roundToInt

/**
 * The body of Personalize -> Background (RemEx-pp4cm.17): a swatch for every style, each drawn by the
 * same renderer the app layer uses so what you see is what you get, and the intensity slider.
 * Animated swatches move under the same pause rules as the real layer.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun BackgroundPicker(
    style: String,
    intensity: Float,
    onStyleChange: (String) -> Unit,
    onIntensityChange: (Float) -> Unit,
) {
    val haptics = rememberRemexHaptics()

    GroupLabel(stringResource(R.string.personalization_background_textures))
    SwatchRow(
        styles = listOf(BackgroundStyles.None) + BackgroundStyles.Textures,
        selected = style,
        intensity = intensity,
        onSelect = {
            if (it != style) haptics.perform(RemexHapticEvent.ToggleOn)
            onStyleChange(it)
        },
    )

    GroupLabel(stringResource(R.string.personalization_background_animated))
    SwatchRow(
        styles = BackgroundStyles.Animated,
        selected = style,
        intensity = intensity,
        onSelect = {
            if (it != style) haptics.perform(RemexHapticEvent.ToggleOn)
            onStyleChange(it)
        },
    )

    if (BackgroundStyles.isVisible(style)) {
        // The label carries the value, so it is cleared for TalkBack and the slider announces both
        // (same pairing as the card-opacity slider, RemEx-porq / RemEx-qiz5).
        val percent = (intensity * 100).roundToInt()
        val sliderLabel = stringResource(R.string.cd_background_intensity)
        val percentText = stringResource(R.string.cd_percent_value, percent)
        Text(
            stringResource(R.string.personalization_background_intensity, percent),
            style = MaterialTheme.typography.labelMedium,
            modifier = Modifier.clearAndSetSemantics {},
        )
        Slider(
            value = intensity,
            // Detents at a quarter, half, three quarters and full, ticking as the thumb lands on one;
            // the same rule as the contrast slider.
            onValueChange = {
                val snapped = BackgroundIntensity.snap(it)
                if (RemexHaptics.landedOnDetent(intensity, snapped, BackgroundIntensity.DETENTS)) {
                    haptics.perform(RemexHapticEvent.Detent)
                }
                onIntensityChange(snapped)
            },
            onValueChangeFinished = { haptics.perform(RemexHapticEvent.Release) },
            valueRange = BackgroundIntensity.MIN..BackgroundIntensity.MAX,
            modifier = Modifier.semantics {
                contentDescription = sliderLabel
                stateDescription = percentText
            },
        )
        Text(
            stringResource(R.string.personalization_background_note),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

private const val SWATCHES_PER_ROW = 3

@Composable
private fun GroupLabel(text: String) {
    Text(text, style = MaterialTheme.typography.labelMedium)
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun SwatchRow(
    styles: List<String>,
    selected: String,
    intensity: Float,
    onSelect: (String) -> Unit,
) {
    FlowRow(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
        maxItemsInEachRow = SWATCHES_PER_ROW,
    ) {
        styles.forEach { option ->
            Swatch(
                style = option,
                label = backgroundStyleLabel(option),
                selected = option == selected,
                intensity = intensity,
                onClick = { onSelect(option) },
                modifier = Modifier.weight(1f),
            )
        }
        // Fill the last row so a lone swatch stays the size of the others instead of stretching.
        repeat((SWATCHES_PER_ROW - styles.size % SWATCHES_PER_ROW) % SWATCHES_PER_ROW) {
            Spacer(Modifier.weight(1f))
        }
    }
}

@Composable
private fun Swatch(
    style: String,
    label: String,
    selected: Boolean,
    intensity: Float,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val shape = MaterialTheme.shapes.medium
    val ring =
        if (selected) BorderStroke(3.dp, MaterialTheme.colorScheme.primary)
        else BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant)
    Column(
        modifier = modifier.selectable(selected = selected, onClick = onClick, role = Role.RadioButton),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Box(
            Modifier.fillMaxWidth().aspectRatio(4f / 3f).clip(shape).border(ring, shape),
            contentAlignment = Alignment.Center,
        ) {
            if (style == BackgroundStyles.None) {
                // Nothing to draw: the plain background, so "None" reads as exactly that.
                Box(Modifier.fillMaxSize().background(opaqueBackgroundColor()))
            } else {
                // A still preview of a texture is its real tile; an animated one really moves.
                BackgroundCanvas(style, intensity, covered = false, modifier = Modifier.fillMaxSize())
            }
        }
        Text(
            label,
            style = MaterialTheme.typography.labelMedium,
            textAlign = TextAlign.Center,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(horizontal = 2.dp),
        )
    }
}

/** The picker's label for [style]. */
@Composable
internal fun backgroundStyleLabel(style: String): String =
    when (style) {
        BackgroundStyles.Grain -> stringResource(R.string.personalization_background_grain)
        BackgroundStyles.Dots -> stringResource(R.string.personalization_background_dots)
        BackgroundStyles.Grid -> stringResource(R.string.personalization_background_grid)
        BackgroundStyles.Topographic -> stringResource(R.string.personalization_background_topographic)
        BackgroundStyles.Hexagons -> stringResource(R.string.personalization_background_hexagons)
        BackgroundStyles.Carbon -> stringResource(R.string.personalization_background_carbon)
        BackgroundStyles.Aurora -> stringResource(R.string.personalization_background_aurora)
        BackgroundStyles.Mesh -> stringResource(R.string.personalization_background_mesh)
        BackgroundStyles.Starfield -> stringResource(R.string.personalization_background_starfield)
        else -> stringResource(R.string.personalization_background_none)
    }
