package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.ui.platform.LocalView
import android.graphics.BitmapFactory
import android.util.Base64
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.carousel.HorizontalMultiBrowseCarousel
import androidx.compose.material3.carousel.rememberCarouselState
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.Icons.Default
import androidx.compose.material.icons.automirrored.filled.Launch
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.SettingsEthernet
import androidx.compose.material3.*
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.material3.pulltorefresh.PullToRefreshDefaults
import androidx.compose.material3.pulltorefresh.rememberPullToRefreshState
import androidx.compose.ui.input.nestedscroll.nestedScroll
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior
import androidx.compose.runtime.Composable
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import android.graphics.Bitmap
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalContext
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
    val recentApps: List<AppEntry> = emptyList(),
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
    val apps by viewModel.apps.collectAsStateWithLifecycle()
    val recentApps by viewModel.recentApps.collectAsStateWithLifecycle()
    val shapePreset by viewModel.appLauncherCardShapePreset.collectAsStateWithLifecycle()
    val cornerRadius by viewModel.cardCornerRadius.collectAsStateWithLifecycle()
    val isConnected by RemexClientManager.isConnected.collectAsStateWithLifecycle()
    val isRefreshing by viewModel.isRefreshing.collectAsStateWithLifecycle()

    val uiState = AppLauncherUiState(
        apps = apps,
        recentApps = recentApps,
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

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
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
                scrollBehavior = scrollBehavior
            )
        }
    ) { innerPadding ->
      Box(modifier = modifier.fillMaxSize().padding(innerPadding)) {
        val pullState = rememberPullToRefreshState()
        PullToRefreshBox(
            isRefreshing = uiState.isRefreshing,
            onRefresh = {
                view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                onRefreshApps()
            },
            modifier = Modifier.fillMaxSize(),
            state = pullState,
            indicator = {
                PullToRefreshDefaults.LoadingIndicator(
                    state = pullState,
                    isRefreshing = uiState.isRefreshing,
                    modifier = Modifier.align(Alignment.TopCenter)
                )
            }
        ) {
            Column(modifier = Modifier.fillMaxSize()) {
                // Disconnected / empty / apps cross-fade instead of hard-swapping (RemEx-svue);
                // tile animateItem and press-spring behavior inside the grid is untouched.
                val launcherBody = when {
                    !uiState.isConnected && uiState.apps.isEmpty() -> AppLauncherBody.Disconnected
                    uiState.apps.isEmpty() -> AppLauncherBody.Empty
                    else -> AppLauncherBody.Apps
                }
                val bodyFadeSpec = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
                AnimatedContent(
                    targetState = launcherBody,
                    transitionSpec = { fadeIn(bodyFadeSpec) togetherWith fadeOut(bodyFadeSpec) },
                    modifier = Modifier.fillMaxSize(),
                    label = "launcher_body",
                ) { body ->
                if (body == AppLauncherBody.Disconnected) {
                    DisconnectedFullScreen(
                        screenName = stringResource(R.string.screen_app_launcher_title),
                        onNavigateToConnection = onNavigateToConnection,
                        modifier = Modifier.fillMaxSize()
                    )
                } else if (body == AppLauncherBody.Empty) {
                    Box(
                        modifier = Modifier.fillMaxSize(),
                        contentAlignment = Alignment.Center
                    ) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            Icon(
                                Icons.AutoMirrored.Filled.Launch,
                                contentDescription = null /* decorative: the adjacent Text already says it (RemEx-xqli) */,
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
                    Column(modifier = Modifier.fillMaxSize()) {
                        // M3 Expressive: a "Recent" multi-browse carousel above the full grid —
                        // the best-of-both featured strip + complete launcher.
                        if (uiState.recentApps.isNotEmpty()) {
                            Text(
                                text = stringResource(R.string.app_launcher_recent),
                                style = MaterialTheme.typography.titleSmallEmphasized,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.padding(
                                    start = 16.dp, top = 12.dp, bottom = 8.dp
                                )
                            )
                            RecentAppCarousel(
                                apps = uiState.recentApps,
                                onLaunchApp = onLaunchApp,
                                modifier = Modifier.padding(bottom = 4.dp)
                            )
                        }
                        LazyVerticalGrid(
                            columns = GridCells.Adaptive(minSize = 100.dp),
                            modifier = Modifier.weight(1f).fillMaxWidth(),
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
                                    },
                                    modifier =
                                        Modifier.animateItem(
                                            placementSpec =
                                                MaterialTheme.motionScheme.fastSpatialSpec()
                                        )
                                )
                            }
                        }
                    }
                }
                }
            }
        }

        // M3 Expressive: floating quick-action toolbar (Refresh + open Connection).
        HorizontalFloatingToolbar(
            expanded = true,
            modifier = Modifier.align(Alignment.BottomCenter)
                .navigationBarsPadding()
                .padding(bottom = 24.dp)
        ) {
            FilledTonalIconButton(onClick = {
                view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                onRefreshApps()
            }) {
                Icon(Default.Refresh, contentDescription = stringResource(R.string.cd_refresh))
            }
            IconButton(onClick = {
                view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                onNavigateToConnection()
            }) {
                Icon(
                    Default.SettingsEthernet,
                    contentDescription = stringResource(R.string.button_connect)
                )
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

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun AppGridItem(
    app: AppEntry,
    shapePreset: Float,
    cornerRadius: Int,
    onClick: () -> Unit,
    modifier: Modifier = Modifier
) {
    val shape = cardShape(shapePreset, cornerRadius)
    val adaptivePadding = calculateAdaptivePadding(shapePreset)

    // Tactile spring press-scale on tap.
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val scale by animateFloatAsState(
        targetValue = if (pressed) 0.94f else 1f,
        animationSpec = MaterialTheme.motionScheme.fastSpatialSpec(),
        label = "appTileScale"
    )

    Card(
        onClick = onClick,
        interactionSource = interaction,
        modifier = modifier.graphicsLayer { scaleX = scale; scaleY = scale },
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
                    val bitmap = rememberAppIconBitmap(app.iconBase64)
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
                style = MaterialTheme.typography.labelMediumEmphasized,
                textAlign = TextAlign.Center,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(top = 8.dp)
            )
        }
    }
}

/**
 * M3 Expressive "Recent" strip — a HorizontalMultiBrowseCarousel of recently-launched apps.
 * The keyline layout scales items toward the edges; [maskClip] gives the expressive item masking.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun RecentAppCarousel(
    apps: List<AppEntry>,
    onLaunchApp: (AppEntry) -> Unit,
    modifier: Modifier = Modifier
) {
    val view = LocalView.current
    val carouselState = rememberCarouselState { apps.size }
    HorizontalMultiBrowseCarousel(
        state = carouselState,
        preferredItemWidth = 140.dp,
        itemSpacing = 10.dp,
        contentPadding = PaddingValues(horizontal = 16.dp),
        modifier = modifier.fillMaxWidth().height(150.dp)
    ) { i ->
        val app = apps[i]
        Box(
            modifier = Modifier
                .height(150.dp)
                .maskClip(MaterialTheme.shapes.extraLarge)
                .background(MaterialTheme.colorScheme.surfaceVariant)
                .clickable {
                    view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                    onLaunchApp(app)
                },
            contentAlignment = Alignment.Center
        ) {
            Column(
                modifier = Modifier.padding(12.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center
            ) {
                val bitmap = rememberAppIconBitmap(app.iconBase64)
                if (bitmap != null) {
                    Image(
                        bitmap = bitmap.asImageBitmap(),
                        contentDescription = app.name,
                        modifier = Modifier.size(52.dp)
                    )
                } else {
                    Icon(
                        Icons.AutoMirrored.Filled.Launch,
                        contentDescription = app.name,
                        modifier = Modifier.size(40.dp),
                        tint = MaterialTheme.colorScheme.primary
                    )
                }
                Text(
                    text = app.name,
                    style = MaterialTheme.typography.labelMediumEmphasized,
                    textAlign = TextAlign.Center,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(top = 8.dp)
                )
            }
        }
    }
}

/** Which of the launcher's three body states is showing (drives the AnimatedContent swap). */
private enum class AppLauncherBody { Disconnected, Empty, Apps }

/**
 * Decodes an app icon's base64 payload off the main thread, returning `null` (and thus the
 * fallback launch icon) for a missing or corrupt icon rather than crashing or leaving a blank tile.
 */
@Composable
private fun rememberAppIconBitmap(iconBase64: String?): Bitmap? {
    val state = produceState<Bitmap?>(initialValue = null, key1 = iconBase64) {
        value = if (iconBase64 == null) {
            null
        } else {
            withContext(Dispatchers.Default) {
                try {
                    val bytes = Base64.decode(iconBase64, Base64.DEFAULT)
                    BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                } catch (_: Exception) {
                    null
                }
            }
        }
    }
    return state.value
}
