package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.drag
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Category
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.DeleteOutline
import androidx.compose.material.icons.filled.OpenInFull
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.scale
import androidx.compose.ui.input.pointer.AwaitPointerEventScope
import androidx.compose.ui.input.pointer.positionChange
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.components.hapticCommandAcknowledged
import com.clindsay94.remex.ui.components.hapticLift
import com.clindsay94.remex.ui.components.hapticSelectToggle
import com.clindsay94.remex.ui.theme.cardShape
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Wraps one dashboard card's gesture handling + selection affordance (two-layer touch model,
 * spec section 4 as refined). Extracted from the inline per-card block.
 *
 * NORMAL (no selection): a quick drag anywhere falls through to the parent's pan/zoom; holding a
 * card ~0.5s "picks it up" (haptic). From the picked-up state, dragging MOVES the card (drop on
 * release -> back to normal); releasing in place SELECTS it and shows the action bar.
 * SELECTION (selectionActive): a tap toggles this card in/out of the group; dragging a SELECTED
 * card moves the whole group. Pin badge / resize handle / view-picker button are this composable's
 * own direct children (not [content]) so they can be hidden uniformly while selectionActive.
 */
@Composable
fun DraggableDashboardCard(
    card: HomeCardState,
    selectionActive: Boolean,
    isSelected: Boolean,
    isDragging: Boolean,
    layoutLocked: Boolean,
    density: Float,
    cornerRadius: Int,
    cardOpacity: Float,
    shapeIndex: Float,
    canvasScale: () -> Float,
    onBeginCardDrag: (String) -> Unit,
    onDragCardBy: (Float, Float) -> Unit,
    onEndCardDrag: () -> Unit,
    onSelectCard: (String) -> Unit,
    onToggleSelect: (String) -> Unit,
    onBeginInteraction: () -> Unit,
    onMoveSelection: (Float, Float) -> Unit,
    onSaveLayout: () -> Unit,
    onTogglePin: (String) -> Unit,
    onResize: (String, Float, Float) -> Unit,
    modifier: Modifier = Modifier,
    content: @Composable () -> Unit
) {
    val view = LocalView.current

    val gestureModifier = if (selectionActive) {
        Modifier
            .pointerInput(card.id, isSelected) {
                // Dragging a SELECTED card moves the whole group; unselected cards fall through to pan.
                if (isSelected) {
                    detectDragGestures(
                        onDragStart = { onBeginInteraction() },
                        onDrag = { change, dragAmount ->
                            change.consume()
                            val s = canvasScale()
                            onMoveSelection(dragAmount.x / (density * s), dragAmount.y / (density * s))
                        },
                        onDragEnd = { onSaveLayout() }
                    )
                }
            }
            .pointerInput(card.id) {
                detectTapGestures {
                    view.hapticSelectToggle()
                    onToggleSelect(card.id)
                }
            }
    } else {
        Modifier.pointerInput(card.id, layoutLocked) {
            if (layoutLocked) return@pointerInput
            awaitEachGesture {
                detectPickUpGesture(
                    touchSlop = viewConfiguration.touchSlop,
                    longPressMs = viewConfiguration.longPressTimeoutMillis,
                    canvasScale = canvasScale,
                    density = density,
                    onArm = { view.hapticLift(); onBeginCardDrag(card.id) },
                    onMove = { dx, dy -> onDragCardBy(dx, dy) },
                    onDrop = { onEndCardDrag() },
                    onSelect = { onSelectCard(card.id) }
                )
            }
        }
    }

    Card(
        modifier = modifier.then(gestureModifier),
        shape = cardShape(shapeIndex, cornerRadius),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.primaryContainer.copy(
                alpha = if (selectionActive && !isSelected) cardOpacity * 0.6f else cardOpacity
            ),
            contentColor = MaterialTheme.colorScheme.onPrimaryContainer
        ),
        border = if (isSelected) BorderStroke(2.dp, MaterialTheme.colorScheme.primary) else null,
        elevation = CardDefaults.cardElevation(defaultElevation = if (isDragging) 8.dp else 1.dp)
    ) {
        Box(
            modifier = Modifier.fillMaxSize()
                .then(if (isDragging) Modifier.scale(1.03f) else Modifier)
                .padding(4.dp)
        ) {
            content()

            if (!selectionActive) {
                // Per-card pin toggle — pinned cards are position/resize-anchored but still liftable.
                Surface(
                    shape = CircleShape,
                    color = if (card.pinned) MaterialTheme.colorScheme.primary
                            else MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.85f),
                    tonalElevation = 2.dp,
                    onClick = {
                        view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                        onTogglePin(card.id)
                    },
                    modifier = Modifier.align(Alignment.BottomStart).padding(12.dp).size(24.dp)
                ) {
                    Icon(
                        Icons.Default.PushPin,
                        contentDescription = stringResource(
                            if (card.pinned) R.string.dashboard_unpin_card else R.string.dashboard_pin_card
                        ),
                        tint = if (card.pinned) MaterialTheme.colorScheme.onPrimary
                               else MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.size(14.dp).padding(1.dp)
                    )
                }

                Surface(
                    shape = CircleShape,
                    color = MaterialTheme.colorScheme.primary,
                    tonalElevation = 2.dp,
                    modifier = Modifier.align(Alignment.BottomEnd).padding(12.dp).size(24.dp)
                        .pointerInput("resize_${card.id}", layoutLocked, card.pinned) {
                            if (layoutLocked || card.pinned) return@pointerInput
                            detectDragGestures(
                                onDragStart = { onBeginInteraction() },
                                onDrag = { change, dragAmount ->
                                    change.consume()
                                    val s = canvasScale()
                                    onResize(card.id, dragAmount.x / (density * s), dragAmount.y / (density * s))
                                },
                                onDragEnd = { onSaveLayout() }
                            )
                        }
                ) {
                    Icon(
                        Icons.Default.OpenInFull,
                        contentDescription = stringResource(R.string.cd_resize_cards),
                        tint = MaterialTheme.colorScheme.onPrimary,
                        modifier = Modifier.size(12.dp).padding(2.dp)
                    )
                }
            }

            if (selectionActive && isSelected) {
                Surface(
                    shape = CircleShape,
                    color = MaterialTheme.colorScheme.primary,
                    tonalElevation = 2.dp,
                    modifier = Modifier.align(Alignment.TopStart).padding(8.dp).size(20.dp)
                ) {
                    Icon(
                        Icons.Default.Check,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.onPrimary,
                        modifier = Modifier.size(14.dp).padding(3.dp)
                    )
                }
            }
        }
    }
}

/**
 * The two-layer pick-up detector for one down->up gesture in NORMAL mode.
 *
 * 1. Waits [longPressMs] for the finger to sit still. Releasing or moving beyond [touchSlop] first
 *    resolves the gesture WITHOUT consuming (so the parent pans / a child button click lands).
 * 2. Once armed ([onArm]), branches: the first move beyond slop -> MOVE (drag until release, then
 *    [onDrop]); a release with no move -> [onSelect].
 */
private suspend fun AwaitPointerEventScope.detectPickUpGesture(
    touchSlop: Float,
    longPressMs: Long,
    canvasScale: () -> Float,
    density: Float,
    onArm: () -> Unit,
    onMove: (Float, Float) -> Unit,
    onDrop: () -> Unit,
    onSelect: () -> Unit
) {
    val down = awaitFirstDown(requireUnconsumed = false)

    // Layer 1 — resolves early (returns true) on release/move; times out (null) once armed.
    val resolvedEarly = withTimeoutOrNull(longPressMs) {
        while (true) {
            val event = awaitPointerEvent()
            val change = event.changes.firstOrNull { it.id == down.id } ?: continue
            if (!change.pressed) return@withTimeoutOrNull true
            if ((change.position - down.position).getDistance() > touchSlop) return@withTimeoutOrNull true
        }
        @Suppress("UNREACHABLE_CODE")
        true
    } ?: false
    if (resolvedEarly) return

    // Armed: haptic + visual pick-up.
    onArm()

    // Layer 2 — first move => MOVE, release in place => SELECT.
    var moved = false
    while (true) {
        val event = awaitPointerEvent()
        val change = event.changes.firstOrNull { it.id == down.id } ?: continue
        if (!change.pressed) break
        if ((change.position - down.position).getDistance() > touchSlop) {
            moved = true
            break
        }
    }

    if (moved) {
        drag(down.id) { change ->
            val delta = change.positionChange()
            val s = canvasScale()
            onMove(delta.x / (density * s), delta.y / (density * s))
            change.consume()
        }
        onDrop()
    } else {
        onSelect()
    }
}

/** Top-center pill shown while a selection is active - spec section 4.4. */
@Composable
fun DashboardSelectionActionBar(
    selectionCount: Int,
    allPinned: Boolean,
    onTogglePin: () -> Unit,
    onReshape: () -> Unit,
    onRemove: () -> Unit,
    onDone: () -> Unit,
    modifier: Modifier = Modifier
) {
    val view = LocalView.current
    Surface(
        shape = RoundedCornerShape(28.dp),
        color = MaterialTheme.colorScheme.surfaceContainerHigh,
        tonalElevation = 6.dp,
        modifier = modifier
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                pluralStringResource(R.plurals.dashboard_selection_count, selectionCount, selectionCount),
                style = MaterialTheme.typography.labelLarge,
                color = MaterialTheme.colorScheme.onSurface,
                modifier = Modifier.padding(end = 8.dp)
            )
            FilledTonalIconButton(onClick = { view.hapticCommandAcknowledged(); onTogglePin() }, modifier = Modifier.size(44.dp)) {
                Icon(
                    Icons.Default.PushPin,
                    contentDescription = stringResource(
                        if (allPinned) R.string.cd_dashboard_unpin_selection else R.string.cd_dashboard_pin_selection
                    )
                )
            }
            Spacer(Modifier.width(4.dp))
            FilledTonalIconButton(onClick = onReshape, modifier = Modifier.size(44.dp)) {
                Icon(Icons.Default.Category, contentDescription = stringResource(R.string.cd_dashboard_reshape_selection))
            }
            Spacer(Modifier.width(4.dp))
            FilledTonalIconButton(onClick = { view.hapticCommandAcknowledged(); onRemove() }, modifier = Modifier.size(44.dp)) {
                Icon(Icons.Default.DeleteOutline, contentDescription = stringResource(R.string.cd_dashboard_remove_selection))
            }
            Spacer(Modifier.width(4.dp))
            FilledTonalIconButton(
                onClick = { view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP); onDone() },
                modifier = Modifier.size(44.dp)
            ) {
                Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cd_dashboard_exit_selection))
            }
        }
    }
}
