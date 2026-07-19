package com.clindsay94.remex.ui.screens

import androidx.compose.animation.core.Animatable
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.TouchApp
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.MotionScheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import kotlinx.coroutines.delay

/**
 * First-run coaching for Home Base (RemEx-km0i.10). Android-native and motion-first: the star is an
 * animated hold-to-lift demonstration (the one gesture with no visual affordance), followed by two
 * light directional callouts for the ⋮ menu and the + button.
 *
 * Physics comes from the M3 **Expressive** [MotionScheme] spring tokens — bouncy `spatial` specs for
 * anything that moves or scales, gentler `effects` specs for alpha/scrim. The looping demo is driven
 * by [Animatable]s in a coroutine so each phase is a real spring (not a duration tween).
 *
 * Purely presentational: the caller owns visibility/step state and the suppression guards
 * (drag / selection / open sheet — locked decision #7), only mounting this when a hint should show.
 *
 * @param step 0..[DASHBOARD_COACH_HINT_COUNT]-1 — which hint to render.
 * @param onAdvance advance to the next hint (the last step's button finishes + persists).
 * @param onDismiss skip the whole sequence now.
 */
@Composable
fun DashboardCoachOverlay(
    step: Int,
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
            1 -> DirectionalPointer(
                icon = Icons.Filled.MoreVert,
                modifier = Modifier
                    .align(Alignment.TopEnd)
                    .padding(top = 8.dp, end = 8.dp),
            )
            2 -> DirectionalPointer(
                icon = Icons.Filled.Add,
                modifier = Modifier
                    .align(Alignment.BottomEnd)
                    .padding(bottom = 96.dp, end = 16.dp),
            )
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
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .padding(horizontal = 20.dp, vertical = 28.dp),
        )
    }
}

/** Animated hold-to-lift demonstration: finger presses a mini card, a ripple blooms, the card pops up
 *  (bouncy) and nudges sideways to imply a drag — looped forever with expressive springs. */
@Composable
private fun HoldToLiftDemo(modifier: Modifier = Modifier) {
    val motion = remember { MotionScheme.expressive() }
    val lift = remember { Animatable(0f) }        // 0 resting → 1 lifted (scale + elevation)
    val ripple = remember { Animatable(0f) }      // 0 → 1 press ripple bloom
    val fingerDrop = remember { Animatable(0f) }  // 0 raised → 1 touching the card
    val dragX = remember { Animatable(0f) }       // 0 centered → 1 nudged right (drag hint)

    LaunchedEffect(Unit) {
        while (true) {
            lift.snapTo(0f); ripple.snapTo(0f); fingerDrop.snapTo(0f); dragX.snapTo(0f)
            delay(400)
            fingerDrop.animateTo(1f, motion.defaultSpatialSpec())   // finger descends
            ripple.animateTo(1f, motion.slowEffectsSpec())          // hold ripple blooms
            lift.animateTo(1f, motion.fastSpatialSpec())            // card pops up — bouncy
            delay(120)
            dragX.animateTo(1f, motion.defaultSpatialSpec())        // drag sideways
            delay(450)
            ripple.animateTo(0f, motion.defaultEffectsSpec())       // release
            dragX.animateTo(0f, motion.defaultSpatialSpec())
            lift.animateTo(0f, motion.defaultSpatialSpec())         // settle
            delay(500)
        }
    }

    Box(modifier.size(200.dp), contentAlignment = Alignment.Center) {
        val nudge = 34.dp

        // Bloom ripple behind the card during the "hold".
        Box(
            Modifier
                .size(96.dp)
                .graphicsLayer {
                    val s = 0.4f + ripple.value * 1.1f
                    scaleX = s; scaleY = s
                    translationX = dragX.value * nudge.toPx()
                    alpha = (1f - ripple.value) * 0.5f
                }
                .border(3.dp, MaterialTheme.colorScheme.primary, CircleShape),
        )

        // The sample card being lifted.
        Box(
            Modifier
                .size(120.dp, 84.dp)
                .graphicsLayer {
                    val s = 1f + lift.value * 0.14f
                    scaleX = s; scaleY = s
                    translationX = dragX.value * nudge.toPx()
                    shadowElevation = lift.value * 26f
                    shape = RoundedCornerShape(22.dp)
                    clip = true
                }
                .background(MaterialTheme.colorScheme.primaryContainer, RoundedCornerShape(22.dp)),
        )

        // The finger, descending onto the card and riding the drag nudge.
        Icon(
            imageVector = Icons.Filled.TouchApp,
            contentDescription = null,
            tint = MaterialTheme.colorScheme.onPrimaryContainer,
            modifier = Modifier
                .align(Alignment.Center)
                .size(40.dp)
                .graphicsLayer {
                    translationY = (1f - fingerDrop.value) * (-64.dp).toPx() + 18.dp.toPx()
                    translationX = dragX.value * nudge.toPx()
                    val s = 0.9f + lift.value * 0.15f
                    scaleX = s; scaleY = s
                },
        )
    }
}

/** A softly pulsing icon that points the eye at a real control (the ⋮ menu or the + button). */
@Composable
private fun DirectionalPointer(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    modifier: Modifier = Modifier,
) {
    val motion = remember { MotionScheme.expressive() }
    val pulse = remember { Animatable(0f) }
    LaunchedEffect(Unit) {
        while (true) {
            pulse.animateTo(1f, motion.slowSpatialSpec())
            pulse.animateTo(0f, motion.slowSpatialSpec())
        }
    }
    Surface(
        modifier = modifier
            .size(56.dp)
            .graphicsLayer {
                val s = 0.92f + pulse.value * 0.22f
                scaleX = s; scaleY = s
            },
        shape = CircleShape,
        color = MaterialTheme.colorScheme.primary,
    ) {
        Box(contentAlignment = Alignment.Center) {
            Icon(
                imageVector = icon,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onPrimary,
                modifier = Modifier.size(28.dp),
            )
        }
    }
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
