package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.background
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.SignalWifiOff
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularWavyProgressIndicator
import androidx.compose.material3.ContainedLoadingIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearWavyProgressIndicator
import androidx.compose.material3.LoadingIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.MotionScheme
import androidx.compose.material3.SuggestionChip
import androidx.compose.material3.SuggestionChipDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.LocalReducedMotion
import com.clindsay94.remex.ui.theme.RemExTheme
import com.clindsay94.remex.R
import kotlinx.coroutines.delay

/**
 * A slim warning banner that slides in at the top of a screen when the PC is not connected.
 * Non-blocking — the screen content is still visible/interactable below it. Includes a 3-second
 * debounce to avoid flashing during transient reconnection cycles.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
/** One full breathe of the connection chip's status dot (1.0x -> 1.4x, reversed). */
private const val CONNECTION_CHIP_PULSE_MS = 1400

@Composable
fun NotConnectedBanner(
        isConnected: Boolean,
        onNavigateToConnection: () -> Unit,
        modifier: Modifier = Modifier,
        useDelay: Boolean = true
) {
        val view = LocalView.current
        var showBanner by remember { mutableStateOf(false) }

        // Debounce: only show banner if disconnected for 3+ seconds (if requested)
        LaunchedEffect(isConnected) {
                if (!isConnected) {
                        if (useDelay) {
                                delay(3000)
                        }
                        showBanner = !isConnected // Check again after delay
                } else {
                        showBanner = false
                }
        }

        AnimatedVisibility(
                visible = showBanner,
                enter = expandVertically(MaterialTheme.motionScheme.fastSpatialSpec()),
                exit = shrinkVertically(MaterialTheme.motionScheme.fastSpatialSpec()),
                modifier = modifier
        ) {
                Card(
                        modifier =
                                Modifier.fillMaxWidth()
                                        .padding(horizontal = 16.dp, vertical = 8.dp),
                        colors =
                                CardDefaults.cardColors(
                                        containerColor = MaterialTheme.colorScheme.errorContainer
                                )
                ) {
                        Row(
                                modifier =
                                        Modifier.fillMaxWidth()
                                                .padding(horizontal = 12.dp, vertical = 8.dp),
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.spacedBy(8.dp)
                        ) {
                                Icon(
                                        Icons.Default.SignalWifiOff,
                                        contentDescription = null,
                                        tint = MaterialTheme.colorScheme.onErrorContainer,
                                        modifier = Modifier.size(18.dp)
                                )
                                Text(
                                        text = stringResource(R.string.banner_no_pc_connected),
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onErrorContainer,
                                        modifier = Modifier.weight(1f)
                                )
                                val bannerInteractionSource = remember {
                                        MutableInteractionSource()
                                }
                                val isBannerPressed by
                                        bannerInteractionSource.collectIsPressedAsState()
                                val bannerScale by
                                        animateFloatAsState(
                                                targetValue = if (isBannerPressed) 0.92f else 1f,
                                                animationSpec =
                                                        MaterialTheme.motionScheme
                                                                .fastSpatialSpec(),
                                                label = "bannerButtonScale"
                                        )
                                TextButton(
                                        onClick = {
                                                view.performHapticFeedback(
                                                        HapticFeedbackConstants.CONFIRM
                                                )
                                                onNavigateToConnection()
                                        },
                                        interactionSource = bannerInteractionSource,
                                        modifier =
                                                Modifier.graphicsLayer(
                                                        scaleX = bannerScale,
                                                        scaleY = bannerScale
                                                ),
                                        colors =
                                                ButtonDefaults.textButtonColors(
                                                        contentColor =
                                                                MaterialTheme.colorScheme
                                                                        .onErrorContainer
                                                )
                                ) {
                                        Text(
                                                stringResource(R.string.button_connect),
                                                fontWeight = FontWeight.Bold,
                                                style = MaterialTheme.typography.labelSmall
                                        )
                                }
                        }
                }
        }
}

/**
 * Full-screen disconnected placeholder — used when a screen's content is entirely dependent on a
 * live connection (e.g. App Launcher with no cached apps).
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun DisconnectedFullScreen(
        screenName: String,
        onNavigateToConnection: () -> Unit,
        modifier: Modifier = Modifier
) {
        val view = LocalView.current
        Column(
                modifier = modifier.fillMaxWidth().padding(32.dp),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = Alignment.CenterHorizontally
        ) {
                Icon(
                        Icons.Default.SignalWifiOff,
                        contentDescription = null,
                        modifier = Modifier.size(72.dp),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.4f)
                )
                Spacer(Modifier.height(24.dp))
                Text(
                        stringResource(R.string.disconnected_requires_connection, screenName),
                        style = MaterialTheme.typography.titleMedium,
                        fontWeight = FontWeight.SemiBold,
                        textAlign = TextAlign.Center,
                        color = MaterialTheme.colorScheme.onSurface
                )
                Spacer(Modifier.height(8.dp))
                Text(
                        stringResource(R.string.disconnected_connect_wifi),
                        style = MaterialTheme.typography.bodyMedium,
                        textAlign = TextAlign.Center,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Spacer(Modifier.height(24.dp))
                val ctaInteractionSource = remember { MutableInteractionSource() }
                val isCtaPressed by ctaInteractionSource.collectIsPressedAsState()
                val ctaScale by
                        animateFloatAsState(
                                targetValue = if (isCtaPressed) 0.95f else 1f,
                                animationSpec = MaterialTheme.motionScheme.fastSpatialSpec(),
                                label = "ctaButtonScale"
                        )
                Button(
                        onClick = {
                                view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                                onNavigateToConnection()
                        },
                        interactionSource = ctaInteractionSource,
                        modifier = Modifier.graphicsLayer(scaleX = ctaScale, scaleY = ctaScale),
                        shape = ButtonDefaults.shape
                ) { Text(stringResource(R.string.button_setup_connection)) }
        }
}

/**
 * Small persistent pill showing PC connection state. Intended to float in a corner via the caller's
 * Modifier.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class, ExperimentalMaterial3Api::class)
@Composable
fun ConnectionStatusChip(isConnected: Boolean, modifier: Modifier = Modifier) {
        // M3: animateColorAsState for smooth dot color transition
        val dotColor by
                animateColorAsState(
                        targetValue =
                                if (isConnected) MaterialTheme.colorScheme.primary
                                else MaterialTheme.colorScheme.outline,
                        animationSpec = MaterialTheme.motionScheme.defaultEffectsSpec(),
                        label = "chip_dot_color"
                )
        // The infinite pulse is only composed while it is actually visible (connected) and
        // motion is allowed — under reduce-motion the dot holds steady (RemEx-3gkr).
        val reducedMotion = LocalReducedMotion.current
        // State (not a plain Float): the value is read inside graphicsLayer so pulse frames
        // stay in the draw phase and never recompose the chip.
        val pulseScale =
                if (isConnected && !reducedMotion) {
                        val infiniteTransition = rememberInfiniteTransition(label = "chip_pulse")
                        infiniteTransition.animateFloat(
                                initialValue = 1f,
                                targetValue = 1.4f,
                                animationSpec =
                                        infiniteRepeatable(
                                                animation =
                                                        tween(
                                                                durationMillis = CONNECTION_CHIP_PULSE_MS,
                                                                easing = FastOutSlowInEasing
                                                        ),
                                                repeatMode = RepeatMode.Reverse
                                        ),
                                label = "chip_pulse_scale"
                        )
                } else remember { mutableStateOf(1f) }
        // M3: SuggestionChip replaces Surface+Row for semantic chip semantics
        SuggestionChip(
                onClick = {},
                modifier = modifier,
                icon = {
                        Box(
                                modifier =
                                        Modifier.size(6.dp)
                                                .graphicsLayer {
                                                        if (isConnected) {
                                                                scaleX = pulseScale.value
                                                                scaleY = pulseScale.value
                                                        }
                                                }
                                                .background(color = dotColor, shape = CircleShape)
                        )
                },
                label = {
                        val labelFadeSpec = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
                        AnimatedContent(
                                targetState = isConnected,
                                transitionSpec = {
                                        fadeIn(labelFadeSpec) togetherWith fadeOut(labelFadeSpec)
                                },
                                label = "chip_label"
                        ) { connected ->
                                Text(
                                        text =
                                                if (connected) stringResource(R.string.status_connected)
                                                else stringResource(R.string.snackbar_no_pc_connected),
                                        style = MaterialTheme.typography.labelSmall
                                )
                        }
                },
                colors =
                        SuggestionChipDefaults.suggestionChipColors(
                                containerColor = MaterialTheme.colorScheme.surfaceBright
                        )
        )
}

/**
 * M3 Expressive indeterminate loading indicator — the morphing-polygon spinner that is the
 * signature "alive" loading affordance of Material 3 Expressive. Use this everywhere a plain
 * spinner used to sit (connecting, pairing, fetching).
 *
 * @param contained when true, draws the indicator inside the expressive tonal container shape.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun RemexLoadingIndicator(
        modifier: Modifier = Modifier,
        contained: Boolean = false,
        color: androidx.compose.ui.graphics.Color? = null,
) {
        when {
                contained -> ContainedLoadingIndicator(modifier = modifier)
                color != null -> LoadingIndicator(modifier = modifier, color = color)
                else -> LoadingIndicator(modifier = modifier)
        }
}

/**
 * M3 Expressive circular wavy gauge with a centred percent/value label. Replaces the old
 * `CircularProgressIndicator` gauges on the dashboard with the living, wavy expressive ring.
 *
 * @param progress 0f..1f fill amount (already animated by the caller if desired).
 * @param centerLabel text drawn in the middle of the ring (e.g. "73%").
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun RemexCircularWavyGauge(
        progress: Float,
        centerLabel: String,
        modifier: Modifier = Modifier,
        size: androidx.compose.ui.unit.Dp = 64.dp,
) {
        Box(contentAlignment = Alignment.Center, modifier = modifier) {
                CircularWavyProgressIndicator(
                        progress = { progress.coerceIn(0f, 1f) },
                        modifier = Modifier.size(size),
                )
                Text(
                        text = centerLabel,
                        style = MaterialTheme.typography.labelMedium,
                        fontWeight = FontWeight.Bold,
                )
        }
}

/**
 * M3 Expressive linear wavy progress bar. The track animates as a flowing wave while in motion —
 * ideal for file-transfer and long-running task progress.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun RemexLinearWavyProgress(
        progress: Float,
        modifier: Modifier = Modifier,
) {
        LinearWavyProgressIndicator(
                progress = { progress.coerceIn(0f, 1f) },
                modifier = modifier,
        )
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Preview(showBackground = true)
@Composable
private fun RemexExpressiveProgressPreview() {
        RemExTheme {
                Column(
                        modifier = Modifier.padding(24.dp),
                        verticalArrangement = Arrangement.spacedBy(24.dp),
                        horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                        RemexLoadingIndicator()
                        RemexLoadingIndicator(contained = true)
                        RemexCircularWavyGauge(progress = 0.73f, centerLabel = "73%")
                        RemexLinearWavyProgress(progress = 0.45f, modifier = Modifier.fillMaxWidth())
                }
        }
}

@Preview(showBackground = true)
@Composable
private fun NotConnectedBannerPreview() {
    RemExTheme {
        NotConnectedBanner(
            isConnected = false,
            onNavigateToConnection = {},
            useDelay = false
        )
    }
}

@Preview(showBackground = true)
@Composable
private fun DisconnectedFullScreenPreview() {
    RemExTheme {
        DisconnectedFullScreen(
            screenName = "Feature Name",
            onNavigateToConnection = {}
        )
    }
}

@Preview(showBackground = true)
@Composable
private fun ConnectionStatusChipPreview() {
    RemExTheme {
        Row(modifier = Modifier.padding(16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            ConnectionStatusChip(isConnected = true)
            ConnectionStatusChip(isConnected = false)
        }
    }
}
