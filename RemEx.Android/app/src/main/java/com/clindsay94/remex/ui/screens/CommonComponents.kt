package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
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
import androidx.compose.animation.shrinkVertically
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
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
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
import com.clindsay94.remex.ui.theme.RemExTheme
import com.clindsay94.remex.R
import kotlinx.coroutines.delay

/**
 * A slim warning banner that slides in at the top of a screen when the PC is not connected.
 * Non-blocking — the screen content is still visible/interactable below it. Includes a 3-second
 * debounce to avoid flashing during transient reconnection cycles.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
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
        val infiniteTransition = rememberInfiniteTransition(label = "chip_pulse")
        val pulseScale by
                infiniteTransition.animateFloat(
                        initialValue = 1f,
                        targetValue = if (isConnected) 1.4f else 1f,
                        animationSpec =
                                infiniteRepeatable(
                                        animation =
                                                tween(
                                                        durationMillis = 1400,
                                                        easing = FastOutSlowInEasing
                                                ),
                                        repeatMode = RepeatMode.Reverse
                                ),
                        label = "chip_pulse_scale"
                )
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
                                                                scaleX = pulseScale
                                                                scaleY = pulseScale
                                                        }
                                                }
                                                .background(color = dotColor, shape = CircleShape)
                        )
                },
                label = {
                        Text(
                                text =
                                        if (isConnected) stringResource(R.string.status_connected)
                                        else stringResource(R.string.snackbar_no_pc_connected),
                                style = MaterialTheme.typography.labelSmall
                        )
                },
                colors =
                        SuggestionChipDefaults.suggestionChipColors(
                                containerColor =
                                        MaterialTheme.colorScheme.surfaceContainerHighest.copy(
                                                alpha = 0.92f
                                        )
                        )
        )
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
