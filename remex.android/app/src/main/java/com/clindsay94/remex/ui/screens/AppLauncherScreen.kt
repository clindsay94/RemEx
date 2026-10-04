package com.clindsay94.remex.ui.screens

import android.graphics.BitmapFactory
import android.util.Base64
import android.util.LruCache
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
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.carousel.HorizontalMultiBrowseCarousel
import androidx.compose.material3.carousel.rememberCarouselState
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.Icons.Default
import androidx.compose.material.icons.automirrored.filled.Launch
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.*
import androidx.compose.ui.layout.layout
import com.clindsay94.remex.ui.components.RemexTooltip
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.material3.pulltorefresh.PullToRefreshDefaults
import androidx.compose.material3.pulltorefresh.rememberPullToRefreshState
import androidx.compose.ui.input.nestedscroll.nestedScroll
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.floatingChromeBottomPadding
import com.clindsay94.remex.ui.components.navigationBarBottomInset
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
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Constraints
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.ui.theme.cardInnerPadding
import com.clindsay94.remex.ui.theme.cardShape
import com.clindsay94.remex.ui.theme.shapeSafeArea
import com.clindsay94.remex.ui.components.RemexHapticEvent
import com.clindsay94.remex.ui.components.rememberRemexHaptics
import com.clindsay94.remex.ui.theme.rememberRemexButtonShapes
import com.clindsay94.remex.ui.theme.rememberRemexIconButtonShapes
import androidx.compose.material3.ButtonDefaults

data class AppLauncherUiState(
    val apps: List<AppEntry> = emptyList(),
    val recentApps: List<AppEntry> = emptyList(),
    val shapePreset: Float = 0f,
    val cornerRadius: Int = 8,
    val isConnected: Boolean = false,
    val refreshState: LauncherRefreshState = LauncherRefreshState.Idle,
    /** Connected, but the PC no longer recognises this phone (sweep P3, RemEx-wqo7a.7). */
    val needsPairing: Boolean = false,
) {
    val isRefreshing: Boolean
        get() = refreshState == LauncherRefreshState.Refreshing
}

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
    val refreshState by viewModel.refreshState.collectAsStateWithLifecycle()
    val needsPairing by RemexClientManager.needsPairing.collectAsStateWithLifecycle()

    val uiState = AppLauncherUiState(
        apps = apps,
        recentApps = recentApps,
        shapePreset = shapePreset,
        cornerRadius = cornerRadius,
        isConnected = isConnected,
        refreshState = refreshState,
        needsPairing = isConnected && needsPairing,
    )

    AppLauncherScreenContent(
        uiState = uiState,
        onRefreshApps = { viewModel.refreshApps() },
        onLaunchApp = { viewModel.launchApp(it) },
        onNavigateToConnection = onNavigateToConnection,
        onDismissRefreshError = { viewModel.dismissRefreshError() },
        onPair = {
            ConnectionOpenRequests.requestAddPc()
            onNavigateToConnection()
        },
    )
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun AppLauncherScreenContent(
    uiState: AppLauncherUiState,
    onRefreshApps: () -> Unit,
    onLaunchApp: (AppEntry) -> Unit,
    onNavigateToConnection: () -> Unit,
    modifier: Modifier = Modifier,
    onDismissRefreshError: () -> Unit = {},
    /** Connection > Add a PC, for a PC that needs pairing again. */
    onPair: () -> Unit = onNavigateToConnection,
) {
    val haptics = rememberRemexHaptics()
    val scrollBehavior = rememberRemexTopBarScrollBehavior()
    val refreshLabel = stringResource(R.string.cd_refresh)
    Scaffold(
        modifier = modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
        topBar = {
            RemexFlexibleTopBar(
                title = stringResource(R.string.screen_app_launcher_title),
                subtitle = stringResource(R.string.screen_app_launcher_subtitle),
                scrollBehavior = scrollBehavior,
                // Refresh lives in the top bar (RemEx-wqo7a.6). The floating Refresh/Connect
                // toolbar that used to sit over the grid is gone: pull-to-refresh and this action
                // cover refresh, and the disconnected state has its own way to Connection.
                actions = {
                    RemexTooltip(refreshLabel) {
                        IconButton(
                            onClick = {
                                haptics.perform(RemexHapticEvent.Press)
                                onRefreshApps()
                            },
                            enabled = uiState.isConnected && !uiState.isRefreshing,
                            shapes = rememberRemexIconButtonShapes()
                        ) {
                            Icon(Default.Refresh, contentDescription = refreshLabel)
                        }
                    }
                }
            )
        }
    ) { innerPadding ->
        val pullState = rememberPullToRefreshState()
        PullToRefreshBox(
            isRefreshing = uiState.isRefreshing,
            onRefresh = {
                haptics.perform(RemexHapticEvent.Refresh)
                onRefreshApps()
            },
            modifier = Modifier.fillMaxSize().padding(innerPadding),
            state = pullState,
            indicator = {
                PullToRefreshDefaults.LoadingIndicator(
                    state = pullState,
                    isRefreshing = uiState.isRefreshing,
                    modifier = Modifier.align(Alignment.TopCenter)
                )
            }
        ) {
            // The body states cross-fade instead of hard-swapping (RemEx-svue); tile animateItem
            // and the press spring inside the grid are untouched.
            val launcherBody = appLauncherBodyFor(
                isConnected = uiState.isConnected,
                hasApps = uiState.apps.isNotEmpty(),
                refreshState = uiState.refreshState,
                needsPairing = uiState.needsPairing,
            )
            val bodyFadeSpec = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
            AnimatedContent(
                targetState = launcherBody,
                transitionSpec = { fadeIn(bodyFadeSpec) togetherWith fadeOut(bodyFadeSpec) },
                modifier = Modifier.fillMaxSize(),
                label = "launcher_body",
            ) { body ->
                when (body) {
                    AppLauncherBody.Disconnected ->
                        DisconnectedFullScreen(
                            screenName = stringResource(R.string.screen_app_launcher_title),
                            onNavigateToConnection = onNavigateToConnection,
                            modifier = Modifier.fillMaxSize()
                        )
                    AppLauncherBody.NeedsPairing ->
                        Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                            NeedsPairingContent(onPair = onPair)
                        }
                    AppLauncherBody.Loading ->
                        LauncherMessage(message = stringResource(R.string.app_launcher_loading)) {
                            RemexLoadingIndicator(contained = true)
                        }
                    AppLauncherBody.NoAnswer ->
                        LauncherMessage(
                            message = stringResource(R.string.app_launcher_no_answer),
                            actionLabel = stringResource(R.string.app_launcher_try_again),
                            onAction = {
                                haptics.perform(RemexHapticEvent.Press)
                                onRefreshApps()
                            }
                        ) {
                            Icon(
                                Default.Warning,
                                contentDescription = null /* decorative: the text says it */,
                                modifier = Modifier.size(48.dp),
                                tint = MaterialTheme.colorScheme.error
                            )
                        }
                    AppLauncherBody.Empty ->
                        LauncherMessage(
                            message = stringResource(R.string.app_launcher_no_apps),
                            detail = stringResource(R.string.app_launcher_no_apps_hint),
                            actionLabel = stringResource(R.string.button_fetch_from_host),
                            onAction = {
                                haptics.perform(RemexHapticEvent.Press)
                                onRefreshApps()
                            }
                        ) {
                            Icon(
                                Icons.AutoMirrored.Filled.Launch,
                                contentDescription = null /* decorative: the adjacent Text already says it (RemEx-xqli) */,
                                modifier = Modifier.size(48.dp),
                                tint = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    AppLauncherBody.Apps ->
                        AppGrid(
                            uiState = uiState,
                            onLaunchApp = { app ->
                                haptics.perform(RemexHapticEvent.CommandSent)
                                onLaunchApp(app)
                            },
                            onRetry = onRefreshApps,
                            onDismissRefreshError = onDismissRefreshError
                        )
                }
            }
        }
    }
}

/** The recent-apps strip, the "no answer" banner when the list is stale, and the tile grid. */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun AppGrid(
    uiState: AppLauncherUiState,
    onLaunchApp: (AppEntry) -> Unit,
    onRetry: () -> Unit,
    onDismissRefreshError: () -> Unit
) {
    val dedupedApps = remember(uiState.apps) {
        uiState.apps.distinctBy { "${it.name}|${it.path}" }
    }
    val gridState = androidx.compose.foundation.lazy.grid.rememberLazyGridState()
    // Tapping Apps while on Apps goes back to the top (3.0 comb, no-reselect).
    com.clindsay94.remex.ui.navigation.TabReselectEffect(com.clindsay94.remex.ui.navigation.Screen.AppLauncher) {
        gridState.animateScrollToItem(0)
    }
    LazyVerticalGrid(
        columns = GridCells.Adaptive(minSize = LauncherTileMinSize),
        state = gridState,
        modifier = Modifier.fillMaxSize(),
        // Nothing floats over this grid any more, so the bottom only has to clear the system
        // navigation bar (RemEx-wqo7a.1, RemEx-wqo7a.6).
        contentPadding = PaddingValues(
            start = 16.dp,
            top = 8.dp,
            end = 16.dp,
            bottom = floatingChromeBottomPadding(
                floatingFootprint = 0.dp,
                navBarInset = navigationBarBottomInset()
            )
        ),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        // The list on screen is the last one the PC sent; say so when a refresh went unanswered
        // rather than leaving the person to wonder whether it worked.
        if (uiState.refreshState == LauncherRefreshState.NoAnswer) {
            item(key = "no_answer", span = { GridItemSpan(maxLineSpan) }) {
                LauncherNoAnswerBanner(onRetry = onRetry, onDismiss = onDismissRefreshError)
            }
        }
        // M3 Expressive: a "Recent" multi-browse carousel above the full grid.
        if (uiState.recentApps.isNotEmpty()) {
            item(key = "recent_header", span = { GridItemSpan(maxLineSpan) }) {
                Text(
                    text = stringResource(R.string.app_launcher_recent),
                    style = MaterialTheme.typography.titleSmallEmphasized,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = 4.dp)
                )
            }
            item(key = "recent_carousel", span = { GridItemSpan(maxLineSpan) }) {
                RecentAppCarousel(
                    apps = uiState.recentApps,
                    onLaunchApp = onLaunchApp,
                    modifier = Modifier.padding(bottom = 4.dp)
                )
            }
        }
        items(dedupedApps, key = { "${it.name}|${it.path}" }) { app ->
            AppGridItem(
                app = app,
                shapePreset = uiState.shapePreset,
                cornerRadius = uiState.cornerRadius,
                onClick = { onLaunchApp(app) },
                modifier =
                    Modifier.animateItem(
                        placementSpec = MaterialTheme.motionScheme.fastSpatialSpec()
                    )
            )
        }
    }
}

/** A centred icon, message and optional action: the launcher's loading, empty and error states. */
@Composable
private fun LauncherMessage(
    message: String,
    detail: String? = null,
    actionLabel: String? = null,
    onAction: () -> Unit = {},
    icon: @Composable () -> Unit
) {
    // Scrollable so pull-to-refresh still works on these states: the pull gesture needs a
    // scrolling child to read the overscroll from.
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(12.dp, Alignment.CenterVertically)
    ) {
        icon()
        Text(
            text = message,
            style = MaterialTheme.typography.bodyLarge,
            color = MaterialTheme.colorScheme.onSurface,
            textAlign = TextAlign.Center
        )
        if (detail != null) {
            Text(
                text = detail,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center
            )
        }
        if (actionLabel != null) {
            FilledTonalButton(onClick = onAction, shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.ContentPadding) { Text(actionLabel) }
        }
    }
}

/** Shown above a stale grid when the last refresh got no answer. */
@Composable
private fun LauncherNoAnswerBanner(onRetry: () -> Unit, onDismiss: () -> Unit) {
    Surface(
        color = MaterialTheme.colorScheme.errorContainer,
        contentColor = MaterialTheme.colorScheme.onErrorContainer,
        shape = MaterialTheme.shapes.large,
        modifier = Modifier.fillMaxWidth()
    ) {
        Column(modifier = Modifier.padding(start = 16.dp, top = 12.dp, end = 8.dp, bottom = 4.dp)) {
            Text(
                text = stringResource(R.string.app_launcher_no_answer),
                style = MaterialTheme.typography.bodyMedium
            )
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.End
            ) {
                TextButton(onClick = onDismiss, shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.TextButtonContentPadding) {
                    Text(
                        stringResource(R.string.button_dismiss),
                        color = MaterialTheme.colorScheme.onErrorContainer
                    )
                }
                TextButton(onClick = onRetry, shapes = rememberRemexButtonShapes(), contentPadding = ButtonDefaults.TextButtonContentPadding) {
                    Text(
                        stringResource(R.string.app_launcher_try_again),
                        color = MaterialTheme.colorScheme.onErrorContainer
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
                cornerRadius = 16,
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
    val adaptivePadding = cardInnerPadding()

    // Tactile spring press-scale on tap.
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val scale by animateFloatAsState(
        targetValue = if (pressed) 0.94f else 1f,
        animationSpec = MaterialTheme.motionScheme.fastSpatialSpec(),
        label = "appTileScale"
    )

    // A rounded-square tile on surfaceContainer, the card anatomy every refreshed screen shares
    // (RemEx-wqo7a.6). The corner comes from the same radius token as every other card.
    Card(
        onClick = onClick,
        interactionSource = interaction,
        modifier = modifier.graphicsLayer { scaleX = scale; scaleY = scale },
        shape = shape,
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainer)
    ) {
        Column(
            modifier = Modifier
                .shapeSafeArea(shapePreset)
                .fillMaxWidth()
                .atLeastSquare()
                .padding(adaptivePadding),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center
        ) {
            // Icon wrapped in the card shape for bounded display. The icons are decorative: the
            // label under them already names the app, and the tile is one merged button for
            // TalkBack, so naming it twice would read the name twice.
            Box(
                modifier = Modifier
                    .size(56.dp)
                    .clip(shape),
                contentAlignment = Alignment.Center
            ) {
                val bitmap = rememberAppIconBitmap(app.iconBase64)
                if (bitmap != null) {
                    Image(
                        bitmap = bitmap.asImageBitmap(),
                        contentDescription = null,
                        modifier = Modifier.size(48.dp)
                    )
                } else {
                    Icon(
                        Icons.AutoMirrored.Filled.Launch,
                        contentDescription = null,
                        modifier = Modifier.size(36.dp),
                        tint = MaterialTheme.colorScheme.primary
                    )
                }
            }

            // App name centered below icon — no executable path shown
            LauncherTileLabel(
                name = app.name,
                modifier = Modifier.padding(top = 8.dp)
            )
        }
    }
}

/** The narrowest a launcher tile gets; the grid fits as many columns of at least this as it can. */
private val LauncherTileMinSize = 100.dp

/**
 * Makes a tile at least as tall as it is wide, so a row of short labels still reads as a row of
 * squares. A two-line label that needs more height than that still gets it: the tile grows rather
 * than clipping the text.
 */
private fun Modifier.atLeastSquare(): Modifier = layout { measurable, constraints ->
    val side = if (constraints.hasBoundedWidth) constraints.maxWidth else constraints.minWidth
    val minHeight = side.coerceIn(constraints.minHeight, constraints.maxHeight)
    val placeable = measurable.measure(constraints.copy(minHeight = minHeight))
    layout(placeable.width, placeable.height) { placeable.place(0, 0) }
}

/** Which of the launcher's body states is showing (drives the AnimatedContent swap). */
enum class AppLauncherBody { Disconnected, NeedsPairing, Loading, NoAnswer, Empty, Apps }

/**
 * Picks the launcher's body (RemEx-wqo7a.6). An app list the PC already sent wins, even while
 * disconnected or after an unanswered refresh: the last list is still useful, and the grid shows a
 * banner for the unanswered case. With no list, the state says why in plain words.
 *
 * The one exception is a connected PC that no longer recognises this phone (sweep P3): it refuses
 * every launch, so the list is replaced by "needs pairing" and its Pair action.
 */
internal fun appLauncherBodyFor(
    isConnected: Boolean,
    hasApps: Boolean,
    refreshState: LauncherRefreshState,
    needsPairing: Boolean = false,
): AppLauncherBody = when {
    isConnected && needsPairing -> AppLauncherBody.NeedsPairing
    hasApps -> AppLauncherBody.Apps
    !isConnected -> AppLauncherBody.Disconnected
    refreshState == LauncherRefreshState.Refreshing -> AppLauncherBody.Loading
    refreshState == LauncherRefreshState.NoAnswer -> AppLauncherBody.NoAnswer
    else -> AppLauncherBody.Empty
}

/** Zero-width space: an invisible line-break opportunity. */
private const val LauncherLabelBreak = '​'

/** What separates the label's unbreakable segments: whitespace and the inserted break points. */
private val LauncherLabelSegmentSeparator = Regex("[\\s​]+")

/**
 * The launcher label as DISPLAYED (RemEx-wqo7a.1): the stored app name with an invisible break
 * opportunity after `_`, `-` and `.`, and between a lowercase letter and the uppercase letter that
 * follows it. Executable-style names have no spaces ("BLEACH_Rebirth_of_Souls", "SparkingZERO"),
 * so without these the line breaker had nowhere to wrap them and they ellipsised after a few
 * letters. Only the displayed text changes; the stored name, launch request and search never see it.
 */
internal fun launcherLabelDisplayText(name: String): String = buildString(name.length + 4) {
    name.forEachIndexed { index, c ->
        append(c)
        val next = name.getOrNull(index + 1) ?: return@forEachIndexed
        if (next.isWhitespace() || next == LauncherLabelBreak) return@forEachIndexed
        val afterSeparator = c == '_' || c == '-' || c == '.'
        val camelBoundary = c.isLowerCase() && next.isUpperCase()
        if (afterSeparator || camelBoundary) append(LauncherLabelBreak)
    }
}

/**
 * The font sizes the label tries, largest first: [startSp] down to [floorSp] in [stepSp] steps,
 * always ending exactly on the floor. A style already at or below the floor is used as is.
 */
internal fun launcherLabelFontSizes(startSp: Float, floorSp: Float, stepSp: Float = 0.5f): List<Float> {
    if (startSp <= floorSp || stepSp <= 0f) return listOf(startSp)
    val steps = generateSequence(startSp) { it - stepSp }.takeWhile { it > floorSp + 0.001f }.toList()
    return steps + floorSp
}

/** How a launcher tile lays out its app name: see [launcherLabelLayout]. */
internal data class LauncherLabelLayout(val fontSizeSp: Float, val maxLines: Int, val softWrap: Boolean)

/**
 * The launcher tile label's fitting policy (RemEx-wqo7a.1).
 *
 * Android's line breaker splits a segment that is wider than the line at whatever character runs
 * out of room, which is how "BCUninstaller" became "BCUninstall / er". So the label only wraps
 * when every segment (see [launcherLabelDisplayText]) fits on a line by itself; then every break
 * lands on a word or separator boundary.
 *
 * For each size in [fontSizesSp] (largest first), the first one at which every segment fits and the
 * whole label takes at most two lines wins. If none does, the label uses the floor (the last size):
 * two lines ellipsised at the end when every segment fits there, otherwise one unwrapped line
 * ellipsised at the end, never a mid-word break.
 *
 * Pure so the policy is testable without a renderer: [segmentFits] measures one segment on one
 * line, and [lineCount] measures how many lines the soft-wrapped text needs at a size.
 */
internal fun launcherLabelLayout(
    displayText: String,
    fontSizesSp: List<Float>,
    segmentFits: (segment: String, fontSizeSp: Float) -> Boolean,
    lineCount: (text: String, fontSizeSp: Float) -> Int,
): LauncherLabelLayout {
    require(fontSizesSp.isNotEmpty()) { "The launcher label needs at least one font size." }
    val segments = displayText.split(LauncherLabelSegmentSeparator).filter { it.isNotEmpty() }
    for (size in fontSizesSp) {
        if (segments.all { segmentFits(it, size) } && lineCount(displayText, size) <= 2) {
            return LauncherLabelLayout(fontSizeSp = size, maxLines = 2, softWrap = true)
        }
    }
    val floor = fontSizesSp.last()
    return if (segments.all { segmentFits(it, floor) }) {
        LauncherLabelLayout(fontSizeSp = floor, maxLines = 2, softWrap = true)
    } else {
        LauncherLabelLayout(fontSizeSp = floor, maxLines = 1, softWrap = false)
    }
}

/** The app name under a launcher tile's icon, laid out by [launcherLabelLayout]. */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun LauncherTileLabel(name: String, modifier: Modifier = Modifier) {
    val style = MaterialTheme.typography.labelMediumEmphasized
    // Never shrink below labelSmall: smaller than that stops being readable on a tile.
    val floorSize = MaterialTheme.typography.labelSmall.fontSize
    val measurer = rememberTextMeasurer()
    val displayText = remember(name) { launcherLabelDisplayText(name) }
    BoxWithConstraints(modifier = modifier, contentAlignment = Alignment.Center) {
        val maxWidthPx = constraints.maxWidth
        val bounded = constraints.hasBoundedWidth
        // Only an sp size can step down; any other unit keeps the style's own size.
        val canStep = style.fontSize.isSp && floorSize.isSp
        fun styleAt(sizeSp: Float) = if (canStep) style.copy(fontSize = sizeSp.sp) else style
        val layout = remember(displayText, style, floorSize, maxWidthPx, bounded) {
            val sizes =
                if (canStep) {
                    launcherLabelFontSizes(style.fontSize.value, floorSize.value)
                } else {
                    listOf(style.fontSize.value)
                }
            launcherLabelLayout(
                displayText = displayText,
                fontSizesSp = sizes,
                segmentFits = { segment, size ->
                    !bounded ||
                        measurer.measure(
                            segment,
                            style = styleAt(size),
                            maxLines = 1,
                            softWrap = false
                        ).size.width <= maxWidthPx
                },
                lineCount = { text, size ->
                    if (!bounded) {
                        1
                    } else {
                        measurer.measure(
                            text,
                            style = styleAt(size),
                            softWrap = true,
                            constraints = Constraints(maxWidth = maxWidthPx)
                        ).lineCount
                    }
                }
            )
        }
        Text(
            text = displayText,
            style = styleAt(layout.fontSizeSp),
            textAlign = TextAlign.Center,
            maxLines = layout.maxLines,
            softWrap = layout.softWrap,
            overflow = TextOverflow.Ellipsis
        )
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
    val haptics = rememberRemexHaptics()
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
                    haptics.perform(RemexHapticEvent.CommandSent)
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


/**
 * Decoded app icons, keyed by the base64 string they came from (perf audit P3-17).
 *
 * The "Recent" carousel and the full grid both render the same [AppEntry] (a recently-launched
 * app shows in both), and each is its own `rememberAppIconBitmap` call site — without a shared
 * cache the identical icon payload was decoded twice, and again on every screen re-entry since
 * `produceState` alone doesn't survive leaving composition. Bounded by decoded bytes, same
 * pattern as `FileManagerFileItem`'s `ThumbnailBitmapCache` (P3-13).
 */
private object AppIconBitmapCache {
    private const val MAX_BYTES = 4 * 1024 * 1024

    private val cache = object : LruCache<String, Bitmap>(MAX_BYTES) {
        override fun sizeOf(key: String, value: Bitmap): Int = value.allocationByteCount
    }

    fun get(base64: String): Bitmap? = cache.get(base64)

    fun put(base64: String, bitmap: Bitmap) {
        cache.put(base64, bitmap)
    }
}

/** The result of one off-main decode, tagged with the key it was decoded from. */
private class DecodedAppIcon(val base64: String, val bitmap: Bitmap?)

/**
 * Decodes an app icon's base64 payload off the main thread, returning `null` (and thus the
 * fallback launch icon) for a missing or corrupt icon rather than crashing or leaving a blank tile.
 * A cache hit returns synchronously so the grid tile and the carousel tile for the same app share
 * one decoded [Bitmap] instead of each decoding it independently.
 */
@Composable
private fun rememberAppIconBitmap(iconBase64: String?): Bitmap? {
    if (iconBase64 == null) return null
    AppIconBitmapCache.get(iconBase64)?.let { return it }
    val decoded by produceState<DecodedAppIcon?>(initialValue = null, key1 = iconBase64) {
        val bitmap = withContext(Dispatchers.Default) {
            try {
                val bytes = Base64.decode(iconBase64, Base64.DEFAULT)
                BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
            } catch (_: Exception) {
                null
            }
        }
        if (bitmap != null) AppIconBitmapCache.put(iconBase64, bitmap)
        value = DecodedAppIcon(iconBase64, bitmap)
    }
    // produceState keeps its previous value until the restarted producer writes, so an icon that
    // just changed key must not show the OLD key's bitmap for that window.
    return decoded?.takeIf { it.base64 == iconBase64 }?.bitmap
}
