package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Dashboard
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.repeatOnLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior
import com.clindsay94.remex.ui.theme.RemExTheme

/**
 * Home, the first tab (refresh spec, Navigation slot 1; RemEx-wqo7a.2).
 *
 * The phase-2 placeholder that makes the navigation real: the PC card (the Sensors canvas's own
 * [ConnectionOrbCard], plus Wake while disconnected) and an Open Sensors card into the full canvas.
 * Phase 3 adds pinned sensors, shortcuts and recent activity around these.
 *
 * Reads [DashboardViewModel] for the connection state, the PC card's shape and Wake, but never
 * calls its `setVisible`: Home shows no readings, so it must not switch the telemetry parse on.
 */
@Composable
fun HomeScreen(
        onNavigateToConnection: () -> Unit,
        onOpenSensors: () -> Unit,
        viewModel: DashboardViewModel = viewModel(),
) {
    val isConnected by viewModel.isConnected.collectAsStateWithLifecycle()
    val isConnecting by viewModel.isConnecting.collectAsStateWithLifecycle()
    val pcCardShapePreset by viewModel.pcCardShapePreset.collectAsStateWithLifecycle()
    val cornerRadius by viewModel.cardCornerRadius.collectAsStateWithLifecycle()
    val snackbarHostState = remember { SnackbarHostState() }

    // Same rule as the Sensors canvas: a Wake result is shown only while this screen is started,
    // never minutes later after the user has gone elsewhere.
    val lifecycleOwner = LocalLifecycleOwner.current
    LaunchedEffect(lifecycleOwner) {
        lifecycleOwner.repeatOnLifecycle(Lifecycle.State.STARTED) {
            viewModel.wakeStatus.collect { message ->
                snackbarHostState.showSnackbar(message, duration = SnackbarDuration.Short)
            }
        }
    }

    HomeScreenContent(
            isConnected = isConnected,
            isConnecting = isConnecting,
            pcCardShapePreset = pcCardShapePreset,
            cornerRadius = cornerRadius,
            snackbarHostState = snackbarHostState,
            onToggleConnection = { viewModel.toggleConnection() },
            onWakePc = { viewModel.wakePc() },
            onNavigateToConnection = onNavigateToConnection,
            onOpenSensors = onOpenSensors,
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreenContent(
        isConnected: Boolean,
        isConnecting: Boolean,
        pcCardShapePreset: Float,
        cornerRadius: Int,
        onToggleConnection: () -> Unit,
        onWakePc: () -> Unit,
        onNavigateToConnection: () -> Unit,
        onOpenSensors: () -> Unit,
        snackbarHostState: SnackbarHostState = remember { SnackbarHostState() },
) {
    val view = LocalView.current
    val scrollBehavior = rememberRemexTopBarScrollBehavior()
    Scaffold(
            modifier = Modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
            topBar = {
                RemexFlexibleTopBar(
                        title = stringResource(R.string.screen_home_title),
                        subtitle = stringResource(R.string.screen_home_subtitle),
                        scrollBehavior = scrollBehavior
                )
            },
            snackbarHost = {
                SnackbarHost(snackbarHostState, modifier = Modifier.navigationBarsPadding())
            }
    ) { innerPadding ->
        Column(
                modifier =
                        Modifier.fillMaxSize()
                                .padding(innerPadding)
                                .verticalScroll(rememberScrollState())
                                .padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            // The PC card: the canvas's own connection card, so both screens agree on what
            // "online", "connecting" and "tap to connect" look like.
            Card(
                    modifier = Modifier.fillMaxWidth(),
                    shape = MaterialTheme.shapes.large,
                    colors =
                            CardDefaults.cardColors(
                                    containerColor = MaterialTheme.colorScheme.surfaceContainer
                            )
            ) {
                Box(modifier = Modifier.fillMaxWidth().height(168.dp)) {
                    ConnectionOrbCard(
                            isConnected = isConnected,
                            isConnecting = isConnecting,
                            shapePreset = pcCardShapePreset,
                            cornerRadius = cornerRadius,
                            onToggle = {
                                view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                                onToggleConnection()
                            },
                            onNavigateToConnection = onNavigateToConnection
                    )
                }
                // Disconnected, the PC card is the connect card: Wake sits under the orb.
                AnimatedVisibility(
                        visible = !isConnected && !isConnecting,
                        enter =
                                expandVertically(MaterialTheme.motionScheme.fastSpatialSpec()) +
                                        fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
                        exit =
                                shrinkVertically(MaterialTheme.motionScheme.fastSpatialSpec()) +
                                        fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
                ) {
                    Box(modifier = Modifier.fillMaxWidth().height(72.dp)) {
                        WakeOnLanCard(onWake = onWakePc)
                    }
                }
            }

            OpenSensorsCard(onClick = {
                view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                onOpenSensors()
            })
        }
    }
}

@Composable
private fun OpenSensorsCard(onClick: () -> Unit) {
    val sensorsTitle = stringResource(R.string.screen_dashboard_title)
    Card(
            onClick = onClick,
            modifier = Modifier.fillMaxWidth(),
            shape = MaterialTheme.shapes.large,
            colors =
                    CardDefaults.cardColors(
                            containerColor = MaterialTheme.colorScheme.surfaceContainer
                    )
    ) {
        Row(
                modifier = Modifier.fillMaxWidth().padding(16.dp),
                verticalAlignment = Alignment.CenterVertically
        ) {
            Icon(
                    Icons.Default.Dashboard,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(32.dp)
            )
            Spacer(Modifier.width(16.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(
                        stringResource(R.string.home_open_sensors_title, sensorsTitle),
                        style = MaterialTheme.typography.titleMedium,
                        color = MaterialTheme.colorScheme.onSurface
                )
                Text(
                        stringResource(R.string.home_open_sensors_body),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            Icon(
                    Icons.AutoMirrored.Filled.KeyboardArrowRight,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

@Preview(showBackground = true)
@Composable
private fun HomeScreenPreview() {
    RemExTheme {
        HomeScreenContent(
                isConnected = false,
                isConnecting = false,
                pcCardShapePreset = 0f,
                cornerRadius = 12,
                onToggleConnection = {},
                onWakePc = {},
                onNavigateToConnection = {},
                onOpenSensors = {},
        )
    }
}
