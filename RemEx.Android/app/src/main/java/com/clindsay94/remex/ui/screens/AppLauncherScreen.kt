package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.ui.platform.LocalView
import android.graphics.BitmapFactory
import android.util.Base64
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.Icons.Default
import androidx.compose.material.icons.automirrored.filled.Launch
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.*
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.material3.pulltorefresh.rememberPullToRefreshState
import androidx.compose.ui.input.nestedscroll.nestedScroll
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.ui.theme.calculateAdaptivePadding
import com.clindsay94.remex.ui.theme.cardShape

data class AppLauncherUiState(
    val apps: List<AppEntry> = emptyList(),
    val shapePreset: Float = 0f,
    val cornerRadius: Int = 8,
    val isConnected: Boolean = false,
    val isRefreshing: Boolean = false
)

@Composable
fun AppLauncherScreen(
    onNavigateToConnection: () -> Unit = {},
    viewModel: AppLauncherViewModel = run {
        val context = LocalContext.current
        val settingsManager = remember(context) { SettingsManager(context) }
        viewModel(
            factory = AppLauncherViewModel.provideFactory(
                settingsManager = settingsManager,
                remexClientManager = RemexClientManager,
                remexCoreClient = RemexCoreClient
            )
        )
    }
) {
    val apps by viewModel.apps.collectAsState()
    val shapePreset by viewModel.appLauncherCardShapePreset.collectAsState()
    val cornerRadius by viewModel.cardCornerRadius.collectAsState()
    val isConnected by RemexClientManager.isConnected.collectAsState()
    val isRefreshing by viewModel.isRefreshing.collectAsState()

    val uiState = AppLauncherUiState(
        apps = apps,
        shapePreset = shapePreset,
        cornerRadius = cornerRadius,
        isConnected = isConnected,
        isRefreshing = isRefreshing
    )

    AppLauncherScreenContent(
        uiState = uiState,
        onRefreshApps = { viewModel.refreshApps() },
        onLaunchApp = { viewModel.launchApp(it) },
        onNavigateToConnection = onNavigateToConnection
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppLauncherScreenContent(
    uiState: AppLauncherUiState,
    onRefreshApps: () -> Unit,
    onLaunchApp: (AppEntry) -> Unit,
    onNavigateToConnection: () -> Unit,
    modifier: Modifier = Modifier
) {
    val view = LocalView.current
    val scrollBehavior = rememberRemexTopBarScrollBehavior()
    Scaffold(
        modifier = Modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
        topBar = {
            RemexFlexibleTopBar(
                title = stringResource(R.string.screen_app_launcher_title),
                scrollBehavior = scrollBehavior,
                actions = {
                    IconButton(onClick = {
                        view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                        onRefreshApps()
                    }, enabled = uiState.isConnected) {
                        Icon(Default.Refresh, contentDescription = stringResource(R.string.cd_refresh))
                    }
                }
            )
        }
    ) { innerPadding ->
        PullToRefreshBox(
            isRefreshing = uiState.isRefreshing,
            onRefresh = onRefreshApps,
            modifier = modifier.fillMaxSize().padding(innerPadding)
        ) {
            Column(modifier = Modifier.fillMaxSize()) {
                if (!uiState.isConnected && uiState.apps.isEmpty()) {
                    DisconnectedFullScreen(
                        screenName = stringResource(R.string.screen_app_launcher_title),
                        onNavigateToConnection = onNavigateToConnection,
                        modifier = Modifier.weight(1f)
                    )
                } else if (uiState.apps.isEmpty()) {
                    Box(
                        modifier = Modifier.fillMaxSize(),
                        contentAlignment = Alignment.Center
                    ) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            Icon(
                                Icons.AutoMirrored.Filled.Launch,
                                contentDescription = stringResource(R.string.cd_launch_icon),
                                modifier = Modifier.size(64.dp),
                                tint = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                            Text(
                                stringResource(R.string.app_launcher_no_apps),
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                            Button(
                                onClick = {
                                    view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                    onRefreshApps()
                                },
                                modifier = Modifier.padding(16.dp)
                            ) {
                                Text(stringResource(R.string.button_fetch_from_host))
                            }
                        }
                    }
                } else {
                    val dedupedApps = remember(uiState.apps) {
                        uiState.apps.distinctBy { "${it.name}|${it.path}" }
                    }
                    LazyVerticalGrid(
                        columns = GridCells.Adaptive(minSize = 100.dp),
                        modifier = Modifier.fillMaxSize(),
                        contentPadding = PaddingValues(16.dp),
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                        verticalArrangement = Arrangement.spacedBy(12.dp)
                    ) {
                        items(dedupedApps, key = { "${it.name}|${it.path}" }) { app ->
                            AppGridItem(
                                app = app,
                                shapePreset = uiState.shapePreset,
                                cornerRadius = uiState.cornerRadius,
                                onClick = {
                                    view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                                    onLaunchApp(app)
                                }
                            )
                        }
                    }
                }
            }
        }
    }
}

@Preview(showBackground = true)
@Composable
fun AppLauncherScreenPreview() {
    RemExTheme {
        AppLauncherScreenContent(
            uiState = AppLauncherUiState(
                apps = listOf(
                    AppEntry("Calculator", "calc.exe"),
                    AppEntry("Notepad", "notepad.exe"),
                    AppEntry("Chrome", "chrome.exe")
                ),
                shapePreset = 0f,
                cornerRadius = 8,
                isConnected = true
            ),
            onRefreshApps = {},
            onLaunchApp = {},
            onNavigateToConnection = {}
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppGridItem(
    app: AppEntry,
    shapePreset: Float,
    cornerRadius: Int,
    onClick: () -> Unit
) {
    val shape = cardShape(shapePreset, cornerRadius)
    val adaptivePadding = calculateAdaptivePadding(shapePreset)

    Card(
        onClick = onClick,
        shape = shape,
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(adaptivePadding),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center
        ) {
            // Icon wrapped in the card shape for bounded display
            Box(
                modifier = Modifier
                    .size(56.dp)
                    .clip(shape),
                contentAlignment = Alignment.Center
            ) {
                if (app.iconBase64 != null) {
                    val bitmap = remember(app.iconBase64) {
                        try {
                            val imageBytes = Base64.decode(app.iconBase64, Base64.DEFAULT)
                            BitmapFactory.decodeByteArray(imageBytes, 0, imageBytes.size)
                        } catch (_: Exception) {
                            null
                        }
                    }
                    if (bitmap != null) {
                        Image(
                            bitmap = bitmap.asImageBitmap(),
                            contentDescription = app.name,
                            modifier = Modifier.size(48.dp)
                        )
                    } else {
                        Icon(
                            Icons.AutoMirrored.Filled.Launch,
                            contentDescription = app.name,
                            modifier = Modifier.size(36.dp),
                            tint = MaterialTheme.colorScheme.primary
                        )
                    }
                } else {
                    Icon(
                        Icons.AutoMirrored.Filled.Launch,
                        contentDescription = app.name,
                        modifier = Modifier.size(36.dp),
                        tint = MaterialTheme.colorScheme.primary
                    )
                }
            }

            // App name centered below icon — no executable path shown
            Text(
                text = app.name,
                style = MaterialTheme.typography.labelMedium,
                fontWeight = FontWeight.SemiBold,
                textAlign = TextAlign.Center,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(top = 8.dp)
            )
        }
    }
}
