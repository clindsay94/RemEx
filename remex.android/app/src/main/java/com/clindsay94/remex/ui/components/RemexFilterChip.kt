package com.clindsay94.remex.ui.components

import androidx.compose.animation.animateColorAsState
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp

/**
 * An M3 [FilterChip] whose selected state eases in and out (3.0 motion pass, RemEx-wqo7a.8): the
 * container fills, the label and icons recolour and the outline fades on the motion scheme's
 * effects tier, instead of all of it switching in one frame as the stock chip does. Same colours
 * as the stock chip at rest, same size, same semantics.
 *
 * The unselected container and outline fade to the SAME hue at zero alpha, not to plain
 * transparent: transparent is black, and easing through it would dim the chip mid-way.
 */
@Composable
fun RemexFilterChip(
    selected: Boolean,
    onClick: () -> Unit,
    label: @Composable () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    leadingIcon: @Composable (() -> Unit)? = null,
    trailingIcon: @Composable (() -> Unit)? = null,
) {
    val defaults = FilterChipDefaults.filterChipColors()
    val spec = MaterialTheme.motionScheme.defaultEffectsSpec<Color>()
    val selectedContainer = defaults.selectedContainerColor
    val restingContainer = defaults.containerColor.takeUnless { it.alpha == 0f } ?: selectedContainer.copy(alpha = 0f)
    val container by animateColorAsState(if (selected) selectedContainer else restingContainer, spec, label = "chipContainer")
    val labelColor by
        animateColorAsState(if (selected) defaults.selectedLabelColor else defaults.labelColor, spec, label = "chipLabel")
    val leading by
        animateColorAsState(
            if (selected) defaults.selectedLeadingIconColor else defaults.leadingIconColor,
            spec,
            label = "chipLeadingIcon"
        )
    val trailing by
        animateColorAsState(
            if (selected) defaults.selectedTrailingIconColor else defaults.trailingIconColor,
            spec,
            label = "chipTrailingIcon"
        )
    // The stock chip draws a 1 dp outlineVariant border only while unselected.
    val outline = MaterialTheme.colorScheme.outlineVariant
    val borderColor by animateColorAsState(if (selected) outline.copy(alpha = 0f) else outline, spec, label = "chipBorder")

    FilterChip(
        selected = selected,
        onClick = onClick,
        label = label,
        modifier = modifier,
        enabled = enabled,
        leadingIcon = leadingIcon,
        trailingIcon = trailingIcon,
        colors =
            defaults.copy(
                containerColor = container,
                labelColor = labelColor,
                leadingIconColor = leading,
                trailingIconColor = trailing,
                selectedContainerColor = container,
                selectedLabelColor = labelColor,
                selectedLeadingIconColor = leading,
                selectedTrailingIconColor = trailing,
            ),
        border =
            FilterChipDefaults.filterChipBorder(
                enabled = enabled,
                selected = selected,
                borderColor = borderColor,
                selectedBorderColor = borderColor,
                borderWidth = 1.dp,
                selectedBorderWidth = 1.dp,
            ),
    )
}
