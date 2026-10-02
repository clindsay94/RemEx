package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
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
    DesktopTabScreenContent(
            mode = mode,
            isConnected = isConnected,
            onModeChange = onModeChange,
            onStartStream = onStartStream,
            onNavigateToConnection = onNavigateToConnection,
            trackpadContent = { RemoteMouseScreen(onNavigateToConnection = onNavigateToConnection) },
    )
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
            when (mode) {
                DesktopMode.Stream ->
                        DesktopStreamPane(
                                isConnected = isConnected,
                                onStartStream = onStartStream,
                                onNavigateToConnection = onNavigateToConnection,
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
) {
    val view = LocalView.current
    Column(modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
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
                        view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                        onStartStream()
                    },
                    enabled = isConnected,
                    modifier = Modifier.fillMaxWidth().height(56.dp)
            ) {
                Icon(
                        Icons.Default.PlayArrow,
                        contentDescription = null /* decorative: the label says it */,
                        modifier = Modifier.size(20.dp)
                )
                Spacer(Modifier.width(8.dp))
                Text(stringResource(R.string.button_start_streaming))
            }
        }
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
