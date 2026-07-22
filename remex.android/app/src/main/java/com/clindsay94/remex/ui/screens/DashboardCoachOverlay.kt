package com.clindsay94.remex.ui.screens

import androidx.compose.animation.core.Animatable
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Category
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.GridView
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material.icons.filled.TouchApp
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.roundToInt

/**
 * First-run coaching for Home Base (RemEx-km0i.10). Android-native and motion-first: the star is an
 * animated hold-to-lift demonstration (the one gesture with no visual affordance), followed by two
 * light directional callouts pulsing directly over the real ⋮ menu and + button.
 *
 * Physics comes from the M3 **Expressive** [MotionScheme] spring tokens — bouncy `spatial` specs for
 * anything that moves or scales, gentler `effects` specs for alpha/scrim. The looping demo is driven
 * by [Animatable]s in a coroutine so each phase is a real spring, and the "finger returns + card pops"
 * phases run in parallel via [coroutineScope] so they land together.
 *
 * Purely presentational: the caller owns visibility/step state, the suppression guards, and supplies
 * the live on-screen positions of the ⋮ menu / + button (captured via onGloballyPositioned) so the
 * pointers land on the real controls regardless of device insets.
 *
 * @param step 0..[DASHBOARD_COACH_HINT_COUNT]-1 — which hint to render.
 * @param menuAnchor root-space center of the ⋮ menu button (Offset.Zero until laid out).
 * @param addAnchor root-space center of the + button (Offset.Zero until laid out).
 * @param onAdvance advance to the next hint (the last step's button finishes + persists).
 * @param onDismiss skip the whole sequence now.
 */
@Composable
fun DashboardCoachOverlay(
    step: Int,
    menuAnchor: Offset,
    addAnchor: Offset,
    onAdvance: () -> Unit,
    onDismiss: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val isLast = step >= DASHBOARD_COACH_HINT_COUNT - 1

    Box(
        modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.scrim.copy(alpha = 0.62f))
            // Swallow all touches so the canvas underneath can't be interacted with mid-hint; a scrim
            // tap intentionally does NOT dismiss (avoids losing the tutorial to a stray tap).
            .pointerInput(Unit) { detectTapGestures {} },
    ) {
        when (step) {
            0 -> HoldToLiftDemo(Modifier.align(Alignment.Center))
            1 -> DirectionalPointer(anchor = menuAnchor, haloSize = 48.dp)
            2 -> DirectionalPointer(anchor = addAnchor, haloSize = 84.dp)
            3 -> ViewPickerDemo(Modifier.align(Alignment.Center))
            4 -> GroupSelectDemo(Modifier.align(Alignment.Center))
            5 -> SelectActionBarDemo(Modifier.align(Alignment.Center))
        }

        CoachPanel(
            body = when (step) {
                0 -> stringResource(R.string.coach_hold_body)
                1 -> stringResource(R.string.coach_menu_body)
                2 -> stringResource(R.string.coach_add_body)
                3 -> stringResource(R.string.coach_view_body)
                4 -> stringResource(R.string.coach_group_body)
                else -> stringResource(R.string.coach_select_body)
            },
            isLast = isLast,
            onAdvance = onAdvance,
            onDismiss = onDismiss,
            // Place each caption near what its hint points at: step 0 just below the centred animation,
            // step 1 up by the ⋮ menu, step 2 low but lifted clear of the + FAB.
            modifier = when (step) {
                1 -> Modifier.align(Alignment.TopCenter).statusBarsPadding().padding(top = 64.dp)
                2 -> Modifier.align(Alignment.BottomCenter).padding(bottom = 100.dp)
                else -> Modifier.align(Alignment.Center).offset(y = 162.dp)  // centred animated demos
            }.padding(horizontal = 20.dp, vertical = 28.dp),
        )
    }
}

/**
 * Animated hold-to-lift demonstration. The finger rests **on** the card but slightly enlarged, then
 * **shrinks to "grab"** it (with a ripple bloom), then **returns to normal size as the card expands
 * simultaneously** (the lift), then both **drag** sideways — looped forever with expressive springs.
 */
@Composable
private fun HoldToLiftDemo(modifier: Modifier = Modifier) {
    val motion = MaterialTheme.motionScheme
    val fingerScale = remember { Animatable(1.18f) }  // rest: in position, slightly larger
    val ripple = remember { Animatable(0f) }          // 0 → 1 press ripple bloom
    val cardLift = remember { Animatable(0f) }         // 0 resting → 1 lifted (scale + elevation)
    val dragX = remember { Animatable(0f) }            // 0 centered → 1 nudged right (drag hint)

    LaunchedEffect(Unit) {
        while (true) {
            fingerScale.snapTo(1.18f); ripple.snapTo(0f); cardLift.snapTo(0f); dragX.snapTo(0f)
            delay(520)
            // PRESS — finger shrinks down to grab, ripple blooms (together).
            coroutineScope {
                launch { fingerScale.animateTo(0.76f, motion.defaultSpatialSpec()) }
                launch { ripple.animateTo(1f, motion.slowEffectsSpec()) }
            }
            delay(110)
            // LIFT — finger springs back to normal AND the card pops up, simultaneously.
            coroutineScope {
                launch { fingerScale.animateTo(1f, motion.fastSpatialSpec()) }
                launch { cardLift.animateTo(1f, motion.fastSpatialSpec()) }
            }
            delay(170)
            dragX.animateTo(1f, motion.defaultSpatialSpec())   // drag sideways
            delay(520)
            // RELEASE — everything settles back to rest.
            coroutineScope {
                launch { ripple.animateTo(0f, motion.defaultEffectsSpec()) }
                launch { cardLift.animateTo(0f, motion.defaultSpatialSpec()) }
                launch { dragX.animateTo(0f, motion.defaultSpatialSpec()) }
                launch { fingerScale.animateTo(1.18f, motion.defaultSpatialSpec()) }
            }
            delay(440)
        }
    }

    Box(modifier.size(200.dp), contentAlignment = Alignment.Center) {
        val nudge = 34.dp

        // Ripple ring that blooms out of the card during the "grab".
        Box(
            Modifier
                .size(96.dp)
                .graphicsLayer {
                    val s = 0.5f + ripple.value * 1.0f
                    scaleX = s; scaleY = s
                    translationX = dragX.value * nudge.toPx()
                    alpha = (1f - ripple.value) * 0.55f
                }
                .border(3.dp, MaterialTheme.colorScheme.primary, CircleShape),
        )

        // The sample card being lifted. (Shape captured here: graphicsLayer lambdas run
        // outside composition and cannot read MaterialTheme themselves.)
        val liftedCardShape = MaterialTheme.shapes.large
        Box(
            Modifier
                .size(120.dp, 84.dp)
                .graphicsLayer {
                    val s = 1f + cardLift.value * 0.16f
                    scaleX = s; scaleY = s
                    translationX = dragX.value * nudge.toPx()
                    shadowElevation = cardLift.value * 28f
                    shape = liftedCardShape
                    clip = true
                }
                .background(MaterialTheme.colorScheme.primaryContainer, liftedCardShape),
        )

        // The finger, resting on the card — scales big → small (grab) → normal, and rides the drag.
        Icon(
            imageVector = Icons.Filled.TouchApp,
            contentDescription = null,
            tint = MaterialTheme.colorScheme.onPrimaryContainer,
            modifier = Modifier
                .align(Alignment.Center)
                .size(46.dp)
                .graphicsLayer {
                    translationX = dragX.value * nudge.toPx()
                    translationY = 10.dp.toPx()   // sit slightly low, planted on the card
                    scaleX = fingerScale.value; scaleY = fingerScale.value
                },
        )
    }
}

/**
 * Animated view-picker demonstration: a finger taps the ⊞ grid icon on a sample card, and a little
 * view-picker grid pops up out of that corner (bouncy) — teaching how to change a card's look.
 */
@Composable
private fun ViewPickerDemo(modifier: Modifier = Modifier) {
    val motion = MaterialTheme.motionScheme
    val fingerScale = remember { Animatable(1.15f) }
    val gridPop = remember { Animatable(0f) }   // 0 hidden → 1 picker fully popped

    LaunchedEffect(Unit) {
        while (true) {
            fingerScale.snapTo(1.15f); gridPop.snapTo(0f)
            delay(520)
            fingerScale.animateTo(0.72f, motion.defaultSpatialSpec())   // tap the ⊞ icon
            coroutineScope {
                launch { fingerScale.animateTo(1f, motion.fastSpatialSpec()) }
                launch { gridPop.animateTo(1f, motion.fastSpatialSpec()) }   // picker pops in
            }
            delay(950)
            coroutineScope {
                launch { gridPop.animateTo(0f, motion.defaultSpatialSpec()) }
                launch { fingerScale.animateTo(1.15f, motion.defaultSpatialSpec()) }
            }
            delay(400)
        }
    }

    Box(modifier.size(200.dp), contentAlignment = Alignment.Center) {
        // Sample card (upper) with a ⊞ view icon in its top-right corner.
        Box(
            Modifier
                .align(Alignment.Center)
                .offset(y = (-44).dp)
                .size(120.dp, 74.dp)
                .background(MaterialTheme.colorScheme.primaryContainer, MaterialTheme.shapes.large),
        ) {
            Icon(
                Icons.Filled.GridView,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onPrimaryContainer,
                modifier = Modifier.align(Alignment.TopEnd).padding(8.dp).size(20.dp),
            )
        }

        // The finger tapping the ⊞ icon at the card's top-right.
        Icon(
            imageVector = Icons.Filled.TouchApp,
            contentDescription = null,
            tint = MaterialTheme.colorScheme.onPrimaryContainer,
            modifier = Modifier
                .align(Alignment.Center)
                .offset(x = 44.dp, y = (-56).dp)
                .size(38.dp)
                .graphicsLayer { scaleX = fingerScale.value; scaleY = fingerScale.value },
        )

        // The view-picker grid popping in just below the card.
        Box(
            Modifier
                .align(Alignment.Center)
                .offset(y = 34.dp)
                .graphicsLayer {
                    val s = gridPop.value
                    scaleX = s; scaleY = s
                    alpha = s
                    transformOrigin = TransformOrigin(0.5f, 0f)
                }
                .background(MaterialTheme.colorScheme.surface, MaterialTheme.shapes.large)
                .padding(10.dp),
        ) {
            Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                repeat(2) {
                    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        repeat(3) {
                            Box(
                                Modifier
                                    .size(20.dp)
                                    .background(
                                        MaterialTheme.colorScheme.primary,
                                        MaterialTheme.shapes.small,
                                    ),
                            )
                        }
                    }
                }
            }
        }
    }
}

/**
 * Animated multi-select demonstration: a finger taps two sample cards (each springs to a selected
 * highlight), then the whole group lifts and drags together — teaching group selection + move.
 */
@Composable
private fun GroupSelectDemo(modifier: Modifier = Modifier) {
    val motion = MaterialTheme.motionScheme
    val fingerX = remember { Animatable(0f) }        // 0 over card A → 1 over card B
    val fingerScale = remember { Animatable(1.1f) }
    val selA = remember { Animatable(0f) }           // selection highlight per card
    val selB = remember { Animatable(0f) }
    val lift = remember { Animatable(0f) }           // group lift (scale + elevation)
    val dragX = remember { Animatable(0f) }          // group drag

    LaunchedEffect(Unit) {
        while (true) {
            fingerX.snapTo(0f); fingerScale.snapTo(1.1f)
            selA.snapTo(0f); selB.snapTo(0f); lift.snapTo(0f); dragX.snapTo(0f)
            delay(460)
            // Tap card A → select.
            fingerScale.animateTo(0.74f, motion.defaultSpatialSpec())
            coroutineScope {
                launch { fingerScale.animateTo(1.1f, motion.fastSpatialSpec()) }
                launch { selA.animateTo(1f, motion.fastSpatialSpec()) }
            }
            delay(130)
            fingerX.animateTo(1f, motion.defaultSpatialSpec())   // slide to card B
            // Tap card B → select.
            fingerScale.animateTo(0.74f, motion.defaultSpatialSpec())
            coroutineScope {
                launch { fingerScale.animateTo(1.1f, motion.fastSpatialSpec()) }
                launch { selB.animateTo(1f, motion.fastSpatialSpec()) }
            }
            delay(160)
            // Lift the group and drag both together.
            lift.animateTo(1f, motion.fastSpatialSpec())
            dragX.animateTo(1f, motion.defaultSpatialSpec())
            delay(480)
            coroutineScope {
                launch { lift.animateTo(0f, motion.defaultSpatialSpec()) }
                launch { dragX.animateTo(0f, motion.defaultSpatialSpec()) }
                launch { selA.animateTo(0f, motion.defaultEffectsSpec()) }
                launch { selB.animateTo(0f, motion.defaultEffectsSpec()) }
            }
            delay(360)
        }
    }

    Box(modifier.size(200.dp), contentAlignment = Alignment.Center) {
        val cardW = 62.dp
        val cardH = 56.dp
        val halfSpread = 39.dp    // distance of each card's centre from box centre
        val groupDrag = 24.dp

        // Card A (left) and Card B (right) — both ride the shared lift + drag; each shows its own
        // selection ring fading in.
        MiniSelectCard(cardW, cardH, -halfSpread, selA.value, lift.value, dragX.value * 1f, groupDrag)
        MiniSelectCard(cardW, cardH, halfSpread, selB.value, lift.value, dragX.value * 1f, groupDrag)

        // The finger, sliding from card A to card B and tapping each.
        Icon(
            imageVector = Icons.Filled.TouchApp,
            contentDescription = null,
            tint = MaterialTheme.colorScheme.onPrimaryContainer,
            modifier = Modifier
                .align(Alignment.Center)
                .size(40.dp)
                .graphicsLayer {
                    val spread = halfSpread.toPx()
                    translationX = (-spread + fingerX.value * 2f * spread) + dragX.value * groupDrag.toPx()
                    translationY = 6.dp.toPx()
                    scaleX = fingerScale.value; scaleY = fingerScale.value
                },
        )
    }
}

/** One selectable mini-card for [GroupSelectDemo]: positioned by [centreOffsetX] from the box centre,
 *  scaling with the shared [lift], sliding with the shared [dragFraction], and fading in a selection
 *  ring by [selected] (0..1). */
@Composable
private fun androidx.compose.foundation.layout.BoxScope.MiniSelectCard(
    width: androidx.compose.ui.unit.Dp,
    height: androidx.compose.ui.unit.Dp,
    centreOffsetX: androidx.compose.ui.unit.Dp,
    selected: Float,
    lift: Float,
    dragFraction: Float,
    dragDistance: androidx.compose.ui.unit.Dp,
) {
    val cardShape = MaterialTheme.shapes.large
    Box(
        Modifier
            .align(Alignment.Center)
            .offset(x = centreOffsetX)
            .size(width, height)
            .graphicsLayer {
                val s = 1f + lift * 0.12f
                scaleX = s; scaleY = s
                translationX = dragFraction * dragDistance.toPx()
                shadowElevation = lift * 20f
                shape = cardShape
                clip = true
            }
            .background(MaterialTheme.colorScheme.primaryContainer, cardShape)
            .border(
                width = 3.dp,
                color = MaterialTheme.colorScheme.primary.copy(alpha = selected),
                shape = cardShape,
            ),
    )
}

/**
 * Animated select-to-act demonstration: a finger holds a card until it selects (highlight ring), then
 * the action bar pops up with reshape / pin / remove — the other branch of the hold gesture (hold +
 * release → select, vs. hold + drag → move).
 */
@Composable
private fun SelectActionBarDemo(modifier: Modifier = Modifier) {
    val motion = MaterialTheme.motionScheme
    val fingerScale = remember { Animatable(1.15f) }
    val sel = remember { Animatable(0f) }        // card selection highlight
    val barPop = remember { Animatable(0f) }     // action bar reveal

    LaunchedEffect(Unit) {
        while (true) {
            fingerScale.snapTo(1.15f); sel.snapTo(0f); barPop.snapTo(0f)
            delay(520)
            fingerScale.animateTo(0.76f, motion.defaultSpatialSpec())   // press and hold
            coroutineScope {
                launch { fingerScale.animateTo(1.05f, motion.fastSpatialSpec()) }
                launch { sel.animateTo(1f, motion.fastSpatialSpec()) }   // card selects
            }
            delay(90)
            barPop.animateTo(1f, motion.fastSpatialSpec())              // action bar appears
            delay(1050)
            coroutineScope {
                launch { sel.animateTo(0f, motion.defaultEffectsSpec()) }
                launch { barPop.animateTo(0f, motion.defaultSpatialSpec()) }
                launch { fingerScale.animateTo(1.15f, motion.defaultSpatialSpec()) }
            }
            delay(400)
        }
    }

    Box(modifier.size(200.dp), contentAlignment = Alignment.Center) {
        // Selected sample card (upper).
        val selectedCardShape = MaterialTheme.shapes.large
        Box(
            Modifier
                .align(Alignment.Center)
                .offset(y = (-42).dp)
                .size(120.dp, 74.dp)
                .graphicsLayer {
                    val s = 1f + sel.value * 0.06f
                    scaleX = s; scaleY = s
                    shape = selectedCardShape
                    clip = true
                }
                .background(MaterialTheme.colorScheme.primaryContainer, selectedCardShape)
                .border(
                    width = 3.dp,
                    color = MaterialTheme.colorScheme.primary.copy(alpha = sel.value),
                    shape = selectedCardShape,
                ),
        )

        // Finger holding the card.
        Icon(
            imageVector = Icons.Filled.TouchApp,
            contentDescription = null,
            tint = MaterialTheme.colorScheme.onPrimaryContainer,
            modifier = Modifier
                .align(Alignment.Center)
                .offset(y = (-30).dp)
                .size(40.dp)
                .graphicsLayer { scaleX = fingerScale.value; scaleY = fingerScale.value },
        )

        // The action bar that appears on select: reshape / pin / remove.
        Row(
            Modifier
                .align(Alignment.Center)
                .offset(y = 40.dp)
                .graphicsLayer {
                    val s = barPop.value
                    scaleX = s; scaleY = s
                    alpha = s
                    transformOrigin = TransformOrigin(0.5f, 0f)
                }
                .background(MaterialTheme.colorScheme.surface, MaterialTheme.shapes.extraLarge)
                .padding(horizontal = 12.dp, vertical = 8.dp),
            horizontalArrangement = Arrangement.spacedBy(14.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            ActionDot(Icons.Filled.Category)   // reshape
            ActionDot(Icons.Filled.PushPin)    // pin
            ActionDot(Icons.Filled.Delete)     // remove
        }
    }
}

/** One round action-bar button used by [SelectActionBarDemo]. */
@Composable
private fun ActionDot(icon: ImageVector) {
    Box(
        Modifier.size(30.dp).background(MaterialTheme.colorScheme.primary, CircleShape),
        contentAlignment = Alignment.Center,
    ) {
        Icon(
            imageVector = icon,
            contentDescription = null,
            tint = MaterialTheme.colorScheme.onPrimary,
            modifier = Modifier.size(18.dp),
        )
    }
}

/** A softly pulsing icon centred on a real control (the ⋮ menu or the + button) via its captured
 *  root-space position, so it lands correctly on any device. Renders nothing until the anchor is set. */
@Composable
private fun DirectionalPointer(
    anchor: Offset,
    haloSize: Dp,
    modifier: Modifier = Modifier,
) {
    if (anchor == Offset.Zero) return
    val motion = MaterialTheme.motionScheme
    val pulse = remember { Animatable(0f) }
    LaunchedEffect(Unit) {
        while (true) {
            pulse.animateTo(1f, motion.slowSpatialSpec())
            pulse.animateTo(0f, motion.slowSpatialSpec())
        }
    }
    // A hollow pulsing ring centred on the control, sized to encircle it — so the real ⋮ / + shows
    // through the middle, highlighted rather than covered (the FAB is large, so a filled disc buried
    // it). haloSize is chosen per-target to clear the control.
    Box(
        modifier = modifier
            .offset {
                val half = (haloSize / 2).toPx()
                IntOffset((anchor.x - half).roundToInt(), (anchor.y - half).roundToInt())
            }
            .size(haloSize)
            .graphicsLayer {
                val s = 0.85f + pulse.value * 0.3f
                scaleX = s; scaleY = s
                alpha = 0.55f + pulse.value * 0.45f
            }
            .border(3.dp, MaterialTheme.colorScheme.primary, CircleShape),
    )
}

/** The bottom caption panel shared by every hint: body text + Skip / Next (or Got it) actions. */
@Composable
private fun CoachPanel(
    body: String,
    isLast: Boolean,
    onAdvance: () -> Unit,
    onDismiss: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Surface(
        modifier = modifier.fillMaxWidth(),
        shape = MaterialTheme.shapes.extraLarge,
        color = MaterialTheme.colorScheme.surface,
        tonalElevation = 6.dp,
    ) {
        Column(
            Modifier.padding(horizontal = 24.dp, vertical = 20.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text(
                text = stringResource(R.string.coach_title),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.primary,
            )
            Text(
                text = body,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(top = 8.dp),
            )
            Row(
                Modifier
                    .fillMaxWidth()
                    .padding(top = 16.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                TextButton(onClick = onDismiss) {
                    Text(stringResource(R.string.coach_skip))
                }
                Button(onClick = onAdvance) {
                    Text(
                        if (isLast) stringResource(R.string.coach_got_it)
                        else stringResource(R.string.coach_next)
                    )
                }
            }
        }
    }
}
