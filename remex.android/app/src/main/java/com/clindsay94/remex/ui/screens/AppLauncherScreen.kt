package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.ui.platform.LocalView
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
                subtitle = stringResource(R.string.screen_app_launcher_subtitle),
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
                            // Bottom clears the floating Refresh/Connect toolbar below, which
                            // sits on navigationBarsPadding() + 24.dp, so the last row can
                            // scroll fully into view (RemEx-wqo7a.1).
                            contentPadding = PaddingValues(
                                start = 16.dp,
                                top = 16.dp,
                                end = 16.dp,
                                bottom = floatingChromeBottomPadding(
                                    floatingFootprint = LauncherToolbarFootprint,
                                    navBarInset = navigationBarBottomInset()
                                )
                            ),
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
    val adaptivePadding = cardInnerPadding()

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
            LauncherTileLabel(
                name = app.name,
                modifier = Modifier.padding(top = 8.dp)
            )
        }
    }
}

/**
 * How far the floating Refresh/Connect toolbar reaches up from the nav bar: its 24.dp lift plus
 * the toolbar's 64.dp container. The nav-bar inset is added separately by
 * [floatingChromeBottomPadding] (RemEx-wqo7a.1).
 */
private val LauncherToolbarFootprint = 24.dp + 64.dp

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
