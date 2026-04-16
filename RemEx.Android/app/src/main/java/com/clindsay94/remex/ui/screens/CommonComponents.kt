package com.clindsay94.remex.ui.screens

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.expandVertically
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.SignalWifiOff
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
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
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import kotlinx.coroutines.delay

/**
 * A slim warning banner that slides in at the top of a screen when the PC is not connected.
 * Non-blocking — the screen content is still visible/interactable below it. Includes a 3-second
 * debounce to avoid flashing during transient reconnection cycles.
 */
@Composable
fun NotConnectedBanner(
        isConnected: Boolean,
        onNavigateToConnection: () -> Unit,
        modifier: Modifier = Modifier
) {
    val haptic = LocalHapticFeedback.current
    var showBanner by remember { mutableStateOf(false) }

    // Debounce: only show banner if disconnected for 3+ seconds
    LaunchedEffect(isConnected) {
        if (!isConnected) {
            delay(3000)
            showBanner = !isConnected // Check again after delay
        } else {
            showBanner = false
        }
    }

    AnimatedVisibility(
            visible = showBanner,
            enter = expandVertically(),
            exit = shrinkVertically(),
            modifier = modifier
    ) {
        Card(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
                colors =
                        CardDefaults.cardColors(
                                containerColor = MaterialTheme.colorScheme.errorContainer
                        )
        ) {
            Row(
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp),
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
                val bannerInteractionSource = remember { MutableInteractionSource() }
                val isBannerPressed by bannerInteractionSource.collectIsPressedAsState()
                val bannerScale by
                        animateFloatAsState(
                                targetValue = if (isBannerPressed) 0.92f else 1f,
                                animationSpec = spring(stiffness = Spring.StiffnessMediumLow),
                                label = "bannerButtonScale"
                        )
                TextButton(
                        onClick = {
                            haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                            onNavigateToConnection()
                        },
                        interactionSource = bannerInteractionSource,
                        modifier =
                                Modifier.graphicsLayer(scaleX = bannerScale, scaleY = bannerScale),
                        colors =
                                ButtonDefaults.textButtonColors(
                                        contentColor = MaterialTheme.colorScheme.onErrorContainer
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
@Composable
fun DisconnectedFullScreen(
        screenName: String,
        onNavigateToConnection: () -> Unit,
        modifier: Modifier = Modifier
) {
    val haptic = LocalHapticFeedback.current
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
                        animationSpec = spring(stiffness = Spring.StiffnessMediumLow),
                        label = "ctaButtonScale"
                )
        Button(
                onClick = {
                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                    onNavigateToConnection()
                },
                interactionSource = ctaInteractionSource,
                modifier = Modifier.graphicsLayer(scaleX = ctaScale, scaleY = ctaScale),
                shape = ButtonDefaults.shape
        ) { Text(stringResource(R.string.button_setup_connection)) }
    }
}
