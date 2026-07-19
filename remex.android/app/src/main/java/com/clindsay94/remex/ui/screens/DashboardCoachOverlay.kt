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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.TouchApp
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.MotionScheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
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
            .background(Color.Black.copy(alpha = 0.62f))
            // Swallow all touches so the canvas underneath can't be interacted with mid-hint; a scrim
            // tap intentionally does NOT dismiss (avoids losing the tutorial to a stray tap).
            .pointerInput(Unit) { detectTapGestures {} },
    ) {
        when (step) {
            0 -> HoldToLiftDemo(Modifier.align(Alignment.Center))
            1 -> DirectionalPointer(anchor = menuAnchor, haloSize = 48.dp)
            2 -> DirectionalPointer(anchor = addAnchor, haloSize = 84.dp)
        }

        CoachPanel(
            body = when (step) {
                0 -> stringResource(R.string.coach_hold_body)
                1 -> stringResource(R.string.coach_menu_body)
                else -> stringResource(R.string.coach_add_body)
            },
            isLast = isLast,
            onAdvance = onAdvance,
            onDismiss = onDismiss,
            // Place each caption near what its hint points at: step 0 just below the centred animation,
            // step 1 up by the ⋮ menu, step 2 low but lifted clear of the + FAB.
            modifier = when (step) {
                0 -> Modifier.align(Alignment.Center).offset(y = 150.dp)
                1 -> Modifier.align(Alignment.TopCenter).statusBarsPadding().padding(top = 64.dp)
                else -> Modifier.align(Alignment.BottomCenter).padding(bottom = 100.dp)
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
    val motion = remember { MotionScheme.expressive() }
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

        // The sample card being lifted.
        Box(
            Modifier
                .size(120.dp, 84.dp)
                .graphicsLayer {
                    val s = 1f + cardLift.value * 0.16f
                    scaleX = s; scaleY = s
                    translationX = dragX.value * nudge.toPx()
                    shadowElevation = cardLift.value * 28f
                    shape = RoundedCornerShape(22.dp)
                    clip = true
                }
                .background(MaterialTheme.colorScheme.primaryContainer, RoundedCornerShape(22.dp)),
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

/** A softly pulsing icon centred on a real control (the ⋮ menu or the + button) via its captured
 *  root-space position, so it lands correctly on any device. Renders nothing until the anchor is set. */
@Composable
private fun DirectionalPointer(
    anchor: Offset,
    haloSize: Dp,
    modifier: Modifier = Modifier,
) {
    if (anchor == Offset.Zero) return
    val motion = remember { MotionScheme.expressive() }
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
        shape = RoundedCornerShape(28.dp),
        color = MaterialTheme.colorScheme.surface,
        tonalElevation = 6.dp,
        shadowElevation = 8.dp,
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
