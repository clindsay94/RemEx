package com.clindsay94.remex.ui.screens.sensors

import android.view.HapticFeedbackConstants
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.DeleteOutline
import androidx.compose.material.icons.filled.OpenInFull
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material.icons.outlined.PushPin
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.FilledTonalIconToggleButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R

/**
 * A fading [AnimatedVisibility] called from top level, so the Card's ColumnScope receiver does not
 * capture it (the scoped overload cannot be called with an implicit receiver there).
 */
@Composable
private fun FadeVisibility(visible: Boolean, modifier: Modifier, content: @Composable () -> Unit) {
    AnimatedVisibility(
            visible = visible,
            modifier = modifier,
            enter = fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
            exit = fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
    ) { content() }
}

/** Edit mode's Pin to Home control for one card. */
data class CardPinControl(
        /** The sensor is on the PC's Home (or the phone's own list for an older PC). */
        val pinned: Boolean,
        /** It can be pinned: the PC has a card for it, or the PC is too old to keep the list. */
        val canPin: Boolean,
)

/**
 * One Sensors grid tile (RemEx-wqo7a.7 / .8): the same rounded tile for every card, tonal
 * surfaceContainer fill, the sensor's family accent left to [content]'s label chip, value and graph.
 *
 * In edit mode the content dims and three controls sit on top of it: Pin to Home, Change size (the
 * 1x1 -> 2x1 -> 2x2 cycle) and Remove. Outside edit mode there are no handles at all; the card's own
 * view picker (inside [content]) is the only control.
 *
 * Pin to Home for a sensor the PC cannot pin is shown dimmed but still answers a tap, with
 * [onPinUnavailable] explaining what to do: a truly disabled button would say nothing at all.
 */
@Composable
fun SensorGridCard(
        shape: Shape,
        cardOpacity: Float,
        editMode: Boolean,
        isDragging: Boolean,
        pin: CardPinControl?,
        onTogglePin: () -> Unit,
        onPinUnavailable: () -> Unit,
        onCycleSize: () -> Unit,
        onRemove: () -> Unit,
        modifier: Modifier = Modifier,
        content: @Composable () -> Unit,
) {
    val view = LocalView.current
    val borderWidth by
            animateDpAsState(
                    targetValue = if (isDragging) 2.dp else 1.dp,
                    animationSpec = MaterialTheme.motionScheme.defaultEffectsSpec(),
                    label = "gridCardBorderWidth"
            )
    val borderColor by
            animateColorAsState(
                    targetValue = if (isDragging) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outlineVariant,
                    animationSpec = MaterialTheme.motionScheme.defaultEffectsSpec(),
                    label = "gridCardBorderColor"
            )
    val elevation by
            animateDpAsState(
                    targetValue = if (isDragging) 8.dp else 1.dp,
                    animationSpec = MaterialTheme.motionScheme.fastSpatialSpec(),
                    label = "gridCardElevation"
            )
    val liftScale by
            animateFloatAsState(
                    targetValue = if (isDragging) 1.03f else 1f,
                    animationSpec = MaterialTheme.motionScheme.fastSpatialSpec(),
                    label = "gridCardLift"
            )
    val contentAlpha by
            animateFloatAsState(
                    targetValue = if (editMode) 0.35f else 1f,
                    animationSpec = MaterialTheme.motionScheme.defaultEffectsSpec(),
                    label = "gridCardContentAlpha"
            )

    Card(
            modifier = modifier.graphicsLayer { scaleX = liftScale; scaleY = liftScale },
            shape = shape,
            colors =
                    CardDefaults.cardColors(
                            // Tonal fill, not a family colour: the sensor accent lives on the label
                            // chip, the value and the graph (SensorAccents), never on the card body.
                            containerColor = MaterialTheme.colorScheme.surfaceContainer.copy(alpha = cardOpacity),
                            contentColor = MaterialTheme.colorScheme.onSurface
                    ),
            border = BorderStroke(borderWidth, borderColor),
            elevation = CardDefaults.cardElevation(defaultElevation = elevation)
    ) {
        Box(modifier = Modifier.fillMaxSize().padding(4.dp)) {
            Box(modifier = Modifier.fillMaxSize().graphicsLayer { alpha = contentAlpha }) { content() }

            FadeVisibility(visible = editMode, modifier = Modifier.align(Alignment.Center)) {
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp), verticalAlignment = Alignment.CenterVertically) {
                    if (pin != null) {
                        val pinLabel =
                                stringResource(
                                        when {
                                            pin.pinned -> R.string.dashboard_unpin_from_home
                                            pin.canPin -> R.string.dashboard_pin_to_home
                                            else -> R.string.dashboard_pin_unavailable
                                        }
                                )
                        val pinnedState = stringResource(R.string.dashboard_pinned_to_home)
                        FilledTonalIconToggleButton(
                                checked = pin.pinned,
                                onCheckedChange = {
                                    view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                                    if (pin.pinned || pin.canPin) onTogglePin() else onPinUnavailable()
                                },
                                modifier =
                                        Modifier.graphicsLayer { alpha = if (pin.pinned || pin.canPin) 1f else 0.38f }
                                                .semantics { if (pin.pinned) stateDescription = pinnedState }
                        ) {
                            Icon(
                                    if (pin.pinned) Icons.Filled.PushPin else Icons.Outlined.PushPin,
                                    contentDescription = pinLabel
                            )
                        }
                    }
                    FilledTonalIconButton(onClick = {
                        view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                        onCycleSize()
                    }) {
                        Icon(Icons.Default.OpenInFull, contentDescription = stringResource(R.string.dashboard_resize_card))
                    }
                    FilledTonalIconButton(onClick = {
                        view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                        onRemove()
                    }) {
                        Icon(Icons.Default.DeleteOutline, contentDescription = stringResource(R.string.dashboard_remove_card))
                    }
                }
            }
        }
    }
}
