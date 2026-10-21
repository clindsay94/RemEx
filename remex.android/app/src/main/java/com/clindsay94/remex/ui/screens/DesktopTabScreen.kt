package com.clindsay94.remex.ui.screens

import android.content.res.Configuration
import androidx.annotation.StringRes
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
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
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.SwipeVertical
import androidx.compose.material.icons.filled.TouchApp
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material.icons.filled.ZoomIn
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedCard
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.SheetValue
import androidx.compose.material3.rememberBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.RemexSegmentedSwitch
import com.clindsay94.remex.ui.navigation.DesktopMode
import com.clindsay94.remex.ui.theme.RemExTheme
import com.clindsay94.remex.ui.components.RemexHapticEvent
import com.clindsay94.remex.ui.components.rememberRemexHaptics
import com.clindsay94.remex.ui.theme.rememberRemexButtonShapes
import androidx.compose.material3.ButtonDefaults

/**
 * The Desktop tab (refresh spec, Desktop; RemEx-wqo7a.2): a Stream | Trackpad switch under the
 * "Desktop" header.
 *
 * - **Stream** is the stopped state: the preview area and "Start streaming". Starting opens the
 *   full-screen stream route (`Screen.RemoteDesktop`) on top of the tabs, so the stream keeps the
 *   whole screen, its SurfaceView and its gestures, with no pager or navigation bar around it.
 *   None of the stream code is involved until that route opens.
 * - **Trackpad** is the old Remote Mouse screen, as a mode of this tab rather than a destination.
 *
 * [mode] is owned by the navigation shell so it survives the pager disposing this page.
 */
@Composable
fun DesktopTabScreen(
        mode: DesktopMode,
        onModeChange: (DesktopMode) -> Unit,
        onStartStream: () -> Unit,
        onNavigateToConnection: () -> Unit,
) {
    val isConnected by RemexClientManager.isConnected.collectAsStateWithLifecycle()
    val needsPairing by RemexClientManager.needsPairing.collectAsStateWithLifecycle()
    var showStreamSettings by rememberSaveable { mutableStateOf(false) }
    DesktopTabScreenContent(
            mode = mode,
            isConnected = isConnected,
            needsPairing = isConnected && needsPairing,
            onModeChange = onModeChange,
            onStartStream = onStartStream,
            onNavigateToConnection = onNavigateToConnection,
            onPair = {
                ConnectionOpenRequests.requestAddPc()
                onNavigateToConnection()
            },
            onOpenStreamSettings = { showStreamSettings = true },
            trackpadContent = { RemoteMouseScreen(onNavigateToConnection = onNavigateToConnection) },
    )
    if (showStreamSettings) {
        PreStreamSettingsSheet(onDismiss = { showStreamSettings = false })
    }
}

/**
 * The stream settings sheet before a stream starts (RemEx-172ba): the same Stream and Input &
 * Controls sections the stream's own sheet shows ([StreamSettingsSections]). Picking a display and the
 * window controls need a running stream, so they stay in the stream's sheet only.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun PreStreamSettingsSheet(
        onDismiss: () -> Unit,
        viewModel: PreStreamSettingsViewModel = viewModel(),
) {
    val config by viewModel.config.collectAsStateWithLifecycle()
    val prefs by viewModel.preferences.collectAsStateWithLifecycle()
    val isLandscape = LocalConfiguration.current.orientation == Configuration.ORIENTATION_LANDSCAPE
    ModalBottomSheet(
            onDismissRequest = onDismiss,
            sheetState = rememberBottomSheetState(initialValue = SheetValue.Hidden, enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded)),
            containerColor = MaterialTheme.colorScheme.surfaceContainerLow,
    ) {
        Column(
                modifier =
                        Modifier.fillMaxWidth()
                                .verticalScroll(rememberScrollState())
                                .padding(horizontal = 24.dp, vertical = 16.dp)
                                .navigationBarsPadding(),
                verticalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                        stringResource(R.string.remote_desktop_config_title),
                        style = MaterialTheme.typography.titleLargeEmphasized,
                        modifier = Modifier.weight(1f)
                )
                IconButton(onClick = onDismiss) {
                    Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cd_close_settings))
                }
            }
            Text(
                    stringResource(R.string.desktop_stream_settings_pre_stream_hint),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            StreamSettingsSections(
                    config = config,
                    directTouch = prefs.directTouch,
                    pointerSpeed = prefs.pointerSpeed,
                    cursorScale = prefs.cursorScale,
                    vScrollSensitivity = prefs.verticalScrollSensitivity,
                    hScrollSensitivity = prefs.horizontalScrollSensitivity,
                    isLandscape = isLandscape,
                    onApplyPreset = viewModel::applyPreset,
                    onSelectCustomPreset = viewModel::selectCustomPreset,
                    onUpdateQuality = viewModel::updateQuality,
                    onUpdateTargetFps = viewModel::updateTargetFps,
                    onUpdateScale = viewModel::updateScale,
                    onUpdateAdaptiveScale = viewModel::updateAdaptiveScale,
                    onUpdateDirectTouch = viewModel::updateDirectTouch,
                    onUpdatePointerSpeed = viewModel::updatePointerSpeed,
                    onUpdateCursorScale = viewModel::updateCursorScale,
                    onUpdateScrollSensitivity = viewModel::updateScrollSensitivity,
            )
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DesktopTabScreenContent(
        mode: DesktopMode,
        isConnected: Boolean,
        onModeChange: (DesktopMode) -> Unit,
        onStartStream: () -> Unit,
        onNavigateToConnection: () -> Unit,
        trackpadContent: @Composable () -> Unit,
        /**
         * Connected, but the PC no longer recognises this phone: both modes would be refused, so the
         * tab says so instead of offering Start streaming or a trackpad that does nothing (3.0 comb,
         * needs-pairing-gaps). Gated HERE, before the stream route is ever opened - the stream
         * screen itself is left alone (docs/REGRESSION-GUARDS.md).
         */
        needsPairing: Boolean = false,
        onPair: () -> Unit = onNavigateToConnection,
        onOpenStreamSettings: () -> Unit = {},
) {
    Scaffold(
            topBar = {
                Column {
                    // The title is the tab's own label; the subtitle says what this mode does.
                    RemexFlexibleTopBar(
                            title = stringResource(R.string.screen_remote_desktop_title),
                            subtitle =
                                    stringResource(
                                            when (mode) {
                                                DesktopMode.Stream ->
                                                        R.string.screen_remote_desktop_subtitle
                                                DesktopMode.Trackpad ->
                                                        R.string.desktop_trackpad_subtitle
                                            }
                                    ),
                    )
                    RemexSegmentedSwitch(
                            options = DesktopMode.entries,
                            selected = mode,
                            labelRes = { it.labelRes },
                            onSelect = onModeChange,
                    )
                }
            }
    ) { innerPadding ->
        Box(modifier = Modifier.fillMaxSize().padding(innerPadding)) {
            if (ConnectedPane.state(isConnected, needsPairing) == ConnectedPaneState.NeedsPairing) {
                NeedsPairingContent(onPair = onPair, modifier = Modifier.align(Alignment.Center))
            } else when (mode) {
                DesktopMode.Stream ->
                        DesktopStreamPane(
                                isConnected = isConnected,
                                onStartStream = onStartStream,
                                onNavigateToConnection = onNavigateToConnection,
                                onOpenStreamSettings = onOpenStreamSettings,
                        )
                DesktopMode.Trackpad -> trackpadContent()
            }
        }
    }
}

@Composable
private fun DesktopStreamPane(
        isConnected: Boolean,
        onStartStream: () -> Unit,
        onNavigateToConnection: () -> Unit,
        onOpenStreamSettings: () -> Unit,
) {
    val haptics = rememberRemexHaptics()
    val scrollState = rememberScrollState()
    // Tapping Desktop while on Desktop goes back to the top (3.0 comb, no-reselect).
    com.clindsay94.remex.ui.navigation.TabReselectEffect(com.clindsay94.remex.ui.navigation.Screen.Desktop) {
        scrollState.animateScrollTo(0)
    }
    Column(modifier = Modifier.fillMaxSize().verticalScroll(scrollState)) {
        NotConnectedBanner(
                isConnected = isConnected,
                onNavigateToConnection = onNavigateToConnection
        )
        Column(
                modifier = Modifier.fillMaxWidth().padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            // The stream preview area: where the PC's screen will be, at the PC's usual shape.
            Surface(
                    modifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f),
                    shape = MaterialTheme.shapes.large,
                    color = MaterialTheme.colorScheme.surfaceContainer
            ) {
                Column(
                        modifier = Modifier.fillMaxSize().padding(24.dp),
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.Center
                ) {
                    Icon(
                            Icons.Default.Computer,
                            contentDescription = null,
                            tint = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.size(48.dp)
                    )
                    Spacer(Modifier.height(12.dp))
                    Text(
                            stringResource(R.string.desktop_stream_hint),
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            textAlign = TextAlign.Center
                    )
                }
            }
            Button(
                    onClick = {
                        haptics.perform(RemexHapticEvent.Press)
                        onStartStream()
                    },
                    enabled = isConnected,
                    modifier = Modifier.fillMaxWidth().height(56.dp),
                    shapes = rememberRemexButtonShapes(),
                    contentPadding = ButtonDefaults.ContentPadding
            ) {
                Icon(
                        Icons.Default.PlayArrow,
                        contentDescription = null /* decorative: the label says it */,
                        modifier = Modifier.size(20.dp)
                )
                Spacer(Modifier.width(8.dp))
                Text(stringResource(R.string.button_start_streaming))
            }
            // Saved settings, so they can be set up with no PC connected too.
            OutlinedButton(
                    onClick = {
                        haptics.perform(RemexHapticEvent.Press)
                        onOpenStreamSettings()
                    },
                    modifier = Modifier.fillMaxWidth(),
                    shapes = rememberRemexButtonShapes(),
                    contentPadding = ButtonDefaults.ContentPadding
            ) {
                Icon(Icons.Default.Tune, contentDescription = null, modifier = Modifier.size(20.dp))
                Spacer(Modifier.width(8.dp))
                Text(stringResource(R.string.remote_desktop_config_title))
            }
            TouchControlsCard()
        }
    }
}

@Composable
private fun TouchControlsCard() {
    OutlinedCard(modifier = Modifier.fillMaxWidth()) {
        Column(
                modifier = Modifier.fillMaxWidth().padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            Text(
                    stringResource(R.string.desktop_touch_controls_title),
                    style = MaterialTheme.typography.titleSmall
            )
            TouchControlRow(Icons.Default.TouchApp, R.string.desktop_touch_tap)
            TouchControlRow(Icons.Default.Mouse, R.string.desktop_touch_hold)
            TouchControlRow(Icons.Default.SwipeVertical, R.string.desktop_touch_two_finger)
            TouchControlRow(Icons.Default.ZoomIn, R.string.desktop_touch_pinch)
        }
    }
}

@Composable
private fun TouchControlRow(icon: ImageVector, @StringRes textRes: Int) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Icon(
                icon,
                contentDescription = null /* decorative: the text beside it says it */,
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(20.dp)
        )
        Spacer(Modifier.width(12.dp))
        Text(
                stringResource(textRes),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

@Preview(showBackground = true)
@Composable
private fun DesktopTabScreenPreview() {
    RemExTheme {
        DesktopTabScreenContent(
                mode = DesktopMode.Stream,
                isConnected = true,
                onModeChange = {},
                onStartStream = {},
                onNavigateToConnection = {},
                trackpadContent = {},
        )
    }
}
