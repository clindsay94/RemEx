package com.clindsay94.remex.ui.screens

import android.content.pm.ActivityInfo
import android.view.HapticFeedbackConstants
import android.content.Context
import android.view.MotionEvent
import android.view.inputmethod.InputMethodManager
import androidx.activity.compose.LocalActivity
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.AnimatedVisibilityScope
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animate
import androidx.compose.animation.core.spring
import android.content.res.Configuration
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.foundation.Canvas
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.focusRequester

import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.layout
import androidx.compose.ui.unit.Constraints
import androidx.compose.ui.input.pointer.*
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.components.RemexTooltip
import com.clindsay94.remex.ui.theme.RemExTheme
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.compose.ui.viewinterop.AndroidView
import android.util.Log
import com.clindsay94.remex.R
import kotlin.math.abs
import kotlin.math.roundToInt
import kotlin.math.sqrt
import kotlinx.coroutines.Job
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.launch

private const val TAG = "RemoteDesktopScreen"

// Fullscreen top-end overlay row spacing (RemEx-klq) — named so every icon button in the row is
// provably consistent rather than relying on repeated inline literals.
private val FullscreenOverlayEdgePadding = 16.dp
private val FullscreenOverlayIconSpacing = 8.dp

// How far the screenshot confirmation lifts itself when the PC-keys lane is showing (RemEx-byij).
// Both are anchored to the bottom of the video box, and the lane carries the modifier latch states —
// covering those with a transient message would read as Ctrl/Alt/Shift having come unstuck.
private val PcKeysLaneClearance = 72.dp

// Gesture timing thresholds (ms)
private const val TAP_MAX_DURATION_MS = 250L
private const val LONG_PRESS_THRESHOLD_MS = 500L
private const val DOUBLE_TAP_WINDOW_MS = 300L

// Movement thresholds (dp, converted to px at runtime)
private const val FINGER_SLOP_DP = 8f
private const val STYLUS_SLOP_DP = 4f
private const val DOUBLE_TAP_RADIUS_DP = 24f

// Inertia parameters
private const val INERTIA_MIN_VELOCITY = 2f // dp/frame minimum to start inertia
private const val INERTIA_STOP_VELOCITY = 0.5f // dp/frame to stop
private const val INERTIA_DECAY = 0.93f // velocity multiplier per frame
private const val INERTIA_FRAME_MS = 16L // ~60fps

// Throttle interval for move events
private const val MOVE_THROTTLE_MS = 33L // ~30Hz

/**
 * The four inputs pan-follow reacts to, combined so ONE collector can watch them all.
 *
 * Exists because the alternative is a `LaunchedEffect` keyed on the cursor position, which the host
 * streams 60-90 times a second — so the effect was torn down and relaunched on every packet
 * (RemEx-zc9r).
 */
private data class PanFollowInput(
        val cursorX: Float,
        val cursorY: Float,
        val cursorVisible: Boolean,
        val zoom: Float,
)

/** Stores context about the last completed tap for double-tap detection. */
private data class TapContext(
        val time: Long, // uptimeMillis
        val position: Offset,
        val isStylus: Boolean
)

/** The rectangle (within the view box) that the video content actually occupies: size + top-left
 *  offset. H.264 fills the whole box (stretched TextureView); MJPEG is letterboxed via Fit. */
private data class ContentRect(val w: Float, val h: Float, val x: Float, val y: Float)

data class RemoteDesktopUiState(
        val isStreaming: Boolean = false,
        val capabilityState: RemoteDesktopCapabilityState = RemoteDesktopCapabilityState(),
        val desktopError: String? = null,
        val directTouch: Boolean = false,
        val pointerSpeed: Float = 1.0f,
        val vScrollSensitivity: Float = 1.0f,
        val hScrollSensitivity: Float = 1.0f,
        val cursorScale: Float = 1.0f,
        val hostCursorVisible: Boolean = false,
        val cursorBitmap: ImageBitmap? = null,
        val cursorHotspotX: Int = 0,
        val cursorHotspotY: Int = 0,
        val isFullscreen: Boolean = false,
        val displayTargets: List<DisplayTargetOption> = emptyList(),
        val selectedDisplayToken: String = "",
        // True from the moment the user taps Start until they stop or the connection fails. Drives an
        // immediate rotation to landscape on tap, before the stream is actually up.
        val streamRequested: Boolean = false
)

/**
 * Whether the idle screen offers its start action (RemEx-5k4dd).
 *
 * **AN ERROR IS A DIFFERENT STATE FROM AN UNSUPPORTED HOST**, and conflating them is what made this
 * screen a dead end. Hiding the action on a host that genuinely cannot stream is right. Hiding it
 * when a stream FAILED left the user with an error message under a monitor icon and nothing to
 * press, because several failure paths also clear the capability flag - so the one thing they would
 * naturally try was the one thing not offered.
 *
 * Extracted rather than left inline because the interesting part is the truth table, and the case
 * that must NOT regress - an unsupported host with no error, still hidden - is invisible in a
 * composable and provable here.
 */
internal fun shouldOfferStartAction(
    isStreaming: Boolean,
    supportsRemoteDesktop: Boolean,
    hasError: Boolean
): Boolean = !isStreaming && (supportsRemoteDesktop || hasError)


// Workaround: calling AnimatedVisibility inside a Box that lives inside a Column
// causes Kotlin overload resolution to bind to ColumnScope.AnimatedVisibility, which
// @LayoutScopeMarker then rejects. A plain composable body resolves to the top-level overload.
@Composable
private fun PlainAnimatedVisibility(
        visible: Boolean,
        modifier: Modifier = Modifier,
        enter: androidx.compose.animation.EnterTransition = fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
        exit: androidx.compose.animation.ExitTransition = fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
        content: @Composable AnimatedVisibilityScope.() -> Unit
) {
        AnimatedVisibility(
                visible = visible,
                modifier = modifier,
                enter = enter,
                exit = exit,
                content = content
        )
}

/** Localized display label for a remote-desktop quality preset (RemEx-vj31). */
@Composable
private fun desktopPresetLabel(preset: DesktopPreset): String =
        when (preset) {
                DesktopPreset.UNLIMITED -> stringResource(R.string.remote_desktop_preset_unlimited)
                DesktopPreset.SMOOTH_SHARP ->
                        stringResource(R.string.remote_desktop_preset_smooth_sharp)
                DesktopPreset.BALANCED -> stringResource(R.string.remote_desktop_preset_balanced)
                DesktopPreset.DATA_SAVER -> stringResource(R.string.remote_desktop_preset_data_saver)
                DesktopPreset.CUSTOM -> stringResource(R.string.remote_desktop_preset_custom)
        }

/** Small uppercase-ish section heading used to group controls in the settings sheet. */
@Composable
private fun SettingsSectionHeader(text: String) {
        Text(
                text = text,
                style = MaterialTheme.typography.labelLargeEmphasized,
                color = MaterialTheme.colorScheme.primary
        )
}

/** A compact labeled slider for the settings sheet. The label sits directly above the slider so a
 *  pair of these can sit side-by-side in landscape without wasting vertical space. */
@Composable
private fun SettingSlider(
        label: String,
        value: Float,
        onValueChange: (Float) -> Unit,
        valueRange: ClosedFloatingPointRange<Float>,
        modifier: Modifier = Modifier,
        steps: Int = 0
) {
        Column(modifier = modifier, verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(
                        label,
                        style = MaterialTheme.typography.bodyMediumEmphasized,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                )
                Slider(
                        value = value,
                        onValueChange = onValueChange,
                        valueRange = valueRange,
                        steps = steps
                )
        }
}

/** Lays two settings controls out side-by-side in landscape (to use the wide aspect ratio) and
 *  stacked in portrait. Each slot is handed the Modifier it should apply (weight vs fillMaxWidth). */
@Composable
private fun SettingsPair(
        isLandscape: Boolean,
        first: @Composable (Modifier) -> Unit,
        second: @Composable (Modifier) -> Unit
) {
        if (isLandscape) {
                Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(24.dp)
                ) {
                        first(Modifier.weight(1f))
                        second(Modifier.weight(1f))
                }
        } else {
                Column(verticalArrangement = Arrangement.spacedBy(16.dp)) {
                        first(Modifier.fillMaxWidth())
                        second(Modifier.fillMaxWidth())
                }
        }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RemoteDesktopScreen(viewModel: RemoteDesktopViewModel = viewModel()) {
        val currentBitmap by viewModel.currentBitmap.collectAsStateWithLifecycle()
        val isStreaming by viewModel.isStreaming.collectAsStateWithLifecycle()
        val capabilityState by viewModel.capabilityState.collectAsStateWithLifecycle()
        val desktopError by viewModel.desktopError.collectAsStateWithLifecycle()
        val config by viewModel.configState.collectAsStateWithLifecycle()
        val directTouch by viewModel.directTouch.collectAsStateWithLifecycle()
        val pointerSpeed by viewModel.pointerSpeed.collectAsStateWithLifecycle()
        val vScrollSensitivity by viewModel.verticalScrollSensitivity.collectAsStateWithLifecycle()
        val hScrollSensitivity by viewModel.horizontalScrollSensitivity.collectAsStateWithLifecycle()
        val cursorScale by viewModel.cursorScale.collectAsStateWithLifecycle()
        val hostCursorVisible by viewModel.hostCursorVisible.collectAsStateWithLifecycle()
        val cursorBitmap by viewModel.cursorShapeBitmap.collectAsStateWithLifecycle()
        val cursorHotspotX by viewModel.cursorHotspotX.collectAsStateWithLifecycle()
        val cursorHotspotY by viewModel.cursorHotspotY.collectAsStateWithLifecycle()
        val fps by viewModel.fps.collectAsStateWithLifecycle()
        val windowResults by viewModel.windowResults.collectAsStateWithLifecycle()
        val windowActionError by viewModel.windowActionError.collectAsStateWithLifecycle()
        val activeCodec by viewModel.activeCodecState.collectAsStateWithLifecycle()
        val streamPixelWidth by viewModel.streamPixelWidth.collectAsStateWithLifecycle()
        val streamPixelHeight by viewModel.streamPixelHeight.collectAsStateWithLifecycle()
        val desktopMetaReady by viewModel.desktopMetaReady.collectAsStateWithLifecycle()
        val displayTargets by viewModel.displayTargets.collectAsStateWithLifecycle()
        val selectedDisplayToken by viewModel.selectedDisplayToken.collectAsStateWithLifecycle()
        val modifierStates by viewModel.modifierStates.collectAsStateWithLifecycle()
        val hasShownUnlimitedWarning by viewModel.hasShownUnlimitedWarning.collectAsStateWithLifecycle()
        val screenshotStatus by viewModel.screenshotStatus.collectAsStateWithLifecycle()

        var isFullscreen by rememberSaveable { mutableStateOf(false) }
        var showFpsOverlay by rememberSaveable { mutableStateOf(false) }
        var streamRequested by rememberSaveable { mutableStateOf(false) }

        // Drop the landscape request if the stream never came up (connection/host error).
        LaunchedEffect(desktopError) {
                if (desktopError != null) streamRequested = false
        }

        val uiState =
                RemoteDesktopUiState(
                        isStreaming = isStreaming,
                        capabilityState = capabilityState,
                        desktopError = desktopError,
                        directTouch = directTouch,
                        pointerSpeed = pointerSpeed,
                        vScrollSensitivity = vScrollSensitivity,
                        hScrollSensitivity = hScrollSensitivity,
                        cursorScale = cursorScale,
                        hostCursorVisible = hostCursorVisible,
                        cursorBitmap = cursorBitmap,
                        cursorHotspotX = cursorHotspotX,
                        cursorHotspotY = cursorHotspotY,
                        isFullscreen = isFullscreen,
                        displayTargets = displayTargets,
                        selectedDisplayToken = selectedDisplayToken,
                        streamRequested = streamRequested
                )

        RemoteDesktopScreenContent(
                uiState = uiState,
                // Passed as a Flow, not collected here: collecting would put the position back into
                // composition, which is exactly what RemEx-zc9r removed.
                hostCursor = viewModel.hostCursor,
                currentBitmap = currentBitmap,
                config = config,
                onSetFullscreen = { isFullscreen = it },
                onStartStreaming = {
                        streamRequested = true
                        viewModel.startStreaming()
                },
                onStopStreaming = {
                        streamRequested = false
                        viewModel.stopStreaming()
                },
                onSendText = { viewModel.sendText(it) },
                onSendKeyPress = { viewModel.sendKeyPress(it) },
                modifierStates = modifierStates,
                onCycleModifier = { viewModel.cycleModifier(it) },
                onSendMouseDown = { b, x, y -> viewModel.sendMouseDown(b, x, y) },
                onSendMouseClick = { b -> viewModel.sendMouseClick(b) },
                onSendMouseUp = { b -> viewModel.sendMouseUp(b) },
                onSendMouseMove = { x, y -> viewModel.sendMouseMove(x, y) },
                onSendMouseAbsolute = { x, y -> viewModel.sendMouseAbsolute(x, y) },
                onSendMouseAbsoluteClick = { b, x, y -> viewModel.sendMouseAbsoluteClick(b, x, y) },
                onSendMouseScroll = { x, y -> viewModel.sendMouseScroll(x, y) },
                onSendPointerBatch = { viewModel.sendPointerBatch(it) },
                onUpdateQuality = { viewModel.updateQuality(it) },
                onUpdateTargetFps = { viewModel.updateTargetFps(it) },
                onUpdateScale = { viewModel.updateScale(it) },
                onApplyPreset = { preset, q, f, s -> viewModel.applyDesktopPreset(preset, q, f, s) },
                onSelectCustomPreset = { viewModel.selectCustomPreset() },
                hasShownUnlimitedWarning = hasShownUnlimitedWarning,
                onMarkUnlimitedWarningShown = { viewModel.markUnlimitedWarningShown() },
                onUpdateDirectTouch = { viewModel.updateDirectTouch(it) },
                onUpdatePointerSpeed = { viewModel.updatePointerSpeed(it) },
                onUpdateScrollSensitivity = { v, h -> viewModel.updateScrollSensitivity(v, h) },
                onUpdateCursorScale = { viewModel.updateCursorScale(it) },
                onSelectDisplayTarget = { viewModel.selectDisplayTarget(it) },
                windowResults = windowResults,
                windowActionError = windowActionError,
                onQueryWindows = { viewModel.queryWindows(it) },
                onActivateWindow = { viewModel.activateWindow(it) },
                onRaiseWindow = { viewModel.raiseWindow(it) },
                onMinimizeWindow = { viewModel.minimizeWindow(it) },
                onCloseWindow = { viewModel.closeWindow(it) },
                onResizeWindow = { id, width, height -> viewModel.resizeWindow(id, width, height) },
                onMoveWindowToDesktop = { id, desktop ->
                        viewModel.moveWindowToDesktop(id, desktop)
                },
                getHostScreenSize = { viewModel.getHostScreenSize() },
                getHostDesktopOffset = { viewModel.getHostDesktopOffset() },
                frameTick = viewModel.frameTick,
                fps = fps,
                showFpsOverlay = showFpsOverlay,
                onToggleFpsOverlay = { showFpsOverlay = !showFpsOverlay },
                screenshotStatus = screenshotStatus,
                onTakeScreenshot = { viewModel.takeScreenshot() },
                activeCodec = activeCodec,
                streamPixelWidth = streamPixelWidth,
                streamPixelHeight = streamPixelHeight,
                desktopMetaReady = desktopMetaReady,
                onActiveH264DecoderChange = { decoder -> viewModel.activeH264Decoder = decoder },
                onH264DecoderReleased = { decoder -> viewModel.onH264DecoderReleased(decoder) },
                onH264DecoderInitFailed = { viewModel.onH264DecoderInitFailed() },
                onH264KeyframeNeeded = { viewModel.requestKeyframe() }
        )
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalComposeUiApi::class)
@Composable
fun RemoteDesktopScreenContent(
        uiState: RemoteDesktopUiState,
        /**
         * The host cursor position, as a STREAM rather than as state.
         *
         * Deliberately not part of [uiState]: the host sends 60-90 of these a second, and anything in
         * uiState recomposes this whole screen on every one. A Flow parameter is stable, so emissions
         * reach the one collector that wants them and nothing else notices (RemEx-zc9r).
         */
        hostCursor: kotlinx.coroutines.flow.Flow<HostCursorSample>,
        currentBitmap: android.graphics.Bitmap?,
        config: RemoteDesktopConfigState,
        onSetFullscreen: (Boolean) -> Unit,
        onStartStreaming: () -> Unit,
        onStopStreaming: () -> Unit,
        onSendText: (String) -> Unit,
        onSendKeyPress: (Int) -> Unit,
        modifierStates: Map<Int, ModifierState> = emptyMap(),
        onCycleModifier: (Int) -> Unit = {},
        onSendMouseDown: (Int, Int?, Int?) -> Unit,
        onSendMouseClick: (Int) -> Unit,
        onSendMouseUp: (Int) -> Unit,
        onSendMouseMove: (Float, Float) -> Unit,
        onSendMouseAbsolute: (Int, Int) -> Unit,
        onSendMouseAbsoluteClick: (Int, Int, Int) -> Unit,
        onSendMouseScroll: (Int, Int) -> Unit,
        onSendPointerBatch: (String) -> Unit = {},
        onUpdateQuality: (Int) -> Unit,
        onUpdateTargetFps: (Int) -> Unit,
        onUpdateScale: (Float) -> Unit = {},
        onApplyPreset: (DesktopPreset, Int, Int, Float) -> Unit = { _, _, _, _ -> },
        onSelectCustomPreset: () -> Unit = {},
        hasShownUnlimitedWarning: Boolean = false,
        onMarkUnlimitedWarningShown: () -> Unit = {},
        onUpdateDirectTouch: (Boolean) -> Unit,
        onUpdatePointerSpeed: (Float) -> Unit,
        onUpdateScrollSensitivity: (Float, Float) -> Unit,
        onUpdateCursorScale: (Float) -> Unit = {},
        onSelectDisplayTarget: (String) -> Unit = {},
        windowResults: List<DesktopWindowModel>,
        windowActionError: String?,
        onQueryWindows: (String) -> Unit,
        onActivateWindow: (String) -> Unit,
        onRaiseWindow: (String) -> Unit,
        onMinimizeWindow: (String) -> Unit,
        onCloseWindow: (String) -> Unit,
        onResizeWindow: (String, Int, Int) -> Unit,
        onMoveWindowToDesktop: (String, Int) -> Unit,
        getHostScreenSize: () -> Pair<Int, Int>,
        getHostDesktopOffset: () -> Pair<Int, Int> = { Pair(0, 0) },
        frameTick: androidx.compose.runtime.LongState,
        fps: Float = 0f,
        showFpsOverlay: Boolean = false,
        onToggleFpsOverlay: () -> Unit = {},
        screenshotStatus: String? = null,
        onTakeScreenshot: () -> Unit = {},
        activeCodec: String = "Mjpeg",
        streamPixelWidth: Int = 1920,
        streamPixelHeight: Int = 1080,
        desktopMetaReady: Boolean = false,
        onActiveH264DecoderChange: (H264StreamDecoder?) -> Unit = {},
        onH264DecoderReleased: (H264StreamDecoder) -> Unit = {},
        onH264DecoderInitFailed: () -> Unit = {},
        onH264KeyframeNeeded: () -> Unit = {}
) {
        val activity = LocalActivity.current
        val scope = rememberCoroutineScope()
        val density = LocalDensity.current
        val view = LocalView.current

        // Last non-null screenshot outcome, kept so the status pill still has words to show while it
        // fades out. Binding the Text to [screenshotStatus] directly would empty it the moment the
        // ViewModel cleared the flow, and the fade would play on a blank pill (RemEx-byij).
        var lastScreenshotStatus by remember { mutableStateOf<String?>(null) }
        LaunchedEffect(screenshotStatus) {
                if (screenshotStatus != null) lastScreenshotStatus = screenshotStatus
        }

        var showSettings by remember { mutableStateOf(false) }
        // Skip the half-expanded state: in landscape the partial sheet is too short to reveal the
        // input sliders, and the content is scrollable anyway. Always open fully expanded.
        val sheetState = rememberBottomSheetState(initialValue = SheetValue.Hidden, enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded))

        var zoomFactor by remember { mutableFloatStateOf(1f) }
        var panOffsetX by remember { mutableFloatStateOf(0f) }
        var panOffsetY by remember { mutableFloatStateOf(0f) }
        // RD-A3: tracks which (displayToken, isLandscape) pair has already received the initial fit.
        // Empty = no fit applied. Resets on stream stop; re-fits on display switch or orientation change.
        var lastFitKey by remember { mutableStateOf("") }
        // While the user is manually panning/pinching, suppress cursor pan-follow so the two do
        // not fight. Set to now+cooldown on every manual pan write; pan-follow skips until then.
        var suppressPanFollowUntilMs by remember { mutableLongStateOf(0L) }
        var cursorX by remember { mutableFloatStateOf(0f) }
        var cursorY by remember { mutableFloatStateOf(0f) }
        var imageSize by remember { mutableStateOf(IntSize.Zero) }

        var isStylusActive by remember { mutableStateOf(false) }
        var inputResetTrigger by remember { mutableIntStateOf(0) }
        var cursorVisible by remember { mutableStateOf(false) }
        var windowSearch by rememberSaveable { mutableStateOf("") }
        var selectedWindowId by rememberSaveable { mutableStateOf<String?>(null) }
        var resizeWidthText by rememberSaveable { mutableStateOf("1280") }
        var resizeHeightText by rememberSaveable { mutableStateOf("720") }
        var targetDesktopText by rememberSaveable { mutableStateOf("1") }
        val selectedWindow = windowResults.firstOrNull { it.id == selectedWindowId }

        // Keyboard support
        val focusRequester = remember { FocusRequester() }
        val keyboardController = LocalSoftwareKeyboardController.current
        var textValue by remember { mutableStateOf(TextFieldValue("")) }
        val isRemoteKeyboardOpen = WindowInsets.ime.getBottom(LocalDensity.current) > 0

        // "PC keys" bar (Ctrl/Alt/Shift/Win + Esc/Tab/arrows/Del) is independent of the soft
        // keyboard: it can be shown on its own via the toolbar toggle below, and it also shows
        // automatically whenever the IME is open (see the AnimatedVisibility condition further
        // down). Dismissing the IME no longer hides it if the user explicitly opened it (RemEx-yi8o).
        var pcKeysBarVisible by rememberSaveable { mutableStateOf(false) }

        // Expand/collapse state for the F-key/nav-key grid revealed below the compact PC-keys row
        // (RemEx-bct remnant): F1-F12, Home/End/PgUp/PgDn, Insert.
        var extraKeysExpanded by rememberSaveable { mutableStateOf(false) }

        // Floating PC-keys pill drag offset, relative to its bottom-center anchor (RemEx-2y31).
        // Persisted across rotation/recreation; re-clamped to the current overlay-lane bounds at
        // apply time, so a position dragged in landscape self-heals after rotating to portrait.
        var pcKeysOffsetX by rememberSaveable { mutableFloatStateOf(0f) }
        var pcKeysOffsetY by rememberSaveable { mutableFloatStateOf(0f) }
        var pcKeysLaneSize by remember { mutableStateOf(IntSize.Zero) }
        var pcKeysPillSize by remember { mutableStateOf(IntSize.Zero) }

        // Robust keyboard toggle reused by every keyboard button. The hidden BasicTextField keeps
        // Compose focus after a back-gesture IME dismiss, so a bare requestFocus()/show() no-ops and
        // the keyboard never reopens (the RemEx-46q symptom: "loses the ability to come up"). Force
        // the IME up via the platform InputMethodManager as a belt-and-braces so it appears every
        // time, however many times it is invoked. (RemEx-46q)
        val toggleRemoteKeyboard: () -> Unit = {
            try {
                if (!isRemoteKeyboardOpen) {
                    focusRequester.requestFocus()
                    keyboardController?.show()
                    // Flags 0, not SHOW_IMPLICIT: that flag was deprecated in API 37 (it has no
                    // effect at all from Android B onward) and its documented replacement is to call
                    // showSoftInput without it. Dropping it also matches the intent here — this is an
                    // explicit request triggered by the user tapping the keyboard button.
                    (view.context.getSystemService(Context.INPUT_METHOD_SERVICE) as? InputMethodManager)
                        ?.showSoftInput(view, 0)
                } else {
                    keyboardController?.hide()
                }
            } catch (_: Exception) {}
        }

        // Inertia job reference (cancelled on new touch)
        var inertiaJob by remember { mutableStateOf<Job?>(null) }

        // Precompute slop thresholds in pixels
        val fingerSlopPx = with(density) { FINGER_SLOP_DP.dp.toPx() }
        val stylusSlopPx = with(density) { STYLUS_SLOP_DP.dp.toPx() }
        val doubleTapRadiusPx = with(density) { DOUBLE_TAP_RADIUS_DP.dp.toPx() }
        val inertiaMinVelPx = with(density) { INERTIA_MIN_VELOCITY.dp.toPx() }
        val inertiaStopVelPx = with(density) { INERTIA_STOP_VELOCITY.dp.toPx() }

        // These are read inside pointerInput coroutines which don't see recomposition.
        // rememberUpdatedState ensures the coroutine always reads the latest value.
        val pointerSpeedState = rememberUpdatedState(uiState.pointerSpeed)
        val hScrollSensState = rememberUpdatedState(uiState.hScrollSensitivity)
        val vScrollSensState = rememberUpdatedState(uiState.vScrollSensitivity)

        DisposableEffect(activity, uiState.isFullscreen, uiState.isStreaming, uiState.streamRequested) {
                if (activity == null) {
                        onDispose {}
                } else {
                        val window = activity.window
                        val controller = WindowInsetsControllerCompat(window, window.decorView)
                        // Always edge-to-edge, matching MainActivity.enableEdgeToEdge(). With
                        // decor-fits TRUE (the old non-fullscreen path) the window itself resizes
                        // when the IME opens (pre-API-35 behavior), shrinking the video box —
                        // imageSize keys the H.264 SurfaceView, so that tore down the decoder and
                        // blacked the stream until the next keyframe (RemEx-2y31). With decor-fits
                        // FALSE the window never resizes: IME insets flow to Compose, where only
                        // the floating PC-keys pill's overlay lane consumes them.
                        WindowCompat.setDecorFitsSystemWindows(window, false)
                        if (uiState.isFullscreen) {
                                controller.hide(WindowInsetsCompat.Type.systemBars())
                                controller.systemBarsBehavior =
                                        WindowInsetsControllerCompat
                                                .BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                        } else {
                                controller.show(WindowInsetsCompat.Type.systemBars())
                        }
                        // Honor the device's orientation like every other screen. Forced landscape
                        // was a crutch for the pre-pan-follow era (a zoomed portrait view stranded
                        // the cursor off-screen); pan-follow now keeps the cursor on screen, so RD
                        // no longer locks orientation.
                        activity.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
                        onDispose {
                                // Restore the app-wide edge-to-edge state (MainActivity calls
                                // enableEdgeToEdge()); the old restore-to-TRUE silently disabled
                                // edge-to-edge for every other screen after visiting RD.
                                WindowCompat.setDecorFitsSystemWindows(window, false)
                                controller.show(WindowInsetsCompat.Type.systemBars())
                                activity.requestedOrientation =
                                        ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
                        }
                }
        }

        // The rectangle the video content occupies within the view box. BOTH codecs letterbox to
        // preserve the source aspect ratio — H.264 via Modifier.aspectRatio on the TextureView,
        // MJPEG via ContentScale.Fit — so this is the same letterboxed region for both. Input
        // mapping, cursor overlay, and the video must all agree on this rect or coordinates drift.
        fun contentRect(): ContentRect {
                if (imageSize.width == 0 || imageSize.height == 0) {
                        return ContentRect(0f, 0f, 0f, 0f)
                }
                val bmpWidth =
                        (if (streamPixelWidth > 0) streamPixelWidth
                        else currentBitmap?.width ?: 1920).toFloat()
                val bmpHeight =
                        (if (streamPixelHeight > 0) streamPixelHeight
                        else currentBitmap?.height ?: 1080).toFloat()
                val bmpAspect = bmpWidth / bmpHeight
                val boxAspect = imageSize.width.toFloat() / imageSize.height.toFloat()
                return if (bmpAspect > boxAspect) {
                        val ew = imageSize.width.toFloat()
                        val eh = ew / bmpAspect
                        ContentRect(ew, eh, 0f, (imageSize.height - eh) / 2f)
                } else {
                        val eh = imageSize.height.toFloat()
                        val ew = eh * bmpAspect
                        ContentRect(ew, eh, (imageSize.width - ew) / 2f, 0f)
                }
        }

        // Returns null (not Offset.Zero) on the three not-ready / degenerate error
        // conditions below, because (0,0) is also a LEGITIMATE host pixel (primary
        // monitor top-left). Callers must skip their action when this returns null.
        fun mapLocalToHost(localOffset: Offset): Offset? {
                if (imageSize.width == 0 || imageSize.height == 0) return null
                // Guard against Offset.Unspecified (NaN) and any other non-finite values
                // that would produce NaN in the output and crash JSON serialization.
                if (!localOffset.x.isFinite() || !localOffset.y.isFinite()) return null
                val (hostW, hostH) = getHostScreenSize()
                val (hostLeft, hostTop) = getHostDesktopOffset()

                val centerX = imageSize.width / 2f
                val centerY = imageSize.height / 2f
                val adjustedX = (localOffset.x - centerX - panOffsetX) / zoomFactor + centerX
                val adjustedY = (localOffset.y - centerY - panOffsetY) / zoomFactor + centerY

                val rect = contentRect()
                if (rect.w <= 0f || rect.h <= 0f) return null
                val relativeX = ((adjustedX - rect.x) / rect.w).coerceIn(0f, 1f)
                val relativeY = ((adjustedY - rect.y) / rect.h).coerceIn(0f, 1f)

                return Offset(relativeX * hostW + hostLeft, relativeY * hostH + hostTop)
        }

        // Inverse of mapLocalToHost: maps a host cursor position to a local (screen) position so we
        // can draw the cursor overlay where the actual Windows cursor is.
        fun mapHostToLocal(hostX: Float, hostY: Float, coerce: Boolean = true): Offset? {
                if (imageSize.width == 0 || imageSize.height == 0) return null
                // Negative host coordinates are VALID: monitors positioned left of/above the
                // primary have negative virtual-desktop coordinates. Cursor visibility is gated by
                // the caller (uiState.hostCursorVisible), never by the sign of the coordinate.
                val (hostW, hostH) = getHostScreenSize()
                val (hostLeft, hostTop) = getHostDesktopOffset()
                if (hostW <= 0 || hostH <= 0) return null
                val rect = contentRect()
                if (rect.w <= 0f || rect.h <= 0f) return null
                val relX = ((hostX - hostLeft) / hostW.toFloat()).let { if (coerce) it.coerceIn(0f, 1f) else it }
                val relY = ((hostY - hostTop) / hostH.toFloat()).let { if (coerce) it.coerceIn(0f, 1f) else it }
                val adjX = relX * rect.w + rect.x
                val adjY = relY * rect.h + rect.y
                val centerX = imageSize.width / 2f
                val centerY = imageSize.height / 2f
                return Offset(
                        (adjX - centerX) * zoomFactor + panOffsetX + centerX,
                        (adjY - centerY) * zoomFactor + panOffsetY + centerY
                )
        }

        // Smooth the remote cursor: the host streams its position at a limited rate, so animate the
        // overlay toward each new position (critically-damped spring) instead of stepping to it.
        // Snap on (re)appearance / display switch so the cursor doesn't slide across the whole screen.
        // rememberUpdatedState because the effects below are keyed on Unit and therefore keep their
        // FIRST-composition lambda forever. mapHostToLocal closes over streamPixelWidth/Height and
        // currentBitmap, which are plain composable PARAMETERS captured by value — at first
        // composition 0/0/null, so contentRect() would fall back to a hardcoded 1920x1080 aspect for
        // the life of the screen and pan-follow would mis-map on every non-16:9 stream, silently and
        // permanently. The old keyed effect hid this by rebuilding the closure on every packet; that
        // accidental refresh is exactly what removing the restart took away. (RemEx-zc9r)
        val currentMapHostToLocal by rememberUpdatedState<(Float, Float) -> Offset?> { hx, hy ->
            mapHostToLocal(hx, hy, coerce = false)
        }
        val currentDensity by rememberUpdatedState(density)

        val animatedCursorX = remember { Animatable(0f) }
        val animatedCursorY = remember { Animatable(0f) }
        var cursorWasVisible by remember { mutableStateOf(false) }
        // KEYED ON Unit, NOT ON THE CURSOR. The host streams a position 60-90 times a second, and a
        // keyed effect is torn down and relaunched on every one of them — hundreds of coroutine
        // starts per second, on the main thread, stacked on top of frame delivery. collectLatest
        // reproduces the keyed behaviour exactly (a new position cancels the in-flight block and its
        // children) without the effect itself churning. Reading the flows directly rather than
        // through uiState is the other half: it keeps the raw coordinates out of composition, so a
        // cursor packet no longer recomposes this whole screen. (RemEx-zc9r)
        LaunchedEffect(Unit) {
            hostCursor
                    .collectLatest { (x, y, visible) ->
                        if (!visible) {
                            cursorWasVisible = false
                            return@collectLatest
                        }
                        if (!cursorWasVisible) {
                            cursorWasVisible = true
                            animatedCursorX.snapTo(x)
                            animatedCursorY.snapTo(y)
                            return@collectLatest
                        }
                        val spec = spring<Float>(dampingRatio = 1f, stiffness = Spring.StiffnessMedium)
                        // coroutineScope, and it is load-bearing: collectLatest's action has NO
                        // CoroutineScope receiver, so a bare `launch` here would bind to the
                        // LaunchedEffect's own scope. The action would then complete immediately with
                        // no children, collectLatest would have nothing to cancel, and the
                        // cancel-and-retarget behaviour claimed above would not exist — leaving
                        // correctness resting on Animatable's internal mutex instead of structured
                        // concurrency. This suspends until both finish, so the launches are children
                        // of the action and a new position genuinely cancels them.
                        coroutineScope {
                            launch { animatedCursorX.animateTo(x, spec) }
                            launch { animatedCursorY.animateTo(y, spec) }
                        }
                    }
        }

        // Pan-follow: when zoomed, keep the streamed host cursor on screen by panning the view
        // toward it (edge-triggered via a deadzone), animated so it glides. Re-runs whenever the
        // host cursor moves; each run cancels the previous animation and re-targets.
        // Same treatment, and zoomFactor stays a trigger via snapshotFlow so zooming still re-runs
        // the follow even when the cursor has not moved. collectLatest preserves the
        // cancel-and-retarget semantics the keyed effect had. (RemEx-zc9r)
        LaunchedEffect(Unit) {
            val panFollowInputs =
                    combine(hostCursor, snapshotFlow { zoomFactor }) { c, zoom ->
                        PanFollowInput(c.x, c.y, c.visible, zoom)
                    }
            panFollowInputs.collectLatest { (cursorX, cursorY, cursorVisible, zoom) ->
                if (zoom <= 1f) return@collectLatest
                // Don't chase a cursor that isn't on the streamed display — its coords are last-known/stale.
                if (!cursorVisible) return@collectLatest
                if (System.currentTimeMillis() < suppressPanFollowUntilMs) return@collectLatest
                if (imageSize.width == 0 || imageSize.height == 0) return@collectLatest
                val local = currentMapHostToLocal(cursorX, cursorY)
                    ?: return@collectLatest
                val (targetX, targetY) = PanFollowCalculator.compute(
                    cursorLocalX = local.x,
                    cursorLocalY = local.y,
                    panX = panOffsetX,
                    panY = panOffsetY,
                    zoom = zoom,
                    imageWidth = imageSize.width.toFloat(),
                    imageHeight = imageSize.height.toFloat(),
                )
                // This epsilon is load-bearing: when the host cursor sits past the max-pan clamp,
                // compute() returns the same clamped target every tick; this skip prevents the
                // animation from restarting forever. Do not remove.
                // RD-A2: density-scaled (was a hardcoded 0.5f → sub-pixel on high-DPI panels, causing jitter
                // at high zoom). ~0.75dp is a stable deadband across densities.
                val panFollowEpsilonPx = with(currentDensity) { 0.75.dp.toPx() }
                if (abs(targetX - panOffsetX) < panFollowEpsilonPx && abs(targetY - panOffsetY) < panFollowEpsilonPx) {
                    return@collectLatest
                }
                val startX = panOffsetX
                val startY = panOffsetY
                animate(0f, 1f, animationSpec = spring(stiffness = Spring.StiffnessMediumLow)) { t, _ ->
                    panOffsetX = startX + (targetX - startX) * t
                    panOffsetY = startY + (targetY - startY) * t
                }
            }
        }

        // RD-A3: orientation-aware initial fit. Landscape → zoom 1.0 (whole desktop visible, pinch to
        // zoom in). Portrait → fit-to-height so the full host height is visible and the user pans L/R
        // across the wider desktop. Re-fits on display switch or phone rotation (fitKey encodes both).
        LaunchedEffect(uiState.isStreaming, desktopMetaReady, imageSize, streamPixelWidth, streamPixelHeight, uiState.selectedDisplayToken) {
            if (!uiState.isStreaming) {
                lastFitKey = ""
                return@LaunchedEffect
            }
            if (imageSize.width == 0 || imageSize.height == 0) return@LaunchedEffect
            // Wait for the host's REAL screen dimensions before framing. Fitting against the
            // 1920x1080 placeholder mis-sizes the view until a monitor switch forces a re-fit — the
            // exact "zoom is wrong on first connect, fixed by tapping monitors" bug (RemEx-4k4).
            if (!desktopMetaReady) return@LaunchedEffect
            if (streamPixelWidth <= 0 || streamPixelHeight <= 0) return@LaunchedEffect
            val isLandscapeBox = imageSize.width >= imageSize.height
            // Encode the real stream dimensions into the fit key so authoritative metadata that
            // arrives or changes mid-stream (resolution / monitor / DPI) RE-FITS, instead of being
            // ignored because the display token and orientation happened to be unchanged (RemEx-4k4).
            val fitKey = "${uiState.selectedDisplayToken}/$isLandscapeBox/${streamPixelWidth}x$streamPixelHeight"
            if (lastFitKey == fitKey) return@LaunchedEffect
            val rect = contentRect()
            if (rect.h <= 0f) return@LaunchedEffect
            zoomFactor = if (isLandscapeBox) 1f else (imageSize.height / rect.h).coerceIn(1f, 5f)
            panOffsetX = 0f
            panOffsetY = 0f
            lastFitKey = fitKey
            // Instrumentation (RD zoom diag): shows exactly why the initial view is zoomed — the box
            // size, whether it was judged landscape, the letterboxed content rect, and the zoom applied.
            Log.i(TAG, "RD-fit: imageSize=${imageSize.width}x${imageSize.height} landscape=$isLandscapeBox streamRes=${streamPixelWidth}x$streamPixelHeight rect=${rect.w.roundToInt()}x${rect.h.roundToInt()} -> zoomFactor=$zoomFactor (fitKey=$fitKey)")
        }

        Scaffold(
                topBar = {
                        if (!uiState.isFullscreen) {
                                TopAppBar(
                                        title = {
                                                // M3: TopAppBar already applies titleLarge weight;
                                                // no override needed
                                                Text(
                                                        stringResource(
                                                                R.string.screen_remote_desktop_title
                                                        )
                                                )
                                        },
                                        actions = {
                                                RemexTooltip(stringResource(R.string.cd_show_keyboard)) {
                                                        IconButton(
                                                                onClick = { toggleRemoteKeyboard() }
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.Keyboard,
                                                                        contentDescription =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .cd_show_keyboard
                                                                                )
                                                                )
                                                        }
                                                }
                                                // Independent from the Keyboard button above: this
                                                // toggles the PC-keys (modifier) bar on its own,
                                                // surviving IME dismissal (RemEx-yi8o).
                                                RemexTooltip(stringResource(R.string.cd_show_pc_keys)) {
                                                        IconButton(
                                                                onClick = {
                                                                        pcKeysBarVisible = !pcKeysBarVisible
                                                                }
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.KeyboardCommandKey,
                                                                        contentDescription =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .cd_show_pc_keys
                                                                                ),
                                                                        tint =
                                                                                if (pcKeysBarVisible)
                                                                                        MaterialTheme
                                                                                                .colorScheme
                                                                                                .primary
                                                                                else
                                                                                        LocalContentColor
                                                                                                .current
                                                                )
                                                        }
                                                }
                                                RemexTooltip(stringResource(R.string.cd_reset_input)) {
                                                        IconButton(
                                                                onClick = {
                                                                        inputResetTrigger++
                                                                        zoomFactor = 1f
                                                                        panOffsetX = 0f
                                                                        panOffsetY = 0f
                                                                }
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.Refresh,
                                                                        contentDescription =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .cd_reset_input
                                                                                )
                                                                )
                                                        }
                                                }
                                                // Mirrored into the fullscreen control row in the same
                                                // slot, keeping the shared action order both bars are
                                                // required to agree on (RemEx-klq).
                                                RemexTooltip(stringResource(R.string.action_take_screenshot)) {
                                                        IconButton(onClick = { onTakeScreenshot() }) {
                                                                Icon(
                                                                        Icons.Default.ScreenshotMonitor,
                                                                        contentDescription =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .action_take_screenshot
                                                                                )
                                                                )
                                                        }
                                                }
                                                RemexTooltip(stringResource(R.string.cd_settings)) {
                                                        IconButton(onClick = { showSettings = true }) {
                                                                Icon(
                                                                        Icons.Default.Tune,
                                                                        contentDescription =
                                                                                stringResource(
                                                                                        R.string.cd_settings
                                                                                )
                                                                )
                                                        }
                                                }
                                                // Enters only, never exits: this whole top bar is
                                                // rendered inside `if (!uiState.isFullscreen)`, so
                                                // there is no reachable state in which this button
                                                // would leave fullscreen. The fullscreen overlay
                                                // owns that, with its own cd_exit_fullscreen button.
                                                // Passing `true` rather than `!isFullscreen` keeps
                                                // the description true by construction (RemEx-kqh9).
                                                RemexTooltip(stringResource(R.string.cd_enter_fullscreen)) {
                                                        IconButton(
                                                                onClick = { onSetFullscreen(true) }
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.Fullscreen,
                                                                        contentDescription =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .cd_enter_fullscreen
                                                                                )
                                                                )
                                                        }
                                                }
                                                if (uiState.isStreaming) {
                                                        RemexTooltip(stringResource(R.string.cd_stop)) {
                                                                IconButton(
                                                                        onClick = { onStopStreaming() }
                                                                ) {
                                                                        Icon(
                                                                                Icons.Default.Stop,
                                                                                contentDescription =
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .cd_stop
                                                                                        ),
                                                                                tint =
                                                                                        MaterialTheme
                                                                                                .colorScheme
                                                                                                .error
                                                                        )
                                                                }
                                                        }
                                                } else {
                                                        RemexTooltip(
                                                                stringResource(R.string.button_start_streaming)
                                                        ) {
                                                        IconButton(
                                                                onClick = { onStartStreaming() },
                                                                enabled =
                                                                        uiState.capabilityState
                                                                                .supportsRemoteDesktop
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.PlayArrow,
                                                                        contentDescription =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .button_start_streaming
                                                                                ),
                                                                        tint =
                                                                                if (uiState.capabilityState
                                                                                                .supportsRemoteDesktop
                                                                                )
                                                                                        MaterialTheme
                                                                                                .colorScheme
                                                                                                .primary
                                                                                else
                                                                                        MaterialTheme
                                                                                                .colorScheme
                                                                                                .onSurfaceVariant
                                                                )
                                                        }
                                                        }
                                                }
                                        }
                                )
                        }
                }
        ) { padding ->
                Column(
                        modifier =
                                Modifier.fillMaxSize()
                                        .padding(
                                                if (uiState.isFullscreen) PaddingValues(0.dp)
                                                else padding
                                        )
                                        .background(Color.Black)
                ) {

                        // Hidden TextField for keyboard input (invisible but focusable)
                        BasicTextField(
                                value = textValue,
                                onValueChange = { newValue ->
                                        // While a PC-key modifier is armed/locked, a single typed
                                        // alphanumeric character is routed as a chord (Ctrl+C, Ctrl+V,
                                        // Ctrl+A, Ctrl+Z, ...) via onSendKeyPress instead of as typed
                                        // text — sendKeyPress applies the active modifier(s) itself.
                                        // Everything else (no modifier active, or a multi-character /
                                        // non-alphanumeric edit such as autocomplete or punctuation)
                                        // keeps the existing text path byte-for-byte (RemEx-yi8o).
                                        val hasActiveModifier =
                                                modifierStates.values.any { it != ModifierState.OFF }
                                        val chordVk =
                                                if (hasActiveModifier) {
                                                        singleCharacterInsertion(
                                                                        textValue.text,
                                                                        newValue.text
                                                                )
                                                                ?.let {
                                                                        asciiAlphanumericVirtualKeyCode(
                                                                                it
                                                                        )
                                                                }
                                                } else {
                                                        null
                                                }
                                        textValue =
                                                if (chordVk != null) {
                                                        applyRemoteKeyboardEdit(
                                                                        currentValue = textValue,
                                                                        newValue = newValue,
                                                                        onSendText = {},
                                                                        onSendKeyPress = {}
                                                                )
                                                                .also { onSendKeyPress(chordVk) }
                                                } else {
                                                        applyRemoteKeyboardEdit(
                                                                currentValue = textValue,
                                                                newValue = newValue,
                                                                onSendText = onSendText,
                                                                onSendKeyPress = onSendKeyPress
                                                        )
                                                }
                                },
                                keyboardOptions = KeyboardOptions(
                                        keyboardType = KeyboardType.Text,
                                        autoCorrectEnabled = false,
                                        // Default (not Send): on a multiline field Gboard renders a real ↵ Enter key,
                                        // whose '\n' insertion is converted to keycode 13 by applyRemoteKeyboardEdit.
                                        imeAction = ImeAction.Default
                                ),
                                keyboardActions = KeyboardActions(
                                        // Fallbacks: any IME that still renders an action key routes here.
                                        onDone = { onSendKeyPress(13) },
                                        onGo = { onSendKeyPress(13) },
                                        onNext = { onSendKeyPress(13) },
                                        onSearch = { onSendKeyPress(13) },
                                        onSend = { onSendKeyPress(13) }
                                ),
                                modifier =
                                        Modifier.size(1.dp)
                                                .graphicsLayer { alpha = 0f }
                                                .focusRequester(focusRequester)
                        )

                        Box(
                                modifier =
                                        Modifier.weight(1f)
                                                .fillMaxWidth()
                                                .onGloballyPositioned { imageSize = it.size }
                                                // ═══ RAW STYLUS CONTACT CAPTURE ═══
                                                // intercepts before Compose gesture system;
                                                // returns true (consumed) for stylus/eraser only
                                                .pointerInteropFilter { event ->
                                                        if (!uiState.isStreaming)
                                                                return@pointerInteropFilter false
                                                        val toolType = event.getToolType(0)
                                                        if (toolType !=
                                                                        MotionEvent
                                                                                .TOOL_TYPE_STYLUS &&
                                                                        toolType !=
                                                                                MotionEvent
                                                                                        .TOOL_TYPE_ERASER
                                                        ) {
                                                                return@pointerInteropFilter false
                                                        }
                                                        // Hover events must NOT be consumed here.
                                                        // Returning true for ACTION_HOVER_*
                                                        // corrupts
                                                        // Compose's pointer-tracking state and
                                                        // crashes
                                                        // the app. Let them fall through to the
                                                        // pointerInput hover block below.
                                                        val actionMasked = event.actionMasked
                                                        if (actionMasked ==
                                                                        MotionEvent
                                                                                .ACTION_HOVER_ENTER ||
                                                                        actionMasked ==
                                                                                MotionEvent
                                                                                        .ACTION_HOVER_MOVE ||
                                                                        actionMasked ==
                                                                                MotionEvent
                                                                                        .ACTION_HOVER_EXIT
                                                        ) {
                                                                return@pointerInteropFilter false
                                                        }
                                                        val samples =
                                                                RemoteDesktopMotionMapper
                                                                        .mapContactEvent(event) {
                                                                                vx,
                                                                                vy ->
                                                                                val host =
                                                                                        mapLocalToHost(
                                                                                                Offset(
                                                                                                        vx,
                                                                                                        vy
                                                                                                )
                                                                                        )
                                                                                if (host == null)
                                                                                        null
                                                                                else
                                                                                        Pair(
                                                                                                host.x,
                                                                                                host.y
                                                                                        )
                                                                        }
                                                        if (samples.isNotEmpty()) {
                                                                val json =
                                                                        RemoteDesktopPointerSerializer
                                                                                .toBatchJson(
                                                                                        samples
                                                                                )
                                                                onSendPointerBatch(json)
                                                        }
                                                        true
                                                }
                                                // ═══ STYLUS HOVER HANDLING ═══
                                                .pointerInput(uiState.isStreaming) {
                                                        if (!uiState.isStreaming)
                                                                return@pointerInput
                                                        awaitPointerEventScope {
                                                                var lastHoverTime = 0L
                                                                while (true) {
                                                                        val event =
                                                                                awaitPointerEvent(
                                                                                        PointerEventPass
                                                                                                .Main
                                                                                )
                                                                        if (event.type ==
                                                                                        PointerEventType
                                                                                                .Move
                                                                        ) {
                                                                                val stylusChange =
                                                                                        event.changes
                                                                                                .find {
                                                                                                        it.type ==
                                                                                                                PointerType
                                                                                                                        .Stylus &&
                                                                                                                !it.pressed
                                                                                                }
                                                                                if (stylusChange !=
                                                                                                null
                                                                                ) {
                                                                                        val now =
                                                                                                stylusChange
                                                                                                        .uptimeMillis
                                                                                        if (now -
                                                                                                        lastHoverTime >=
                                                                                                        MOVE_THROTTLE_MS
                                                                                        ) {
                                                                                                lastHoverTime =
                                                                                                        now
                                                                                                val hostPos =
                                                                                                        mapLocalToHost(
                                                                                                                stylusChange
                                                                                                                        .position
                                                                                                        )
                                                                                                                ?: continue
                                                                                                // Send as typed hover sample (tool kind Pen, phase HoverMove)
                                                                                                val sample =
                                                                                                        PointerSampleData(
                                                                                                                timestamp =
                                                                                                                        now,
                                                                                                                pointerId =
                                                                                                                        stylusChange
                                                                                                                                .id
                                                                                                                                .value
                                                                                                                                .toInt(),
                                                                                                                phase =
                                                                                                                        PointerPhase
                                                                                                                                .HoverMove,
                                                                                                                toolKind =
                                                                                                                        PointerToolKind
                                                                                                                                .Pen,
                                                                                                                deviceKind =
                                                                                                                        PointerDeviceKind
                                                                                                                                .Stylus,
                                                                                                                logicalX =
                                                                                                                        hostPos.x,
                                                                                                                logicalY =
                                                                                                                        hostPos.y,
                                                                                                        )
                                                                                                onSendPointerBatch(
                                                                                                        RemoteDesktopPointerSerializer
                                                                                                                .toBatchJson(
                                                                                                                        listOf(
                                                                                                                                sample
                                                                                                                        )
                                                                                                                )
                                                                                                )
                                                                                                cursorX =
                                                                                                        stylusChange
                                                                                                                .position
                                                                                                                .x
                                                                                                cursorY =
                                                                                                        stylusChange
                                                                                                                .position
                                                                                                                .y
                                                                                                cursorVisible =
                                                                                                        true
                                                                                                isStylusActive =
                                                                                                        true
                                                                                        }
                                                                                }
                                                                        }
                                                                }
                                                        }
                                                }
                                                // ═══ UNIFIED GESTURE STATE MACHINE ═══
                                                // Key on directTouch so the handler restarts when
                                                // mode changes
                                                .pointerInput(
                                                        uiState.isStreaming,
                                                        inputResetTrigger,
                                                        uiState.directTouch
                                                ) {
                                                        if (!uiState.isStreaming)
                                                                return@pointerInput

                                                        awaitPointerEventScope {
                                                                // ── Persistent gesture state ──
                                                                var primaryPointerId: PointerId? =
                                                                        null
                                                                var pressTime =
                                                                        0L // uptimeMillis of
                                                                // primary pointer press
                                                                var pressPos =
                                                                        Offset.Zero // position at
                                                                // press
                                                                var pressIsStylus = false
                                                                var isDragging =
                                                                        false // true once mouseDown
                                                                // has been sent
                                                                var dragButton =
                                                                        MouseButtons.LEFT // which mouse button is
                                                                // held
                                                                var hasMovedBeyondSlop = false
                                                                var multiTouchActive =
                                                                        false // true when 2+
                                                                // pointers are down
                                                                var lastTap: TapContext? =
                                                                        null // for double-tap
                                                                // detection
                                                                var isDoubleTapHoldArmed =
                                                                        false // second press in
                                                                // double-tap window
                                                                var longPressArmed =
                                                                        false // 500ms still-hold
                                                                // completed
                                                                // (trackpad)
                                                                var longPressJob: Job? = null
                                                                var lastMoveTime =
                                                                        0L // for move throttling
                                                                var scrollAccumX = 0f
                                                                var scrollAccumY = 0f

                                                                // Two-finger gesture state
                                                                var twoFingerIntent: String? =
                                                                        null // "scroll" | "pinch" |
                                                                // null
                                                                var prevTwoFingerDist = 0f
                                                                var prevTwoFingerCenter =
                                                                        Offset.Zero

                                                                // Velocity tracking for inertia
                                                                // (trackpad mode)
                                                                val recentDeltas =
                                                                        ArrayDeque<Offset>(4)
                                                                var lastDeltaTime = 0L

                                                                fun slopPx() =
                                                                        if (pressIsStylus)
                                                                                stylusSlopPx
                                                                        else fingerSlopPx

                                                                fun resetSinglePointerState() {
                                                                        primaryPointerId = null
                                                                        pressTime = 0L
                                                                        pressPos = Offset.Zero
                                                                        hasMovedBeyondSlop = false
                                                                        isDoubleTapHoldArmed = false
                                                                        longPressArmed = false
                                                                        longPressJob?.cancel()
                                                                        longPressJob = null
                                                                        lastMoveTime = 0L
                                                                        recentDeltas.clear()
                                                                }

                                                                fun resetTwoFingerState() {
                                                                        twoFingerIntent = null
                                                                        prevTwoFingerDist = 0f
                                                                        prevTwoFingerCenter =
                                                                                Offset.Zero
                                                                        scrollAccumX = 0f
                                                                        scrollAccumY = 0f
                                                                }

                                                                fun cancelDrag() {
                                                                        if (isDragging) {
                                                                                onSendMouseUp(
                                                                                        dragButton
                                                                                )
                                                                                isDragging = false
                                                                                dragButton = MouseButtons.LEFT
                                                                        }
                                                                }

                                                                try {
                                                                        while (true) {
                                                                                val event =
                                                                                        awaitPointerEvent()
                                                                                val activePointers =
                                                                                        event.changes
                                                                                                .filter {
                                                                                                        it.pressed
                                                                                                }
                                                                                val pointerCount =
                                                                                        activePointers
                                                                                                .size

                                                                                // Cancel inertia on
                                                                                // any new touch
                                                                                if (event.type ==
                                                                                                PointerEventType
                                                                                                        .Press
                                                                                ) {
                                                                                        inertiaJob
                                                                                                ?.cancel()
                                                                                        inertiaJob =
                                                                                                null
                                                                                }

                                                                                // ── MULTI-TOUCH
                                                                                // HANDLING (2
                                                                                // fingers) ──
                                                                                if (pointerCount >=
                                                                                                2
                                                                                ) {
                                                                                        if (!multiTouchActive
                                                                                        ) {
                                                                                                // Transition 1→2+: cancel any
                                                                                                // single-pointer gesture
                                                                                                cancelDrag()
                                                                                                resetSinglePointerState()
                                                                                                multiTouchActive =
                                                                                                        true
                                                                                                resetTwoFingerState()
                                                                                        }

                                                                                        if (pointerCount ==
                                                                                                        2
                                                                                        ) {
                                                                                                val p1 =
                                                                                                        activePointers[
                                                                                                                        0]
                                                                                                                .position
                                                                                                val p2 =
                                                                                                        activePointers[
                                                                                                                        1]
                                                                                                                .position
                                                                                                val dist =
                                                                                                        sqrt(
                                                                                                                (p1.x -
                                                                                                                        p2.x) *
                                                                                                                        (p1.x -
                                                                                                                                p2.x) +
                                                                                                                        (p1.y -
                                                                                                                                p2.y) *
                                                                                                                                (p1.y -
                                                                                                                                        p2.y)
                                                                                                        )
                                                                                                val center =
                                                                                                        Offset(
                                                                                                                (p1.x +
                                                                                                                        p2.x) /
                                                                                                                        2f,
                                                                                                                (p1.y +
                                                                                                                        p2.y) /
                                                                                                                        2f
                                                                                                        )

                                                                                                if (prevTwoFingerDist >
                                                                                                                0f
                                                                                                ) {
                                                                                                        val distDelta =
                                                                                                                abs(
                                                                                                                        dist -
                                                                                                                                prevTwoFingerDist
                                                                                                                )
                                                                                                        val moveDelta =
                                                                                                                center -
                                                                                                                        prevTwoFingerCenter

                                                                                                        // Lock intent after first
                                                                                                        // significant gesture
                                                                                                        if (twoFingerIntent ==
                                                                                                                        null
                                                                                                        ) {
                                                                                                                if (distDelta >
                                                                                                                                5f
                                                                                                                )
                                                                                                                        twoFingerIntent =
                                                                                                                                "pinch"
                                                                                                                else if (moveDelta
                                                                                                                                .getDistance() >
                                                                                                                                3f
                                                                                                                )
                                                                                                                        twoFingerIntent =
                                                                                                                                "scroll"
                                                                                                        }

                                                                                                        when (twoFingerIntent
                                                                                                        ) {
                                                                                                                "pinch" -> {
                                                                                                                        if (distDelta >
                                                                                                                                        2f
                                                                                                                        ) {
                                                                                                                                val zoomDelta =
                                                                                                                                        dist /
                                                                                                                                                prevTwoFingerDist
                                                                                                                                val oldZoom =
                                                                                                                                        zoomFactor
                                                                                                                                zoomFactor =
                                                                                                                                        (zoomFactor *
                                                                                                                                                        zoomDelta)
                                                                                                                                                .coerceIn(
                                                                                                                                                        1f,
                                                                                                                                                        4f
                                                                                                                                                )
                                                                                                                                val actualDelta =
                                                                                                                                        zoomFactor /
                                                                                                                                                oldZoom
                                                                                                                                panOffsetX =
                                                                                                                                        (center.x -
                                                                                                                                                imageSize
                                                                                                                                                        .width /
                                                                                                                                                        2f) *
                                                                                                                                                (1f -
                                                                                                                                                        actualDelta) +
                                                                                                                                                panOffsetX *
                                                                                                                                                        actualDelta
                                                                                                                                panOffsetY =
                                                                                                                                        (center.y -
                                                                                                                                                imageSize
                                                                                                                                                        .height /
                                                                                                                                                        2f) *
                                                                                                                                                (1f -
                                                                                                                                                        actualDelta) +
                                                                                                                                                panOffsetY *
                                                                                                                                                        actualDelta
                                                                                                                                val maxPanX =
                                                                                                                                        imageSize.width *
                                                                                                                                                (zoomFactor -
                                                                                                                                                        1f) /
                                                                                                                                                2f
                                                                                                                                val maxPanY =
                                                                                                                                        imageSize.height *
                                                                                                                                                (zoomFactor -
                                                                                                                                                        1f) /
                                                                                                                                                2f
                                                                                                                                panOffsetX =
                                                                                                                                        panOffsetX
                                                                                                                                                .coerceIn(
                                                                                                                                                        -maxPanX,
                                                                                                                                                        maxPanX
                                                                                                                                                )
                                                                                                                                panOffsetY =
                                                                                                                                        panOffsetY
                                                                                                                                                .coerceIn(
                                                                                                                                                        -maxPanY,
                                                                                                                                                        maxPanY
                                                                                                                                                )
                                                                                                                                suppressPanFollowUntilMs = System.currentTimeMillis() + 350
                                                                                                                        }
                                                                                                                }
                                                                                                                "scroll" -> {
                                                                                                                        if (zoomFactor >
                                                                                                                                        1.05f
                                                                                                                        ) {
                                                                                                                                // Panning zoomed view
                                                                                                                                panOffsetX +=
                                                                                                                                        moveDelta
                                                                                                                                                .x
                                                                                                                                panOffsetY +=
                                                                                                                                        moveDelta
                                                                                                                                                .y
                                                                                                                                val maxPanX =
                                                                                                                                        imageSize.width *
                                                                                                                                                (zoomFactor -
                                                                                                                                                        1f) /
                                                                                                                                                2f
                                                                                                                                val maxPanY =
                                                                                                                                        imageSize.height *
                                                                                                                                                (zoomFactor -
                                                                                                                                                        1f) /
                                                                                                                                                2f
                                                                                                                                panOffsetX =
                                                                                                                                        panOffsetX
                                                                                                                                                .coerceIn(
                                                                                                                                                        -maxPanX,
                                                                                                                                                        maxPanX
                                                                                                                                                )
                                                                                                                                panOffsetY =
                                                                                                                                        panOffsetY
                                                                                                                                                .coerceIn(
                                                                                                                                                        -maxPanY,
                                                                                                                                                        maxPanY
                                                                                                                                                )
                                                                                                                                suppressPanFollowUntilMs = System.currentTimeMillis() + 350
                                                                                                                        } else {
                                                                                                                                // Mouse wheel scroll
                                                                                                                                // with accumulator
                                                                                                                                scrollAccumX +=
                                                                                                                                        moveDelta
                                                                                                                                                .x *
                                                                                                                                                1.5f *
                                                                                                                                                hScrollSensState
                                                                                                                                                        .value
                                                                                                                                scrollAccumY +=
                                                                                                                                        moveDelta
                                                                                                                                                .y *
                                                                                                                                                1.5f *
                                                                                                                                                vScrollSensState
                                                                                                                                                        .value
                                                                                                                                val sx =
                                                                                                                                        scrollAccumX
                                                                                                                                                .toInt()
                                                                                                                                val sy =
                                                                                                                                        scrollAccumY
                                                                                                                                                .toInt()
                                                                                                                                if (sx !=
                                                                                                                                                0 ||
                                                                                                                                                sy !=
                                                                                                                                                        0
                                                                                                                                ) {
                                                                                                                                        onSendMouseScroll(
                                                                                                                                                -sx,
                                                                                                                                                -sy
                                                                                                                                        )
                                                                                                                                        scrollAccumX -=
                                                                                                                                                sx
                                                                                                                                        scrollAccumY -=
                                                                                                                                                sy
                                                                                                                                }
                                                                                                                        }
                                                                                                                }
                                                                                                        }
                                                                                                }

                                                                                                prevTwoFingerDist =
                                                                                                        dist
                                                                                                prevTwoFingerCenter =
                                                                                                        center
                                                                                                event.changes
                                                                                                        .forEach {
                                                                                                                it.consume()
                                                                                                        }
                                                                                        }
                                                                                        // 3+
                                                                                        // fingers:
                                                                                        // ignore
                                                                                        // entirely
                                                                                        continue
                                                                                }

                                                                                // ── SINGLE-POINTER
                                                                                // HANDLING ──
                                                                                if (pointerCount ==
                                                                                                0 &&
                                                                                                multiTouchActive
                                                                                ) {
                                                                                        // All
                                                                                        // fingers
                                                                                        // lifted
                                                                                        // after
                                                                                        // multi-touch —
                                                                                        // reset
                                                                                        multiTouchActive =
                                                                                                false
                                                                                        resetTwoFingerState()
                                                                                        resetSinglePointerState()
                                                                                        continue
                                                                                }

                                                                                if (multiTouchActive
                                                                                )
                                                                                        continue // Still in multi-touch, ignore
                                                                                // single pointer
                                                                                // events

                                                                                // Find the primary
                                                                                // pointer (track by
                                                                                // ID)
                                                                                val change =
                                                                                        if (primaryPointerId !=
                                                                                                        null
                                                                                        ) {
                                                                                                event.changes
                                                                                                        .find {
                                                                                                                it.id ==
                                                                                                                        primaryPointerId
                                                                                                        }
                                                                                                        ?: event.changes
                                                                                                                .firstOrNull()
                                                                                        } else {
                                                                                                event.changes
                                                                                                        .firstOrNull()
                                                                                        }
                                                                                if (change == null)
                                                                                        continue

                                                                                val isStylus =
                                                                                        change.type ==
                                                                                                PointerType
                                                                                                        .Stylus
                                                                                val now =
                                                                                        change.uptimeMillis
                                                                                val useAbsolute =
                                                                                        isStylus ||
                                                                                                uiState.directTouch

                                                                                when (event.type) {
                                                                                        PointerEventType
                                                                                                .Press -> {
                                                                                                primaryPointerId =
                                                                                                        change.id
                                                                                                pressTime =
                                                                                                        now
                                                                                                pressPos =
                                                                                                        change.position
                                                                                                pressIsStylus =
                                                                                                        isStylus
                                                                                                hasMovedBeyondSlop =
                                                                                                        false
                                                                                                isStylusActive =
                                                                                                        isStylus

                                                                                                cursorVisible =
                                                                                                        useAbsolute
                                                                                                cursorX =
                                                                                                        change.position
                                                                                                                .x
                                                                                                cursorY =
                                                                                                        change.position
                                                                                                                .y
                                                                                                recentDeltas
                                                                                                        .clear()
                                                                                                lastDeltaTime =
                                                                                                        now

                                                                                                // Check for double-tap-hold
                                                                                                val lt =
                                                                                                        lastTap
                                                                                                if (lt !=
                                                                                                                null &&
                                                                                                                (now -
                                                                                                                        lt.time) <
                                                                                                                        DOUBLE_TAP_WINDOW_MS &&
                                                                                                                (change.position -
                                                                                                                                lt.position)
                                                                                                                        .getDistance() <
                                                                                                                        doubleTapRadiusPx &&
                                                                                                                lt.isStylus ==
                                                                                                                        isStylus
                                                                                                ) {
                                                                                                        isDoubleTapHoldArmed =
                                                                                                                true
                                                                                                } else {
                                                                                                        isDoubleTapHoldArmed =
                                                                                                                false
                                                                                                }

                                                                                                // Arm long-press-then-drag timer for
                                                                                                // trackpad mode (fires after 500ms
                                                                                                // still-hold). Not used in absolute
                                                                                                // mode or when double-tap is armed.
                                                                                                longPressJob
                                                                                                        ?.cancel()
                                                                                                longPressArmed =
                                                                                                        false
                                                                                                if (!useAbsolute &&
                                                                                                                !isDoubleTapHoldArmed
                                                                                                ) {
                                                                                                        longPressJob =
                                                                                                                scope
                                                                                                                        .launch {
                                                                                                                                delay(
                                                                                                                                        LONG_PRESS_THRESHOLD_MS
                                                                                                                                )
                                                                                                                                if (!isDragging &&
                                                                                                                                                !hasMovedBeyondSlop
                                                                                                                                ) {
                                                                                                                                        longPressArmed =
                                                                                                                                                true
                                                                                                                                        view.performHapticFeedback(
                                                                                                                                                HapticFeedbackConstants
                                                                                                                                                        .LONG_PRESS
                                                                                                                                        )
                                                                                                                                }
                                                                                                                        }
                                                                                                }

                                                                                                // DO NOT send mouseDown here — deferred
                                                                                                // until movement or release
                                                                                        }
                                                                                        PointerEventType
                                                                                                .Move -> {
                                                                                                if (primaryPointerId ==
                                                                                                                null
                                                                                                )
                                                                                                        continue

                                                                                                val distance =
                                                                                                        (change.position -
                                                                                                                        pressPos)
                                                                                                                .getDistance()
                                                                                                val slop =
                                                                                                        slopPx()

                                                                                                if (!hasMovedBeyondSlop &&
                                                                                                                distance >
                                                                                                                        slop
                                                                                                ) {
                                                                                                        hasMovedBeyondSlop =
                                                                                                                true
                                                                                                        // Moving cancels the still-hold
                                                                                                        // long-press timer if it hasn't
                                                                                                        // fired yet
                                                                                                        if (!longPressArmed
                                                                                                        ) {
                                                                                                                longPressJob
                                                                                                                        ?.cancel()
                                                                                                                longPressJob =
                                                                                                                        null
                                                                                                        }
                                                                                                }

                                                                                                // Update cursor position for visual
                                                                                                // feedback
                                                                                                if (useAbsolute
                                                                                                ) {
                                                                                                        cursorX =
                                                                                                                change.position
                                                                                                                        .x
                                                                                                        cursorY =
                                                                                                                change.position
                                                                                                                        .y
                                                                                                }

                                                                                                // Throttle movement events to ~30Hz
                                                                                                if (now -
                                                                                                                lastMoveTime <
                                                                                                                MOVE_THROTTLE_MS
                                                                                                )
                                                                                                        continue
                                                                                                lastMoveTime =
                                                                                                        now

                                                                                                if (hasMovedBeyondSlop
                                                                                                ) {
                                                                                                        if (isDoubleTapHoldArmed &&
                                                                                                                        !isDragging
                                                                                                        ) {
                                                                                                                // Double-tap-then-drag: second
                                                                                                                // press has started moving —
                                                                                                                // immediately enter left drag
                                                                                                                if (useAbsolute
                                                                                                                ) {
                                                                                                                        val hostPress =
                                                                                                                                mapLocalToHost(
                                                                                                                                        pressPos
                                                                                                                                )
                                                                                                                        hostPress?.let {
                                                                                                                                onSendMouseDown(
                                                                                                                                        MouseButtons.LEFT,
                                                                                                                                        it.x.toInt(),
                                                                                                                                        it.y.toInt()
                                                                                                                                )
                                                                                                                        }
                                                                                                                } else {
                                                                                                                        onSendMouseDown(
                                                                                                                                MouseButtons.LEFT,
                                                                                                                                null,
                                                                                                                                null
                                                                                                                        )
                                                                                                                }
                                                                                                                isDragging =
                                                                                                                        true
                                                                                                                dragButton =
                                                                                                                        MouseButtons.LEFT
                                                                                                                isDoubleTapHoldArmed =
                                                                                                                        false
                                                                                                                lastTap =
                                                                                                                        null
                                                                                                        } else if (longPressArmed &&
                                                                                                                        !isDragging &&
                                                                                                                        !useAbsolute
                                                                                                        ) {
                                                                                                                // Long-press-then-drag
                                                                                                                // (trackpad): enter left drag
                                                                                                                // now that user has started
                                                                                                                // moving after 500ms hold
                                                                                                                onSendMouseDown(
                                                                                                                        MouseButtons.LEFT,
                                                                                                                        null,
                                                                                                                        null
                                                                                                                )
                                                                                                                isDragging =
                                                                                                                        true
                                                                                                                dragButton =
                                                                                                                        MouseButtons.LEFT
                                                                                                                longPressArmed =
                                                                                                                        false
                                                                                                        } else if (!isDragging &&
                                                                                                                        !isDoubleTapHoldArmed &&
                                                                                                                        useAbsolute
                                                                                                        ) {
                                                                                                                // Absolute/stylus mode: normal
                                                                                                                // drag requires mouseDown
                                                                                                                val hostPress =
                                                                                                                        mapLocalToHost(
                                                                                                                                pressPos
                                                                                                                        )
                                                                                                                hostPress?.let {
                                                                                                                        onSendMouseDown(
                                                                                                                                MouseButtons.LEFT,
                                                                                                                                it.x.toInt(),
                                                                                                                                it.y.toInt()
                                                                                                                        )
                                                                                                                }
                                                                                                                isDragging =
                                                                                                                        true
                                                                                                                dragButton =
                                                                                                                        MouseButtons.LEFT
                                                                                                        }
                                                                                                        // Trackpad (relative, no
                                                                                                        // long-press, no double-tap): just
                                                                                                        // move cursor, no button held

                                                                                                        if (useAbsolute
                                                                                                        ) {
                                                                                                                // Absolute positioning
                                                                                                                val hostPos =
                                                                                                                        mapLocalToHost(
                                                                                                                                change.position
                                                                                                                        )
                                                                                                                hostPos?.let {
                                                                                                                        onSendMouseAbsolute(
                                                                                                                                it.x.toInt(),
                                                                                                                                it.y.toInt()
                                                                                                                        )
                                                                                                                }
                                                                                                        } else {
                                                                                                                // Relative (trackpad)
                                                                                                                // positioning with pointer
                                                                                                                // speed
                                                                                                                val diff =
                                                                                                                        change.position -
                                                                                                                                change.previousPosition
                                                                                                                val scaledX =
                                                                                                                        diff.x *
                                                                                                                                pointerSpeedState
                                                                                                                                        .value
                                                                                                                val scaledY =
                                                                                                                        diff.y *
                                                                                                                                pointerSpeedState
                                                                                                                                        .value

                                                                                                                if (isDragging
                                                                                                                ) {
                                                                                                                        // Dragging with button held
                                                                                                                        onSendMouseMove(
                                                                                                                                scaledX,
                                                                                                                                scaledY
                                                                                                                        )
                                                                                                                } else {
                                                                                                                        // Normal trackpad cursor
                                                                                                                        // movement — NO mouseDown
                                                                                                                        onSendMouseMove(
                                                                                                                                scaledX,
                                                                                                                                scaledY
                                                                                                                        )
                                                                                                                }

                                                                                                                // Track velocity for inertia
                                                                                                                if (!isDragging
                                                                                                                ) {
                                                                                                                        recentDeltas
                                                                                                                                .addLast(
                                                                                                                                        Offset(
                                                                                                                                                scaledX,
                                                                                                                                                scaledY
                                                                                                                                        )
                                                                                                                                )
                                                                                                                        if (recentDeltas
                                                                                                                                        .size >
                                                                                                                                        3
                                                                                                                        )
                                                                                                                                recentDeltas
                                                                                                                                        .removeFirst()
                                                                                                                }
                                                                                                        }
                                                                                                }
                                                                                        }
                                                                                        PointerEventType
                                                                                                .Release -> {
                                                                                                if (primaryPointerId ==
                                                                                                                null
                                                                                                )
                                                                                                        continue
                                                                                                val duration =
                                                                                                        now -
                                                                                                                pressTime

                                                                                                if (isDragging
                                                                                                ) {
                                                                                                        // End drag
                                                                                                        onSendMouseUp(
                                                                                                                dragButton
                                                                                                        )
                                                                                                        isDragging =
                                                                                                                false
                                                                                                        dragButton =
                                                                                                                MouseButtons.LEFT
                                                                                                } else if (!hasMovedBeyondSlop
                                                                                                ) {
                                                                                                        // Tap gesture (no significant
                                                                                                        // movement)
                                                                                                        if (duration <
                                                                                                                        TAP_MAX_DURATION_MS
                                                                                                        ) {
                                                                                                                // Quick tap = left-click
                                                                                                                if (useAbsolute
                                                                                                                ) {
                                                                                                                        val hostPos =
                                                                                                                                mapLocalToHost(
                                                                                                                                        pressPos
                                                                                                                                )
                                                                                                                        hostPos?.let {
                                                                                                                                onSendMouseAbsoluteClick(
                                                                                                                                        MouseButtons.LEFT,
                                                                                                                                        it.x.toInt(),
                                                                                                                                        it.y.toInt()
                                                                                                                                )
                                                                                                                        }
                                                                                                                } else {
                                                                                                                        onSendMouseClick(
                                                                                                                                MouseButtons.LEFT
                                                                                                                        )
                                                                                                                }
                                                                                                                lastTap =
                                                                                                                        TapContext(
                                                                                                                                now,
                                                                                                                                pressPos,
                                                                                                                                isStylus
                                                                                                                        )
                                                                                                        } else if (duration >=
                                                                                                                        LONG_PRESS_THRESHOLD_MS
                                                                                                        ) {
                                                                                                                // Long press = right-click
                                                                                                                if (useAbsolute
                                                                                                                ) {
                                                                                                                        val hostPos =
                                                                                                                                mapLocalToHost(
                                                                                                                                        pressPos
                                                                                                                                )
                                                                                                                        hostPos?.let {
                                                                                                                                onSendMouseAbsoluteClick(
                                                                                                                                        MouseButtons.RIGHT,
                                                                                                                                        it.x.toInt(),
                                                                                                                                        it.y.toInt()
                                                                                                                                )
                                                                                                                        }
                                                                                                                } else {
                                                                                                                        onSendMouseClick(
                                                                                                                                MouseButtons.RIGHT
                                                                                                                        )
                                                                                                                }
                                                                                                                lastTap =
                                                                                                                        null // Long press is
                                                                                                                // not a tap for
                                                                                                                // double-tap
                                                                                                                // purposes
                                                                                                        }
                                                                                                } else if (!useAbsolute &&
                                                                                                                !isDragging
                                                                                                ) {
                                                                                                        // Trackpad: finger lifted after
                                                                                                        // moving — apply inertia
                                                                                                        if (recentDeltas
                                                                                                                        .isNotEmpty()
                                                                                                        ) {
                                                                                                                var avgX =
                                                                                                                        0f
                                                                                                                var avgY =
                                                                                                                        0f
                                                                                                                recentDeltas
                                                                                                                        .forEach {
                                                                                                                                avgX +=
                                                                                                                                        it.x
                                                                                                                                avgY +=
                                                                                                                                        it.y
                                                                                                                        }
                                                                                                                avgX /=
                                                                                                                        recentDeltas
                                                                                                                                .size
                                                                                                                avgY /=
                                                                                                                        recentDeltas
                                                                                                                                .size
                                                                                                                val velocity =
                                                                                                                        sqrt(
                                                                                                                                avgX *
                                                                                                                                        avgX +
                                                                                                                                        avgY *
                                                                                                                                                avgY
                                                                                                                        )

                                                                                                                if (velocity >
                                                                                                                                inertiaMinVelPx
                                                                                                                ) {
                                                                                                                        inertiaJob
                                                                                                                                ?.cancel()
                                                                                                                        inertiaJob =
                                                                                                                                scope
                                                                                                                                        .launch {
                                                                                                                                                var vx =
                                                                                                                                                        avgX
                                                                                                                                                var vy =
                                                                                                                                                        avgY
                                                                                                                                                while (sqrt(
                                                                                                                                                        vx *
                                                                                                                                                                vx +
                                                                                                                                                                vy *
                                                                                                                                                                        vy
                                                                                                                                                ) >
                                                                                                                                                        inertiaStopVelPx) {
                                                                                                                                                        onSendMouseMove(
                                                                                                                                                                vx,
                                                                                                                                                                vy
                                                                                                                                                        )
                                                                                                                                                        vx *=
                                                                                                                                                                INERTIA_DECAY
                                                                                                                                                        vy *=
                                                                                                                                                                INERTIA_DECAY
                                                                                                                                                        delay(
                                                                                                                                                                INERTIA_FRAME_MS
                                                                                                                                                        )
                                                                                                                                                }
                                                                                                                                        }
                                                                                                                }
                                                                                                        }
                                                                                                }

                                                                                                resetSinglePointerState()
                                                                                        }
                                                                                        PointerEventType
                                                                                                .Exit -> {
                                                                                                cancelDrag()
                                                                                                resetSinglePointerState()
                                                                                        }
                                                                                }
                                                                        }
                                                                } finally {
                                                                        // Scope cancelled — release
                                                                        // any held buttons
                                                                        if (isDragging) {
                                                                                onSendMouseUp(
                                                                                        dragButton
                                                                                )
                                                                        }
                                                                        inertiaJob?.cancel()
                                                                        longPressJob?.cancel()
                                                                }
                                                        }
                                                },
                                contentAlignment = Alignment.Center
                        ) {
                                val safeFrame = currentBitmap

                                if (activeCodec == "H264" || (safeFrame != null && !safeFrame.isRecycled)) {
                                        // Gate first surface creation on desktopMetaReady: without this, the surface can be
                                        // created at the 1920x1080 placeholder (streamPixelWidth/Height defaults) before the
                                        // host's real desktop_meta arrives, freezing the SurfaceView's buffer->view scale at
                                        // the wrong geometry (see the freeze note below) until an unrelated key() input
                                        // (e.g. fullscreen toggle) happens to force a rebuild. (Phase 2, RemEx-bqoe)
                                        // Gate on lastFitKey too (not just desktopMetaReady): a SurfaceView freezes its
                                        // buffer->view SCALE at creation, and since x3eb no longer recreates it on a
                                        // resolution change, a surface built BEFORE the fit effect settles zoomFactor/geometry
                                        // stays frozen at that pre-fit (zoomed) scale until an unrelated recreate — the
                                        // "starts zoomed, then refreshes to full" report. lastFitKey is non-empty only once
                                        // the fit has run against the real desktop dims, so waiting for it means the first (and
                                        // only) surface is built at the correct scale. Mid-session scale changes still
                                        // reconfigure in place (streamRes is not a key), so no black regression. (RemEx-oenj)
                                        if (activeCodec == "H264" && desktopMetaReady && lastFitKey.isNotEmpty()) {
                                                // Rebuild the SurfaceView only when the video box (imageSize) settles — NOT on a
                                                // resolution change. A SurfaceView's content sublayer freezes its buffer->view
                                                // SCALE at creation: if the surface is first created while imageSize is
                                                // transiently smaller (e.g. during the capture_unavailable -> reconnect churn on
                                                // first connect, when the box is briefly ~half height), the video stays scaled to
                                                // that stale geometry — rendered at ~0.5x in the top-left quadrant — even after the
                                                // view bounds grow to full. Re-layout (rotation) does NOT refresh the frozen
                                                // content scale; only a teardown+recreate does. Keying on imageSize forces a
                                                // recreate once the box resolves to its settled size, so the final surface is
                                                // built at correct geometry. imageSize is stable while streaming (independent of
                                                // zoom/pan, which the Modifier.layout below applies without a rebuild), so this
                                                // does not churn the decoder in steady state. (RemEx-4fv3)
                                                //
                                                // Resolution (streamPixelWidth/Height) is deliberately NOT in this key. A
                                                // mid-session capture-scale change makes the host rebuild its encoder and emit a
                                                // fresh SPS/PPS+IDR; the LIVE decoder adopts that in place via
                                                // H264StreamDecoder.maybeReconfigureForNewSps (stop → configure(new SPS) → feed
                                                // the IDR → start) with zero frame gap. Keying on resolution instead tore the
                                                // decoder down the instant desktop_meta reported the new size — the replacement
                                                // came up empty and mid-GOP, dropping P-frames until the host's next periodic IDR
                                                // (or a monitor-switch re-bootstrap), i.e. black-with-cursor + a transient zoom.
                                                // The decoder's width/height are hints only (it configures from the stream's own
                                                // SPS). Because the surface persists across resolution changes, its fixed buffer
                                                // geometry MUST be re-pinned when the stream resolution changes — the update
                                                // lambda below does that. (On the S24 Ultra's c2 Qualcomm decoder the codec does
                                                // NOT override a stale setFixedSize: the frozen creation-time geometry rendered
                                                // every frame zoomed by exactly oldRes/newRes ≈ 1/scale — RemEx-oenj.)
                                                // contentRect()/fit still re-key on the dims, so layout re-aspects without a
                                                // rebuild. (RemEx-x3eb)
                                                key(imageSize) {
                                                AndroidView(
                                                        factory = { context ->
                                                                var localDecoder: H264StreamDecoder? = null
                                                                android.view.SurfaceView(context).apply {
                                                                        // Use a SurfaceView (NOT TextureView) for the H.264 video. Its consumer is
                                                                        // SurfaceFlinger — a dedicated hardware-overlay layer that accepts the Qualcomm
                                                                        // c2 decoder's native (UBWC) graphic buffers and always drains them. A TextureView's
                                                                        // GL SurfaceTexture consumer cannot accept those buffers ("? is not a supported pixel
                                                                        // format" / setOutputSurface BAD_INDEX → onOutputBufferAvailable never fires), so the
                                                                        // decoder produces no output, its input pool exhausts after ~2 frames, and the stream
                                                                        // stays black. The cursor overlay Canvas and pan/zoom graphicsLayer below still apply.
                                                                        // (#2b decode-stall, Qualcomm output-surface negotiation)
                                                                        holder.addCallback(object : android.view.SurfaceHolder.Callback {
                                                                                override fun surfaceCreated(holder: android.view.SurfaceHolder) {
                                                                                        // Pin the surface buffer geometry to the DECODED size; SurfaceFlinger scales
                                                                                        // this overlay layer to the view rect, so the displayed size still follows layout.
                                                                                        holder.setFixedSize(streamPixelWidth, streamPixelHeight)
                                                                                        val decoder = H264StreamDecoder(
                                                                                                streamPixelWidth,
                                                                                                streamPixelHeight,
                                                                                                holder.surface,
                                                                                                onInitFailure = onH264DecoderInitFailed,
                                                                                                onKeyframeNeeded = onH264KeyframeNeeded
                                                                                        )
                                                                                        localDecoder = decoder
                                                                                        onActiveH264DecoderChange(decoder)
                                                                                        Log.i(TAG, "H.264 SurfaceView created: encoded=${streamPixelWidth}x${streamPixelHeight}")
                                                                                }
                                                                                override fun surfaceChanged(holder: android.view.SurfaceHolder, format: Int, width: Int, height: Int) {}
                                                                                override fun surfaceDestroyed(holder: android.view.SurfaceHolder) {
                                                                                        // Guarded release: when key(streamPixelWidth, streamPixelHeight) rebuilds this
                                                                                        // AndroidView on a resolution change, the NEW view's surfaceCreated may fire
                                                                                        // BEFORE this (old) view's surfaceDestroyed. onH264DecoderReleased only clears
                                                                                        // the ViewModel's active decoder if it still points at THIS decoder, never the
                                                                                        // freshly-created one — otherwise frames would be starved.
                                                                                        val released = localDecoder
                                                                                        localDecoder = null
                                                                                        if (released != null) {
                                                                                                released.release()
                                                                                                onH264DecoderReleased(released)
                                                                                        }
                                                                                        Log.i(TAG, "H.264 SurfaceView destroyed.")
                                                                                }
                                                                        })
                                                                }
                                                        },
                                                        update = { view ->
                                                                // Re-pin the surface's producer buffer geometry to the CURRENT stream
                                                                // resolution. surfaceCreated pins it once, but since x3eb the SurfaceView
                                                                // deliberately survives a mid-session scale change (the decoder adopts the
                                                                // new SPS in place), so without this re-pin the buffer→view scale stays
                                                                // frozen at the creation-time resolution and every decoded frame renders
                                                                // zoomed by exactly oldRes/newRes (magnification ≈ 1/scale) until an
                                                                // unrelated recreate — the "starts zoomed, refreshes to full" bug.
                                                                // SurfaceView.setFixedSize no-ops when the size is unchanged, so calling
                                                                // it on every recomposition is free. (RemEx-oenj)
                                                                if (streamPixelWidth > 0 && streamPixelHeight > 0) {
                                                                        view.holder.setFixedSize(streamPixelWidth, streamPixelHeight)
                                                                }
                                                        },
                                                        // A SurfaceView's native surface is composited at the view's LAYOUT
                                                        // BOUNDS — Compose graphicsLayer (a draw-time transform) does NOT scale
                                                        // or move it, so zoom/pan applied via graphicsLayer left the H.264 image
                                                        // tiny and stranded in black while input/cursor/pan acted zoomed. Apply
                                                        // zoom/pan via layout instead: size the view to contentRect()*zoom and
                                                        // place it centered + panned. This matches mapHostToLocal exactly, so
                                                        // the video, cursor overlay, input, and pan-follow all stay aligned.
                                                        // The decode buffer size is pinned by holder.setFixedSize above, so the
                                                        // compositor just scales that buffer to these bounds (no surface churn).
                                                        modifier = Modifier.layout { measurable, constraints ->
                                                                val rect = contentRect()
                                                                val boxW = if (constraints.hasBoundedWidth) constraints.maxWidth else imageSize.width
                                                                val boxH = if (constraints.hasBoundedHeight) constraints.maxHeight else imageSize.height
                                                                val vw = if (rect.w > 0f) (rect.w * zoomFactor).roundToInt().coerceAtLeast(1) else boxW.coerceAtLeast(1)
                                                                val vh = if (rect.h > 0f) (rect.h * zoomFactor).roundToInt().coerceAtLeast(1) else boxH.coerceAtLeast(1)
                                                                val placeable = measurable.measure(Constraints.fixed(vw, vh))
                                                                layout(boxW.coerceAtLeast(0), boxH.coerceAtLeast(0)) {
                                                                        val x = ((boxW - vw) / 2f + panOffsetX).roundToInt()
                                                                        val y = ((boxH - vh) / 2f + panOffsetY).roundToInt()
                                                                        placeable.place(x, y)
                                                                }
                                                        }
                                                )
                                                }
                                        } else {
                                                if (safeFrame != null && !safeFrame.isRecycled) {
                                                        // RemEx-ki0t: this used to be
                                                        // key(<per-frame timestamp>) { Image(...) }, which forced
                                                        // Compose to tear down and rebuild the ENTIRE Image node
                                                        // (layout + graphicsLayer + modifier chain) on every
                                                        // incoming MJPEG frame just to force a redraw. A Canvas
                                                        // node is a stable call site (no key wrapper), so it is
                                                        // reused across frames — only the draw phase below reads
                                                        // the new bitmap and repaints; the graphicsLayer transform
                                                        // and layout stay intact. contentRect() is the app's own
                                                        // letterbox calculation, already documented (above) as
                                                        // producing the same fitted rect ContentScale.Fit did, so
                                                        // input mapping/cursor overlay stay pixel-aligned with what
                                                        // is actually drawn.
                                                        val frameDescription =
                                                                stringResource(R.string.cd_remote_desktop_frame)
                                                        // One wrapper per bitmap INSTANCE, not per
                                                        // frame. inBitmap reuse means this instance
                                                        // is stable across frames and only changes
                                                        // when the geometry does, so the allocation
                                                        // was pure churn. Pixels are read live from
                                                        // the underlying Bitmap at draw time, so a
                                                        // cached wrapper still shows new content.
                                                        // (RemEx-9esj)
                                                        val frameImage =
                                                                remember(safeFrame) {
                                                                        safeFrame.asImageBitmap()
                                                                }
                                                        Canvas(
                                                                modifier =
                                                                        Modifier.fillMaxSize()
                                                                                .semantics {
                                                                                        contentDescription =
                                                                                                frameDescription
                                                                                }
                                                                                .graphicsLayer {
                                                                                        scaleX = zoomFactor
                                                                                        scaleY = zoomFactor
                                                                                        translationX = panOffsetX
                                                                                        translationY = panOffsetY
                                                                                }
                                                        ) {
                                                                // THE REPAINT TRIGGER. Read here and
                                                                // nowhere else: a snapshot read in a
                                                                // draw lambda invalidates only the
                                                                // draw phase, so a new frame repaints
                                                                // without recomposing or re-laying
                                                                // out anything. Nothing else this
                                                                // lambda reads changes between frames
                                                                // — the bitmap instance is reused in
                                                                // place — so removing this read
                                                                // freezes the picture. (RemEx-9esj)
                                                                frameTick.longValue
                                                                val rect = contentRect()
                                                                if (rect.w > 0f && rect.h > 0f) {
                                                                        drawImage(
                                                                                image = frameImage,
                                                                                dstOffset =
                                                                                        IntOffset(
                                                                                                rect.x.roundToInt(),
                                                                                                rect.y.roundToInt()
                                                                                        ),
                                                                                dstSize =
                                                                                        IntSize(
                                                                                                rect.w.roundToInt()
                                                                                                        .coerceAtLeast(1),
                                                                                                rect.h.roundToInt()
                                                                                                        .coerceAtLeast(1)
                                                                                        )
                                                                        )
                                                                }
                                                        }
                                                }
                                        }

                                        // Cursor overlay: draw the host's actual cursor as an arrow at
                                        // its mapped on-screen position, so the user can see exactly
                                        // where they're pointing. Uses the same content rect as input
                                        // mapping, so the arrow, the video, and where clicks land all
                                        // agree. The cursor position is streamed rather than held in
                                        // uiState (RemEx-zc9r); -1 = none yet.
                                        // RD-B: gate the overlay on visibility in COMPOSITION (changes rarely),
                                        // but read the ANIMATED cursor position + pan/zoom inside the Canvas DRAW
                                        // scope below, so the per-frame cursor animation invalidates only the draw
                                        // phase (redraw), not composition. Reading animatedCursorX.value here in
                                        // composition forced a full recompose every animation frame.
                                        val cursorOverlayActive = uiState.isStreaming && uiState.hostCursorVisible

                                        if (cursorOverlayActive) {
                                                val cursorBmp = uiState.cursorBitmap
                                                Canvas(modifier = Modifier.fillMaxSize()) {
                                                        // Smoothed (animated) host position, read in the DRAW phase (RD-B).
                                                        val cursorLocal = mapHostToLocal(
                                                                animatedCursorX.value,
                                                                animatedCursorY.value
                                                        ) ?: return@Canvas
                                                        if (cursorBmp != null) {
                                                                // Draw the true native cursor bitmap (BGRA from the host) with
                                                                // its hotspot at the mapped position. Scale it the same way the
                                                                // video is scaled — content-rect width vs host width, times the
                                                                // pan/zoom factor — so it matches desktop content, then by the
                                                                // user's cursorScale preference.
                                                                val rect = contentRect()
                                                                val (hostW, _) = getHostScreenSize()
                                                                val pxPerHost =
                                                                        if (hostW > 0 && rect.w > 0f)
                                                                                (rect.w * zoomFactor) / hostW
                                                                        else 1f
                                                                val drawScale = pxPerHost * uiState.cursorScale
                                                                val w =
                                                                        (cursorBmp.width * drawScale)
                                                                                .roundToInt()
                                                                                .coerceAtLeast(1)
                                                                val h =
                                                                        (cursorBmp.height * drawScale)
                                                                                .roundToInt()
                                                                                .coerceAtLeast(1)
                                                                drawImage(
                                                                        image = cursorBmp,
                                                                        dstOffset =
                                                                                IntOffset(
                                                                                        (cursorLocal.x -
                                                                                                        uiState.cursorHotspotX *
                                                                                                                drawScale)
                                                                                                .roundToInt(),
                                                                                        (cursorLocal.y -
                                                                                                        uiState.cursorHotspotY *
                                                                                                                drawScale)
                                                                                                .roundToInt()
                                                                                ),
                                                                        dstSize = IntSize(w, h)
                                                                )
                                                        } else {
                                                                // Fallback generic arrow until the first cursor shape arrives,
                                                                // or for legacy hosts that don't stream cursor shapes.
                                                                // 1 arrow unit ≈ 1.4.dp → ~26dp tall pointer at scale 1.0;
                                                                // cursorScale (0.5–2.5) resizes it.
                                                                val s = 1.4.dp.toPx() * uiState.cursorScale
                                                                val ox = cursorLocal.x
                                                                val oy = cursorLocal.y
                                                                val arrow =
                                                                        Path().apply {
                                                                                moveTo(ox, oy)
                                                                                lineTo(ox, oy + 16f * s)
                                                                                lineTo(ox + 4f * s, oy + 12.5f * s)
                                                                                lineTo(ox + 6.5f * s, oy + 18.5f * s)
                                                                                lineTo(ox + 8.5f * s, oy + 17.5f * s)
                                                                                lineTo(ox + 6f * s, oy + 11.5f * s)
                                                                                lineTo(ox + 10.5f * s, oy + 11.5f * s)
                                                                                close()
                                                                        }
                                                                drawPath(arrow, Color.White)
                                                                drawPath(
                                                                        arrow,
                                                                        Color.Black,
                                                                        style = Stroke(width = 1.5.dp.toPx())
                                                                )
                                                        }
                                                }
                                        }
                                } else {
                                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                                                Icon(
                                                        imageVector = Icons.Default.Monitor,
                                                        contentDescription =
                                                                null /* decorative: the adjacent Text already says it (RemEx-xqli) */,
                                                        modifier = Modifier.size(64.dp),
                                                        tint =
                                                                MaterialTheme.colorScheme
                                                                        .onSurfaceVariant
                                                )
                                                Spacer(modifier = Modifier.height(16.dp))
                                                Text(
                                                        text =
                                                                when {
                                                                        uiState.desktopError !=
                                                                                null ->
                                                                                uiState.desktopError
                                                                        !uiState.capabilityState
                                                                                .supportsRemoteDesktop ->
                                                                                uiState.capabilityState
                                                                                        .unavailableReason
                                                                                        ?: stringResource(
                                                                                                R.string
                                                                                                        .remote_desktop_unavailable
                                                                                        )
                                                                        uiState.isStreaming ->
                                                                                stringResource(
                                                                                        R.string
                                                                                                .remote_desktop_waiting
                                                                                )
                                                                        else ->
                                                                                stringResource(
                                                                                        R.string
                                                                                                .remote_desktop_stopped
                                                                                )
                                                                },
                                                        color =
                                                                MaterialTheme.colorScheme
                                                                        .onSurfaceVariant,
                                                        style = MaterialTheme.typography.bodyLarge
                                                )
                                                // AN ERROR IS A DIFFERENT STATE FROM AN UNSUPPORTED
                                                // HOST (RemEx-5k4dd). Hiding this on a host that
                                                // genuinely cannot stream is right; hiding it when a
                                                // stream FAILED left the user with an error message
                                                // under a monitor icon and nothing to press, because
                                                // several failure paths also clear the capability
                                                // flag. The screen became a dead end reachable by
                                                // ordinary failure, with backing out to reconnect the
                                                // only way forward.
                                                //
                                                // The label still reads "start streaming" and that is
                                                // accurate - it does. Turning it into a cause-aware
                                                // "Retry" belongs with the guidance work on
                                                // RemEx-9wyu, where it would sit beside a message
                                                // explaining what failed rather than under a raw
                                                // native error string.
                                                if (shouldOfferStartAction(
                                                                isStreaming = uiState.isStreaming,
                                                                supportsRemoteDesktop =
                                                                        uiState.capabilityState
                                                                                .supportsRemoteDesktop,
                                                                hasError = uiState.desktopError != null
                                                        )
                                                ) {
                                                        Spacer(modifier = Modifier.height(16.dp))
                                                        FilledTonalButton(
                                                                onClick = { onStartStreaming() }
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.PlayArrow,
                                                                        contentDescription =
                                                                                null /* decorative: the adjacent Text already says it (RemEx-xqli) */,
                                                                        modifier =
                                                                                Modifier.size(18.dp)
                                                                )
                                                                Spacer(
                                                                        modifier =
                                                                                Modifier.width(8.dp)
                                                                )
                                                                Text(
                                                                        stringResource(
                                                                                R.string
                                                                                        .button_start_streaming
                                                                        )
                                                                )
                                                        }
                                                }
                                        }
                                }

                                // M3: AssistChip with AnimatedVisibility replaces raw Surface+Text
                                // for FPS overlay
                                // PlainAnimatedVisibility resolves ColumnScope.AnimatedVisibility
                                // implicit receiver conflict
                                PlainAnimatedVisibility(
                                        visible = uiState.isStreaming && showFpsOverlay,
                                        modifier =
                                                Modifier.align(Alignment.TopStart).padding(12.dp),
                                        enter =
                                                fadeIn(
                                                        animationSpec =
                                                                MaterialTheme.motionScheme
                                                                        .fastEffectsSpec()
                                                ),
                                        exit =
                                                fadeOut(
                                                        animationSpec =
                                                                MaterialTheme.motionScheme
                                                                        .fastEffectsSpec()
                                                )
                                ) {
                                        AssistChip(
                                                onClick = {},
                                                label = {
                                                        Text(
                                                                text = stringResource(R.string.remote_desktop_fps_overlay, fps.toInt()),
                                                                style =
                                                                        MaterialTheme.typography
                                                                                .labelSmall
                                                        )
                                                },
                                                colors =
                                                        AssistChipDefaults.assistChipColors(
                                                                containerColor =
                                                                        MaterialTheme.colorScheme
                                                                                .inverseSurface.copy(
                                                                                alpha = 0.55f
                                                                        ),
                                                                labelColor =
                                                                        MaterialTheme.colorScheme
                                                                                .inverseOnSurface
                                                        ),
                                                border = null
                                        )
                                }

                                if (uiState.isFullscreen) {
                                        // Action order mirrors the windowed TopAppBar (Keyboard, PC-keys,
                                        // Screenshot, Settings, fullscreen-toggle, Stop/Play) for the
                                        // actions both surfaces share; FPS is fullscreen-only and slotted
                                        // in without disturbing that shared relative order (RemEx-klq).
                                        Row(
                                                modifier =
                                                        Modifier.align(Alignment.TopEnd)
                                                                .padding(FullscreenOverlayEdgePadding),
                                                horizontalArrangement =
                                                        Arrangement.spacedBy(
                                                                FullscreenOverlayIconSpacing
                                                        )
                                        ) {
                                                // Keyboard toggle in fullscreen — same robust re-invoke
                                                // logic so the IME comes up every time without leaving
                                                // immersive mode (RemEx-46q).
                                                RemexTooltip(stringResource(R.string.cd_show_keyboard)) {
                                                        FilledTonalIconButton(
                                                                onClick = { toggleRemoteKeyboard() },
                                                                colors =
                                                                        toggleIconButtonColors(
                                                                                isRemoteKeyboardOpen
                                                                        )
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.Keyboard,
                                                                        contentDescription =
                                                                                stringResource(R.string.cd_show_keyboard)
                                                                )
                                                        }
                                                }
                                                // Independent from the Keyboard button above: toggles
                                                // the PC-keys (modifier) bar on its own, surviving IME
                                                // dismissal (RemEx-yi8o).
                                                RemexTooltip(stringResource(R.string.cd_show_pc_keys)) {
                                                        FilledTonalIconButton(
                                                                onClick = {
                                                                        pcKeysBarVisible = !pcKeysBarVisible
                                                                },
                                                                colors = toggleIconButtonColors(pcKeysBarVisible)
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.KeyboardCommandKey,
                                                                        contentDescription =
                                                                                stringResource(R.string.cd_show_pc_keys)
                                                                )
                                                        }
                                                }
                                                // FPS overlay toggle — re-added to the fullscreen
                                                // controls after the control-bar removal orphaned it
                                                // (onToggleFpsOverlay had no caller). Fullscreen-only:
                                                // no windowed-TopAppBar equivalent exists today.
                                                RemexTooltip(stringResource(R.string.remote_desktop_fps_overlay_btn)) {
                                                        FilledTonalIconButton(
                                                                onClick = { onToggleFpsOverlay() },
                                                                colors = toggleIconButtonColors(showFpsOverlay)
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.Speed,
                                                                        contentDescription =
                                                                                stringResource(
                                                                                        R.string.remote_desktop_fps_overlay_btn
                                                                                )
                                                                )
                                                        }
                                                }
                                                // Same slot as in the windowed TopAppBar, preserving the
                                                // shared action order (RemEx-klq). Worth having here in
                                                // particular: fullscreen is where the PC's screen is
                                                // actually being watched.
                                                RemexTooltip(stringResource(R.string.action_take_screenshot)) {
                                                        FilledTonalIconButton(
                                                                onClick = { onTakeScreenshot() },
                                                                colors =
                                                                        IconButtonDefaults
                                                                                .filledTonalIconButtonColors(
                                                                                        containerColor =
                                                                                                MaterialTheme
                                                                                                        .colorScheme
                                                                                                        .surfaceContainerHighest
                                                                                )
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.ScreenshotMonitor,
                                                                        contentDescription =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .action_take_screenshot
                                                                                )
                                                                )
                                                        }
                                                }
                                                // Settings button — open the (translucent) settings
                                                // overlay without having to leave fullscreen first.
                                                RemexTooltip(stringResource(R.string.cd_settings)) {
                                                        FilledTonalIconButton(
                                                                onClick = { showSettings = true },
                                                                colors =
                                                                        IconButtonDefaults
                                                                                .filledTonalIconButtonColors(
                                                                                        containerColor =
                                                                                                MaterialTheme
                                                                                                        .colorScheme
                                                                                                        .surfaceContainerHighest
                                                                                )
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.Tune,
                                                                        contentDescription =
                                                                                stringResource(R.string.cd_settings)
                                                                )
                                                        }
                                                }
                                                RemexTooltip(stringResource(R.string.cd_exit_fullscreen)) {
                                                        FilledTonalIconButton(
                                                                onClick = { onSetFullscreen(false) },
                                                                colors =
                                                                        IconButtonDefaults
                                                                                .filledTonalIconButtonColors(
                                                                                        containerColor =
                                                                                                MaterialTheme
                                                                                                        .colorScheme
                                                                                                        .surfaceContainerHighest
                                                                                )
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.FullscreenExit,
                                                                        contentDescription =
                                                                                stringResource(
                                                                                        R.string.cd_exit_fullscreen
                                                                                )
                                                                )
                                                        }
                                                }
                                                if (uiState.isStreaming) {
                                                        RemexTooltip(stringResource(R.string.cd_stop)) {
                                                                FilledTonalIconButton(
                                                                        onClick = { onStopStreaming() },
                                                                        colors =
                                                                                IconButtonDefaults
                                                                                        .filledTonalIconButtonColors(
                                                                                                containerColor =
                                                                                                        MaterialTheme
                                                                                                                .colorScheme
                                                                                                                .errorContainer
                                                                                        )
                                                                ) {
                                                                        Icon(
                                                                                Icons.Default.Stop,
                                                                                contentDescription =
                                                                                        stringResource(
                                                                                                R.string.cd_stop
                                                                                        ),
                                                                                tint =
                                                                                        MaterialTheme
                                                                                                .colorScheme
                                                                                                .onErrorContainer
                                                                        )
                                                                }
                                                        }
                                                } else {
                                                        RemexTooltip(
                                                                stringResource(R.string.button_start_streaming)
                                                        ) {
                                                        FilledTonalIconButton(
                                                                onClick = { onStartStreaming() },
                                                                enabled = !uiState.streamRequested,
                                                                colors =
                                                                        IconButtonDefaults
                                                                                .filledTonalIconButtonColors(
                                                                                        containerColor =
                                                                                                MaterialTheme
                                                                                                        .colorScheme
                                                                                                        .primaryContainer
                                                                                )
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.PlayArrow,
                                                                        contentDescription =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .button_start_streaming
                                                                                ),
                                                                        tint =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .onPrimaryContainer
                                                                )
                                                        }
                                                        }
                                                }
                                        }
                                }

                                // Floating PC-keys pill — an OVERLAY inside the video box rather
                                // than a Column sibling below it. As a sibling it stole layout
                                // height whenever it appeared (and, via imePadding, whenever the
                                // IME opened), shrinking the weight(1f) video box; imageSize keys
                                // the H.264 SurfaceView above, so every open/close/IME toggle tore
                                // down the decoder and blacked the stream until the next keyframe
                                // (RemEx-2y31). The inset-aware lane below consumes the nav-bar and
                                // IME insets INSTEAD of the video box: padding a child never
                                // resizes the box, so imageSize — and the SurfaceView — stay
                                // untouched while the pill rides above the IME.
                                Box(
                                        modifier =
                                                Modifier.matchParentSize()
                                                        .navigationBarsPadding()
                                                        .imePadding()
                                                        .onGloballyPositioned {
                                                                pcKeysLaneSize = it.size
                                                        }
                                ) {
                                        // PlainAnimatedVisibility: Box-in-Column overload
                                        // workaround, see its doc comment.
                                        PlainAnimatedVisibility(
                                                visible =
                                                        pcKeysBarVisible || isRemoteKeyboardOpen,
                                                modifier =
                                                        Modifier.align(Alignment.BottomCenter)
                                                                .offset {
                                                                        clampedPcKeysOffset(
                                                                                pcKeysOffsetX,
                                                                                pcKeysOffsetY,
                                                                                pcKeysLaneSize,
                                                                                pcKeysPillSize
                                                                        )
                                                                },
                                                enter =
                                                        slideInVertically(
                                                                animationSpec = MaterialTheme.motionScheme.fastSpatialSpec(),
                                                                initialOffsetY = { it }
                                                        ) + fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
                                                exit =
                                                        slideOutVertically(
                                                                animationSpec = MaterialTheme.motionScheme.fastSpatialSpec(),
                                                                targetOffsetY = { it }
                                                        ) + fadeOut(MaterialTheme.motionScheme.fastEffectsSpec())
                                        ) {
                                                PcKeysBar(
                                                        modifierStates = modifierStates,
                                                        onCycleModifier = onCycleModifier,
                                                        onSendKeyPress = onSendKeyPress,
                                                        view = view,
                                                        extraKeysExpanded = extraKeysExpanded,
                                                        onToggleExtraKeys = {
                                                                extraKeysExpanded =
                                                                        !extraKeysExpanded
                                                        },
                                                        onDrag = { drag ->
                                                                val clamped =
                                                                        clampedPcKeysOffset(
                                                                                pcKeysOffsetX +
                                                                                        drag.x,
                                                                                pcKeysOffsetY +
                                                                                        drag.y,
                                                                                pcKeysLaneSize,
                                                                                pcKeysPillSize
                                                                        )
                                                                pcKeysOffsetX =
                                                                        clamped.x.toFloat()
                                                                pcKeysOffsetY =
                                                                        clamped.y.toFloat()
                                                        },
                                                        onPillSizeChanged = {
                                                                pcKeysPillSize = it
                                                        }
                                                )
                                        }
                                }

                                // Screenshot outcome (RemEx-byij). LAST CHILD OF THIS BOX ON PURPOSE.
                                // A Box paints its children in source order, and the PC-keys lane just
                                // above also anchors BottomCenter — emitted any earlier, this
                                // confirmation is painted over by it. That is not a rare collision: the
                                // lane shows whenever the PC-keys bar is on OR the remote keyboard is
                                // open, and the PC-keys toggle sits in the very control row the
                                // screenshot button was added to.
                                //
                                // Bottom rather than top because in fullscreen the top edge already
                                // carries the FPS chip at TopStart and the control row at TopEnd, and
                                // because it is where a snackbar would have appeared — which is where
                                // people look. This screen has no snackbar to use: its only SnackbarHost
                                // is declared inside the settings sheet (RemEx-vj31) and renders only
                                // while that sheet is open.
                                //
                                // NOT a Surface, and REVIEW CAUGHT THE VERSION THAT WAS ONE. A
                                // non-clickable Surface installs `pointerInput(Unit) {}` and eats every
                                // touch inside its bounds — blocking touch propagation is one of the
                                // things Surface documents itself as doing. Sitting on top of the
                                // PC-keys bar for five seconds, it would have swallowed taps on Ctrl,
                                // Alt and Shift, which would look exactly like the modifier keys being
                                // broken. A clipped Box draws identically and intercepts nothing.
                                //
                                // Not the AssistChip the FPS overlay uses either: a chip is a fixed
                                // height and would clip this sentence in the longer translations. The
                                // colours are the two theme roles that chip already pairs, so both stay
                                // legible under every M3 scheme — seeded, dynamic or static — at either
                                // contrast extreme.
                                //
                                // imePadding to match the lane below: this Box deliberately does not
                                // consume the IME or nav-bar insets (doing so would resize the video and
                                // tear down the decoder, RemEx-2y31), so a child anchored to its bottom
                                // edge renders UNDER an open keyboard unless it insets itself.
                                PlainAnimatedVisibility(
                                        visible = screenshotStatus != null,
                                        modifier =
                                                Modifier.align(Alignment.BottomCenter)
                                                        .navigationBarsPadding()
                                                        .imePadding()
                                                        .padding(16.dp)
                                                        // Clears the PC-keys lane when it is showing, so
                                                        // the confirmation never hides the modifier latch
                                                        // states it would otherwise cover.
                                                        .padding(
                                                                bottom =
                                                                        if (pcKeysBarVisible ||
                                                                                        isRemoteKeyboardOpen
                                                                        )
                                                                                PcKeysLaneClearance
                                                                        else 0.dp
                                                        )
                                ) {
                                        Box(
                                                modifier =
                                                        Modifier.clip(MaterialTheme.shapes.small)
                                                                .background(
                                                                        MaterialTheme.colorScheme
                                                                                .inverseSurface.copy(
                                                                                alpha = 0.88f
                                                                        )
                                                                )
                                        ) {
                                                Text(
                                                        // Held across the exit fade: reading
                                                        // screenshotStatus directly would blank the text
                                                        // the instant it went null, so the pill would
                                                        // fade out empty.
                                                        text = lastScreenshotStatus.orEmpty(),
                                                        style = MaterialTheme.typography.labelLarge,
                                                        color =
                                                                MaterialTheme.colorScheme
                                                                        .inverseOnSurface,
                                                        modifier =
                                                                Modifier.widthIn(max = 320.dp)
                                                                        .padding(
                                                                                horizontal = 14.dp,
                                                                                vertical = 10.dp
                                                                        )
                                                )
                                        }
                                }
                        }

                        if (showSettings) {
                                ModalBottomSheet(
                                        onDismissRequest = { showSettings = false },
                                        sheetState = sheetState,
                                        // Translucent so the desktop stays partly visible behind the
                                        // settings, and no dim scrim over the stream.
                                        containerColor =
                                                MaterialTheme.colorScheme.surface.copy(alpha = 0.88f),
                                        scrimColor = Color.Transparent
                                ) {
                                        val isLandscape =
                                                LocalConfiguration.current.orientation ==
                                                        Configuration.ORIENTATION_LANDSCAPE
                                        // Hosted here (not in the screen's own Scaffold) because a
                                        // ModalBottomSheet renders in its own window above the
                                        // Scaffold — a Scaffold-hosted Snackbar would be invisible
                                        // behind the open sheet. (RemEx-vj31)
                                        val presetSnackbarHostState = remember { SnackbarHostState() }
                                        val unlimitedOverflowWarning =
                                                stringResource(
                                                        R.string
                                                                .remote_desktop_preset_unlimited_overflow_warning
                                                )
                                        LaunchedEffect(config.preset) {
                                                if (config.preset == DesktopPreset.UNLIMITED &&
                                                                !hasShownUnlimitedWarning
                                                ) {
                                                        presetSnackbarHostState.showSnackbar(
                                                                message = unlimitedOverflowWarning,
                                                                duration = SnackbarDuration.Long
                                                        )
                                                        onMarkUnlimitedWarningShown()
                                                }
                                        }
                                        Box(modifier = Modifier.fillMaxWidth()) {
                                        Column(
                                                modifier =
                                                        Modifier.fillMaxWidth()
                                                                // Scrollable so every control is
                                                                // reachable even when the sheet is
                                                                // short (landscape).
                                                                .verticalScroll(
                                                                        rememberScrollState()
                                                                )
                                                                .padding(
                                                                        horizontal = 24.dp,
                                                                        vertical = 16.dp
                                                                )
                                                                .navigationBarsPadding()
                                                                .imePadding(),
                                                verticalArrangement = Arrangement.spacedBy(16.dp)
                                        ) {
                                                // Header: title + close button
                                                Row(
                                                        modifier = Modifier.fillMaxWidth(),
                                                        horizontalArrangement =
                                                                Arrangement.SpaceBetween,
                                                        verticalAlignment =
                                                                Alignment.CenterVertically
                                                ) {
                                                        Text(
                                                                stringResource(
                                                                        R.string
                                                                                .remote_desktop_config_title
                                                                ),
                                                                style =
                                                                        MaterialTheme.typography
                                                                                .titleLargeEmphasized
                                                        )
                                                        RemexTooltip(stringResource(R.string.cd_close_settings)) {
                                                                IconButton(
                                                                        onClick = { showSettings = false }
                                                                ) {
                                                                        Icon(
                                                                                Icons.Default.Close,
                                                                                contentDescription =
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .cd_close_settings
                                                                                        )
                                                                        )
                                                                }
                                                        }
                                                }

                                                // ── Display section ── (only when there is an
                                                // actual choice: 2+ monitors, or a combined option)
                                                if (uiState.displayTargets.size >= 2) {
                                                        SettingsSectionHeader(
                                                                stringResource(
                                                                        R.string
                                                                                .remote_desktop_display_label
                                                                )
                                                        )
                                                        Row(
                                                                modifier =
                                                                        Modifier.fillMaxWidth()
                                                                                .horizontalScroll(
                                                                                        rememberScrollState()
                                                                                ),
                                                                horizontalArrangement =
                                                                        Arrangement.spacedBy(8.dp)
                                                        ) {
                                                                uiState.displayTargets.forEach {
                                                                        target ->
                                                                        FilterChip(
                                                                                selected =
                                                                                        target.token ==
                                                                                                uiState.selectedDisplayToken,
                                                                                onClick = {
                                                                                        onSelectDisplayTarget(
                                                                                                target.token
                                                                                        )
                                                                                },
                                                                                label = {
                                                                                        Text(
                                                                                                target.label
                                                                                        )
                                                                                }
                                                                        )
                                                                }
                                                        }
                                                }

                                                // ── Stream section ──
                                                SettingsSectionHeader(
                                                        stringResource(
                                                                R.string
                                                                        .remote_desktop_section_stream
                                                        )
                                                )
                                                // Named quality presets bundle {quality, targetFps,
                                                // scale} together (persist + push once). Capture SCALE
                                                // is the real FPS lever — host encode time scales with
                                                // pixel count. Custom has no fixed bundle: it reveals
                                                // the raw sliders below instead. (RemEx-vj31)
                                                Row(
                                                        modifier =
                                                                Modifier.fillMaxWidth()
                                                                        .horizontalScroll(
                                                                                rememberScrollState()
                                                                        )
                                                                        .padding(bottom = 4.dp),
                                                        horizontalArrangement =
                                                                Arrangement.spacedBy(8.dp)
                                                ) {
                                                        DESKTOP_PRESET_BUNDLES.forEach { bundle ->
                                                                FilterChip(
                                                                        selected =
                                                                                config.preset ==
                                                                                        bundle.preset,
                                                                        onClick = {
                                                                                onApplyPreset(
                                                                                        bundle.preset,
                                                                                        bundle.quality,
                                                                                        bundle.targetFps,
                                                                                        bundle.scale
                                                                                )
                                                                        },
                                                                        label = {
                                                                                Text(
                                                                                        desktopPresetLabel(
                                                                                                bundle.preset
                                                                                        )
                                                                                )
                                                                        }
                                                                )
                                                        }
                                                        FilterChip(
                                                                selected =
                                                                        config.preset ==
                                                                                DesktopPreset.CUSTOM,
                                                                onClick = onSelectCustomPreset,
                                                                label = {
                                                                        Text(
                                                                                desktopPresetLabel(
                                                                                        DesktopPreset
                                                                                                .CUSTOM
                                                                                )
                                                                        )
                                                                }
                                                        )
                                                }
                                                // Persistent heads-up on Unlimited: it targets the
                                                // technical ceiling (full resolution, uncapped fps),
                                                // which this capture pipeline can't always sustain —
                                                // the same overflow the one-time snackbar explains.
                                                PlainAnimatedVisibility(
                                                        visible =
                                                                config.preset ==
                                                                        DesktopPreset.UNLIMITED
                                                ) {
                                                        Row(
                                                                verticalAlignment =
                                                                        Alignment.CenterVertically,
                                                                horizontalArrangement =
                                                                        Arrangement.spacedBy(6.dp)
                                                        ) {
                                                                Icon(
                                                                        Icons.Default.Info,
                                                                        contentDescription = null,
                                                                        modifier =
                                                                                Modifier.size(16.dp),
                                                                        tint =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .onSurfaceVariant
                                                                )
                                                                Text(
                                                                        stringResource(
                                                                                R.string
                                                                                        .remote_desktop_preset_unlimited_info
                                                                        ),
                                                                        style =
                                                                                MaterialTheme
                                                                                        .typography
                                                                                        .bodySmall,
                                                                        color =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .onSurfaceVariant
                                                                )
                                                        }
                                                }
                                                PlainAnimatedVisibility(
                                                        visible =
                                                                config.preset ==
                                                                        DesktopPreset.CUSTOM
                                                ) {
                                                        Column(
                                                                verticalArrangement =
                                                                        Arrangement.spacedBy(16.dp)
                                                        ) {
                                                                SettingsPair(
                                                                        isLandscape = isLandscape,
                                                                        first = { m ->
                                                                                SettingSlider(
                                                                                        label =
                                                                                                stringResource(
                                                                                                        R.string
                                                                                                                .remote_desktop_quality_label,
                                                                                                        config.quality
                                                                                                ),
                                                                                        value =
                                                                                                config.quality
                                                                                                        .toFloat(),
                                                                                        onValueChange = {
                                                                                                onUpdateQuality(
                                                                                                        it.toInt()
                                                                                                )
                                                                                        },
                                                                                        valueRange =
                                                                                                1f..100f,
                                                                                        modifier = m
                                                                                )
                                                                        },
                                                                        second = { m ->
                                                                                SettingSlider(
                                                                                        label =
                                                                                                if (config.targetFps >
                                                                                                                DESKTOP_FPS_PACED_MAX
                                                                                                )
                                                                                                        stringResource(
                                                                                                                R.string
                                                                                                                        .remote_desktop_fps_unlimited_label
                                                                                                        )
                                                                                                else
                                                                                                        stringResource(
                                                                                                                R.string
                                                                                                                        .remote_desktop_fps_label,
                                                                                                                config.targetFps
                                                                                                        ),
                                                                                        value =
                                                                                                config.targetFps
                                                                                                        .toFloat(),
                                                                                        onValueChange = {
                                                                                                onUpdateTargetFps(
                                                                                                        it.toInt()
                                                                                                )
                                                                                        },
                                                                                        valueRange =
                                                                                                1f..DESKTOP_MAX_FPS
                                                                                                        .toFloat(),
                                                                                        modifier = m
                                                                                )
                                                                        }
                                                                )
                                                                SettingSlider(
                                                                        label =
                                                                                stringResource(
                                                                                        R.string
                                                                                                .remote_desktop_scale_label,
                                                                                        (config.scale *
                                                                                                        100)
                                                                                                .toInt()
                                                                                ),
                                                                        value = config.scale,
                                                                        onValueChange = {
                                                                                onUpdateScale(it)
                                                                        },
                                                                        valueRange = 0.25f..1.0f,
                                                                        modifier =
                                                                                Modifier.fillMaxWidth()
                                                                )
                                                        }
                                                }

                                                HorizontalDivider()

                                                // ── Input & Controls section ──
                                                SettingsSectionHeader(
                                                        stringResource(
                                                                R.string
                                                                        .remote_desktop_section_input
                                                        )
                                                )

                                                // Direct Touch toggle (full width — own row so it
                                                // never collides with a slider beside it)
                                                Row(
                                                        modifier = Modifier.fillMaxWidth()
                                                                .toggleable(
                                                                        value = uiState.directTouch,
                                                                        role = Role.Switch,
                                                                        onValueChange = { onUpdateDirectTouch(it) }
                                                                ),
                                                        horizontalArrangement =
                                                                Arrangement.SpaceBetween,
                                                        verticalAlignment =
                                                                Alignment.CenterVertically
                                                ) {
                                                        Column(modifier = Modifier.weight(1f)) {
                                                                Text(
                                                                        stringResource(
                                                                                R.string
                                                                                        .remote_desktop_direct_touch_label
                                                                        ),
                                                                        style =
                                                                                MaterialTheme
                                                                                        .typography
                                                                                        .bodyLargeEmphasized
                                                                )
                                                                Text(
                                                                        stringResource(
                                                                                R.string
                                                                                        .remote_desktop_direct_touch_desc
                                                                        ),
                                                                        style =
                                                                                MaterialTheme
                                                                                        .typography
                                                                                        .bodySmall,
                                                                        color =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .onSurfaceVariant
                                                                )
                                                        }
                                                        Spacer(Modifier.width(16.dp))
                                                        Switch(
                                                                checked = uiState.directTouch,
                                                                onCheckedChange = null
                                                        )
                                                }

                                                SettingsPair(
                                                        isLandscape = isLandscape,
                                                        first = { m ->
                                                                SettingSlider(
                                                                        label =
                                                                                String.format(
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .remote_desktop_pointer_speed_label
                                                                                        ),
                                                                                        uiState.pointerSpeed
                                                                                ),
                                                                        value =
                                                                                uiState.pointerSpeed,
                                                                        onValueChange = {
                                                                                onUpdatePointerSpeed(
                                                                                        it
                                                                                )
                                                                        },
                                                                        valueRange = 0.25f..3.0f,
                                                                        steps = 10,
                                                                        modifier = m
                                                                )
                                                        },
                                                        second = { m ->
                                                                SettingSlider(
                                                                        label =
                                                                                String.format(
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .remote_desktop_cursor_size_label
                                                                                        ),
                                                                                        uiState.cursorScale
                                                                                ),
                                                                        value =
                                                                                uiState.cursorScale,
                                                                        onValueChange = {
                                                                                onUpdateCursorScale(
                                                                                        it
                                                                                )
                                                                        },
                                                                        valueRange = 0.5f..2.5f,
                                                                        steps = 7,
                                                                        modifier = m
                                                                )
                                                        }
                                                )

                                                SettingsPair(
                                                        isLandscape = isLandscape,
                                                        first = { m ->
                                                                SettingSlider(
                                                                        label =
                                                                                String.format(
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .remote_desktop_v_scroll_short
                                                                                        ),
                                                                                        uiState.vScrollSensitivity
                                                                                ),
                                                                        value =
                                                                                uiState.vScrollSensitivity,
                                                                        onValueChange = {
                                                                                onUpdateScrollSensitivity(
                                                                                        it,
                                                                                        uiState.hScrollSensitivity
                                                                                )
                                                                        },
                                                                        valueRange = 0.1f..5.0f,
                                                                        steps = 20,
                                                                        modifier = m
                                                                )
                                                        },
                                                        second = { m ->
                                                                SettingSlider(
                                                                        label =
                                                                                String.format(
                                                                                        stringResource(
                                                                                                R.string
                                                                                                        .remote_desktop_h_scroll_short
                                                                                        ),
                                                                                        uiState.hScrollSensitivity
                                                                                ),
                                                                        value =
                                                                                uiState.hScrollSensitivity,
                                                                        onValueChange = {
                                                                                onUpdateScrollSensitivity(
                                                                                        uiState.vScrollSensitivity,
                                                                                        it
                                                                                )
                                                                        },
                                                                        valueRange = 0.1f..5.0f,
                                                                        steps = 20,
                                                                        modifier = m
                                                                )
                                                        }
                                                )

                                                Text(
                                                        text =
                                                                stringResource(
                                                                        R.string
                                                                                .remote_desktop_controls_hint_v2
                                                                ),
                                                        style = MaterialTheme.typography.bodySmall,
                                                        color =
                                                                MaterialTheme.colorScheme
                                                                        .onSurfaceVariant
                                                )

                                                // THIS SECTION IS EASY TO BELIEVE IS MISSING, and one
                                                // investigation already concluded exactly that
                                                // (RemEx-is52): it sits at the very bottom of the
                                                // settings sheet, below the Input & Controls hint, so
                                                // a reader who stops scrolling there sees nothing.
                                                //
                                                // It is also hidden entirely unless the host advertises
                                                // supportsAdvancedWindowControl — which on Windows was
                                                // hard-coded false for a while even though the host had
                                                // a complete Win32 backend registered and dispatching.
                                                // If this section is absent on a host you expect to
                                                // support it, suspect the ADVERTISEMENT before
                                                // suspecting this UI: see
                                                // HostCapabilitiesProvider.SupportsAdvancedWindowControl.
                                                // On Linux it additionally needs kdotool or xdotool on
                                                // PATH (LinuxDesktopBackendProbe).
                                                if (uiState.capabilityState
                                                                .supportsAdvancedWindowControl
                                                ) {
                                                        HorizontalDivider()
                                                        Text(
                                                                text = stringResource(R.string.remote_desktop_window_controls_header),
                                                                style =
                                                                        MaterialTheme.typography
                                                                                .titleMediumEmphasized
                                                        )
                                                        Text(
                                                                text =
                                                                        stringResource(R.string.remote_desktop_backend, uiState.capabilityState.windowBackend ?: "host"),
                                                                style =
                                                                        MaterialTheme.typography
                                                                                .bodySmall,
                                                                color =
                                                                        MaterialTheme.colorScheme
                                                                                .onSurfaceVariant
                                                        )

                                                        OutlinedTextField(
                                                                value = windowSearch,
                                                                onValueChange = {
                                                                        windowSearch = it
                                                                },
                                                                label = { Text(stringResource(R.string.remote_desktop_search_windows_label)) },
                                                                modifier = Modifier.fillMaxWidth(),
                                                                singleLine = true
                                                        )

                                                        FilledTonalButton(
                                                                onClick = {
                                                                        onQueryWindows(windowSearch)
                                                                }
                                                        ) { Text(stringResource(R.string.remote_desktop_refresh_windows)) }

                                                        if (windowActionError != null) {
                                                                Text(
                                                                        text = windowActionError,
                                                                        color =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .error,
                                                                        style =
                                                                                MaterialTheme
                                                                                        .typography
                                                                                        .bodySmall
                                                                )
                                                        }

                                                        if (windowResults.isEmpty()) {
                                                                Text(
                                                                        text =
                                                                                stringResource(R.string.remote_desktop_no_results),
                                                                        style =
                                                                                MaterialTheme
                                                                                        .typography
                                                                                        .bodySmall,
                                                                        color =
                                                                                MaterialTheme
                                                                                        .colorScheme
                                                                                        .onSurfaceVariant
                                                                )
                                                        } else {
                                                                Column(
                                                                        verticalArrangement =
                                                                                Arrangement
                                                                                        .spacedBy(
                                                                                                8.dp
                                                                                        )
                                                                ) {
                                                                        windowResults.take(6)
                                                                                .forEach { window ->
                                                                                        ElevatedCard(
                                                                                                onClick = {
                                                                                                        selectedWindowId =
                                                                                                                window.id
                                                                                                }
                                                                                        ) {
                                                                                                Column(
                                                                                                        modifier =
                                                                                                                Modifier.fillMaxWidth()
                                                                                                                        .padding(
                                                                                                                                12.dp
                                                                                                                        ),
                                                                                                        verticalArrangement =
                                                                                                                Arrangement
                                                                                                                        .spacedBy(
                                                                                                                                6.dp
                                                                                                                        )
                                                                                                ) {
                                                                                                        Text(
                                                                                                                text =
                                                                                                                        window.title,
                                                                                                                style =
                                                                                                                        MaterialTheme
                                                                                                                                .typography
                                                                                                                                .bodyLarge,
                                                                                                                fontWeight =
                                                                                                                        FontWeight
                                                                                                                                .SemiBold
                                                                                                        )
                                                                                                        Text(
                                                                                                                text =
                                                                                                                        buildString {
                                                                                                                                append(
                                                                                                                                        window.className
                                                                                                                                                ?: stringResource(R.string.remote_desktop_unknown_class)
                                                                                                                                )
                                                                                                                                window.desktopNumber
                                                                                                                                        ?.let {
                                                                                                                                                append(
                                                                                                                                                        stringResource(
                                                                                                                                                                R.string.remote_desktop_desktop_number,
                                                                                                                                                                it
                                                                                                                                                        )
                                                                                                                                                )
                                                                                                                                        }
                                                                                                                                if (window.isActive
                                                                                                                                ) {
                                                                                                                                        append(
                                                                                                                                                stringResource(
                                                                                                                                                        R.string.remote_desktop_active_status
                                                                                                                                                )
                                                                                                                                        )
                                                                                                                                }
                                                                                                                        },
                                                                                                                style =
                                                                                                                        MaterialTheme
                                                                                                                                .typography
                                                                                                                                .bodySmall,
                                                                                                                color =
                                                                                                                        MaterialTheme
                                                                                                                                .colorScheme
                                                                                                                                .onSurfaceVariant
                                                                                                        )
                                                                                                        Row(
                                                                                                                horizontalArrangement =
                                                                                                                        Arrangement
                                                                                                                                .spacedBy(
                                                                                                                                        8.dp
                                                                                                                                )
                                                                                                        ) {
                                                                                                                TextButton(
                                                                                                                        onClick = {
                                                                                                                                selectedWindowId =
                                                                                                                                        window.id
                                                                                                                                onActivateWindow(
                                                                                                                                        window.id
                                                                                                                                )
                                                                                                                        }
                                                                                                                ) {
                                                                                                                        Text(
                                                                                                                                stringResource(R.string.remote_desktop_action_activate)
                                                                                                                        )
                                                                                                                }
                                                                                                                TextButton(
                                                                                                                        onClick = {
                                                                                                                                selectedWindowId =
                                                                                                                                        window.id
                                                                                                                                onRaiseWindow(
                                                                                                                                        window.id
                                                                                                                                )
                                                                                                                        }
                                                                                                                ) {
                                                                                                                        Text(
                                                                                                                                stringResource(R.string.remote_desktop_action_raise)
                                                                                                                        )
                                                                                                                }
                                                                                                                TextButton(
                                                                                                                        onClick = {
                                                                                                                                selectedWindowId =
                                                                                                                                        window.id
                                                                                                                                onMinimizeWindow(
                                                                                                                                        window.id
                                                                                                                                )
                                                                                                                        }
                                                                                                                ) {
                                                                                                                        Text(
                                                                                                                                stringResource(R.string.remote_desktop_action_minimize)
                                                                                                                        )
                                                                                                                }
                                                                                                                TextButton(
                                                                                                                        onClick = {
                                                                                                                                selectedWindowId =
                                                                                                                                        window.id
                                                                                                                                onCloseWindow(
                                                                                                                                        window.id
                                                                                                                                )
                                                                                                                        }
                                                                                                                ) {
                                                                                                                        Text(
                                                                                                                                stringResource(R.string.remote_desktop_action_close)
                                                                                                                        )
                                                                                                                }
                                                                                                        }
                                                                                                }
                                                                                        }
                                                                                }
                                                                }
                                                        }

                                                        if (selectedWindow != null) {
                                                                Text(
                                                                        text =
                                                                                stringResource(R.string.remote_desktop_selected, selectedWindow.title),
                                                                        style =
                                                                                MaterialTheme
                                                                                        .typography
                                                                                        .bodyMediumEmphasized
                                                                )
                                                                Row(
                                                                        horizontalArrangement =
                                                                                Arrangement
                                                                                        .spacedBy(
                                                                                                8.dp
                                                                                        )
                                                                ) {
                                                                        OutlinedTextField(
                                                                                value =
                                                                                        resizeWidthText,
                                                                                onValueChange = {
                                                                                        resizeWidthText =
                                                                                                it
                                                                                },
                                                                                label = {
                                                                                        Text(
                                                                                                stringResource(R.string.remote_desktop_width)
                                                                                        )
                                                                                },
                                                                                modifier =
                                                                                        Modifier.weight(
                                                                                                1f
                                                                                        ),
                                                                                singleLine = true
                                                                        )
                                                                        OutlinedTextField(
                                                                                value =
                                                                                        resizeHeightText,
                                                                                onValueChange = {
                                                                                        resizeHeightText =
                                                                                                it
                                                                                },
                                                                                label = {
                                                                                        Text(
                                                                                                stringResource(R.string.remote_desktop_height)
                                                                                        )
                                                                                },
                                                                                modifier =
                                                                                        Modifier.weight(
                                                                                                1f
                                                                                        ),
                                                                                singleLine = true
                                                                        )
                                                                }
                                                                FilledTonalButton(
                                                                        onClick = {
                                                                                val width =
                                                                                        resizeWidthText
                                                                                                .toIntOrNull()
                                                                                val height =
                                                                                        resizeHeightText
                                                                                                .toIntOrNull()
                                                                                if (width != null &&
                                                                                                height !=
                                                                                                        null
                                                                                ) {
                                                                                        onResizeWindow(
                                                                                                selectedWindow
                                                                                                        .id,
                                                                                                width,
                                                                                                height
                                                                                        )
                                                                                }
                                                                        }
                                                                ) { Text(stringResource(R.string.remote_desktop_resize_selected)) }

                                                                OutlinedTextField(
                                                                        value = targetDesktopText,
                                                                        onValueChange = {
                                                                                targetDesktopText =
                                                                                        it
                                                                        },
                                                                        label = {
                                                                                Text(stringResource(R.string.remote_desktop_desktop_label))
                                                                        },
                                                                        modifier =
                                                                                Modifier.fillMaxWidth(),
                                                                        singleLine = true
                                                                )
                                                                FilledTonalButton(
                                                                        onClick = {
                                                                                val desktop =
                                                                                        targetDesktopText
                                                                                                .toIntOrNull()
                                                                                if (desktop != null
                                                                                ) {
                                                                                        onMoveWindowToDesktop(
                                                                                                selectedWindow
                                                                                                        .id,
                                                                                                desktop
                                                                                        )
                                                                                }
                                                                        }
                                                                ) { Text(stringResource(R.string.remote_desktop_move_selected)) }
                                                        }
                                                }
                                        }
                                        SnackbarHost(
                                                hostState = presetSnackbarHostState,
                                                modifier =
                                                        Modifier.align(Alignment.BottomCenter)
                                                                .padding(bottom = 16.dp)
                                        )
                                        }
                                }
                        }

                        // The PC-keys bar used to live here as a Column sibling; it is now the
                        // floating PcKeysBar pill overlaid inside the video box above, so showing
                        // it (or the IME) no longer resizes the video and blacks the stream
                        // (RemEx-2y31).
                }
        }
}

/**
 * Clamps the floating PC-keys pill's drag offset (relative to its bottom-center anchor) so the
 * pill always stays fully inside the inset-aware overlay lane, whatever the lane's current size —
 * a position dragged in landscape self-heals after rotating to portrait, and the pill can never be
 * stranded off-screen (RemEx-2y31). Y is <= 0 (up from the bottom edge only); X is symmetric
 * around the horizontal center.
 */
private fun clampedPcKeysOffset(
        offsetX: Float,
        offsetY: Float,
        lane: IntSize,
        pill: IntSize
): IntOffset {
        if (lane == IntSize.Zero || pill == IntSize.Zero) {
                return IntOffset(offsetX.roundToInt(), offsetY.roundToInt())
        }
        val maxX = ((lane.width - pill.width) / 2f).coerceAtLeast(0f)
        val maxUp = (lane.height - pill.height).toFloat().coerceAtLeast(0f)
        return IntOffset(
                offsetX.coerceIn(-maxX, maxX).roundToInt(),
                offsetY.coerceIn(-maxUp, 0f).roundToInt()
        )
}

/**
 * Floating PC-keys pill (RemEx-2y31): the modifier chips (Ctrl/Alt/Shift/Win/AltGr, latching via
 * onCycleModifier — OFF -> ARMED -> LOCKED -> OFF) plus utility keys and the F-key/nav-key grid,
 * hosted as a draggable overlay ON TOP of the video box instead of the old docked full-width bar
 * below it — so showing it (or the IME) never resizes the video box, never rebuilds the H.264
 * SurfaceView, and never blacks the stream. The grip row at the top is the drag surface; [onDrag]
 * receives raw pixel deltas and the caller clamps + persists the position.
 */
@Composable
private fun PcKeysBar(
        modifierStates: Map<Int, ModifierState>,
        onCycleModifier: (Int) -> Unit,
        onSendKeyPress: (Int) -> Unit,
        view: android.view.View,
        extraKeysExpanded: Boolean,
        onToggleExtraKeys: () -> Unit,
        onDrag: (Offset) -> Unit,
        onPillSizeChanged: (IntSize) -> Unit
) {
        Surface(
                shape = MaterialTheme.shapes.large,
                color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.95f),
                shadowElevation = 6.dp,
                modifier =
                        Modifier.padding(horizontal = 12.dp, vertical = 8.dp)
                                .onGloballyPositioned { onPillSizeChanged(it.size) }
        ) {
                Column {
                        // Grip: the drag surface that moves the pill anywhere over the stream.
                        Box(
                                modifier =
                                        Modifier.fillMaxWidth()
                                                .pointerInput(Unit) {
                                                        detectDragGestures { change, dragAmount ->
                                                                change.consume()
                                                                onDrag(dragAmount)
                                                        }
                                                }
                                                .padding(top = 4.dp),
                                contentAlignment = Alignment.Center
                        ) {
                                Icon(
                                        Icons.Default.DragIndicator,
                                        contentDescription =
                                                stringResource(R.string.cd_move_pc_keys),
                                        modifier = Modifier.size(18.dp),
                                        tint = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                        }
                        // vk -> (label, contentDescription). Latching: tapping cycles
                        // OFF -> ARMED -> LOCKED -> OFF via onCycleModifier; sendKeyPress
                        // applies ARMED/LOCKED modifiers to the next non-modifier key.
                        val modifierKeys =
                                listOf(
                                        Triple(17, "Ctrl", stringResource(R.string.cd_key_ctrl)),
                                        Triple(18, "Alt", stringResource(R.string.cd_key_alt)),
                                        Triple(16, "Shift", stringResource(R.string.cd_key_shift)),
                                        Triple(91, "⊞", stringResource(R.string.cd_key_windows)),
                                        Triple(
                                                165,
                                                "AltGr",
                                                stringResource(R.string.cd_key_altgr)
                                        ),
                                )
                        val utilKeys =
                                listOf(
                                        Triple("Esc", stringResource(R.string.cd_key_escape), 27),
                                        Triple("Tab", stringResource(R.string.cd_key_tab), 9),
                                        Triple("⌫", stringResource(R.string.cd_key_backspace), 8),
                                        Triple("↵", stringResource(R.string.cd_key_enter), 13),
                                        Triple(
                                                "←",
                                                stringResource(R.string.cd_key_arrow_left),
                                                37
                                        ),
                                        Triple("↑", stringResource(R.string.cd_key_arrow_up), 38),
                                        Triple(
                                                "↓",
                                                stringResource(R.string.cd_key_arrow_down),
                                                40
                                        ),
                                        Triple(
                                                "→",
                                                stringResource(R.string.cd_key_arrow_right),
                                                39
                                        ),
                                        Triple("Del", stringResource(R.string.cd_key_delete), 46),
                                )
                        Row(
                                modifier =
                                        Modifier.horizontalScroll(rememberScrollState())
                                                .padding(vertical = 2.dp, horizontal = 12.dp),
                                horizontalArrangement = Arrangement.spacedBy(8.dp),
                                verticalAlignment = Alignment.CenterVertically
                        ) {
                                modifierKeys.forEach { (vk, label, cd) ->
                                        val state = modifierStates[vk] ?: ModifierState.OFF
                                        AssistChip(
                                                onClick = {
                                                        view.performHapticFeedback(
                                                                HapticFeedbackConstants.VIRTUAL_KEY
                                                        )
                                                        onCycleModifier(vk)
                                                },
                                                label = {
                                                        Text(
                                                                label,
                                                                fontWeight =
                                                                        if (state ==
                                                                                        ModifierState
                                                                                                .LOCKED
                                                                        )
                                                                                FontWeight.Bold
                                                                        else null
                                                        )
                                                },
                                                leadingIcon =
                                                        if (state == ModifierState.LOCKED) {
                                                                {
                                                                        Icon(
                                                                                Icons.Default.Lock,
                                                                                contentDescription =
                                                                                        null,
                                                                                modifier =
                                                                                        Modifier.size(
                                                                                                14.dp
                                                                                        )
                                                                        )
                                                                }
                                                        } else null,
                                                modifier =
                                                        Modifier.semantics {
                                                                contentDescription = cd
                                                        },
                                                colors =
                                                        when (state) {
                                                                ModifierState.OFF ->
                                                                        AssistChipDefaults
                                                                                .assistChipColors(
                                                                                        containerColor =
                                                                                                MaterialTheme
                                                                                                        .colorScheme
                                                                                                        .secondaryContainer,
                                                                                        labelColor =
                                                                                                MaterialTheme
                                                                                                        .colorScheme
                                                                                                        .onSecondaryContainer
                                                                                )
                                                                ModifierState.ARMED ->
                                                                        AssistChipDefaults
                                                                                .assistChipColors(
                                                                                        containerColor =
                                                                                                MaterialTheme
                                                                                                        .colorScheme
                                                                                                        .primaryContainer,
                                                                                        labelColor =
                                                                                                MaterialTheme
                                                                                                        .colorScheme
                                                                                                        .onPrimaryContainer,
                                                                                        leadingIconContentColor =
                                                                                                MaterialTheme
                                                                                                        .colorScheme
                                                                                                        .onPrimaryContainer
                                                                                )
                                                                ModifierState.LOCKED ->
                                                                        AssistChipDefaults
                                                                                .assistChipColors(
                                                                                        containerColor =
                                                                                                MaterialTheme
                                                                                                        .colorScheme
                                                                                                        .primary,
                                                                                        labelColor =
                                                                                                MaterialTheme
                                                                                                        .colorScheme
                                                                                                        .onPrimary,
                                                                                        leadingIconContentColor =
                                                                                                MaterialTheme
                                                                                                        .colorScheme
                                                                                                        .onPrimary
                                                                                )
                                                        }
                                        )
                                }
                                utilKeys.forEach { (label, cd, vk) ->
                                        AssistChip(
                                                onClick = {
                                                        view.performHapticFeedback(
                                                                HapticFeedbackConstants.VIRTUAL_KEY
                                                        )
                                                        onSendKeyPress(vk)
                                                },
                                                label = { Text(label) },
                                                modifier =
                                                        Modifier.semantics {
                                                                contentDescription = cd
                                                        },
                                                colors =
                                                        AssistChipDefaults.assistChipColors(
                                                                containerColor =
                                                                        MaterialTheme.colorScheme
                                                                                .secondaryContainer,
                                                                labelColor =
                                                                        MaterialTheme.colorScheme
                                                                                .onSecondaryContainer
                                                        )
                                        )
                                }
                                // Expand chevron — reveals the F-key/nav-key grid below
                                // (RemEx-bct remnant).
                                RemexTooltip(stringResource(R.string.cd_more_keys)) {
                                        IconButton(onClick = onToggleExtraKeys) {
                                                Icon(
                                                        if (extraKeysExpanded)
                                                                Icons.Default.KeyboardArrowUp
                                                        else Icons.Default.KeyboardArrowDown,
                                                        contentDescription =
                                                                stringResource(R.string.cd_more_keys)
                                                )
                                        }
                                }
                        }
                        if (extraKeysExpanded) {
                                ExtraKeysGrid(onSendKeyPress = onSendKeyPress, view = view)
                        }
                }
        }
}

/**
 * Detects a pure single-character insertion between [oldText] and [newText] — i.e. exactly one
 * character was typed with no surrounding deletion or replacement, as in a normal keypress on the
 * soft keyboard. Used to decide whether a keystroke should be routed as a PC-key chord (Ctrl+C,
 * Ctrl+V, ...) instead of typed text, when a modifier is currently armed or locked (RemEx-yi8o).
 * Returns null for multi-character insertions, deletions, or replacements.
 */
private fun singleCharacterInsertion(oldText: String, newText: String): Char? {
        if (newText.length != oldText.length + 1) return null
        var prefixLength = 0
        while (prefixLength < oldText.length && oldText[prefixLength] == newText[prefixLength]) {
                prefixLength++
        }
        var suffixLength = 0
        val maxSuffix = oldText.length - prefixLength
        while (suffixLength < maxSuffix &&
                        oldText[oldText.length - 1 - suffixLength] ==
                                newText[newText.length - 1 - suffixLength]
        ) {
                suffixLength++
        }
        if (prefixLength + suffixLength != oldText.length) return null // more than a pure insert
        return newText[prefixLength]
}

/**
 * Maps a single ASCII letter or digit to the virtual-key code sent for a PC-key chord (A-Z/a-z ->
 * 0x41-0x5A uppercased, 0-9 -> 0x30-0x39). Returns null for anything else (RemEx-yi8o).
 */
private fun asciiAlphanumericVirtualKeyCode(c: Char): Int? =
        when {
                c in '0'..'9' -> c.code
                c in 'A'..'Z' || c in 'a'..'z' -> c.uppercaseChar().code
                else -> null
        }

@Preview(showBackground = true)
@Composable
private fun RemoteDesktopScreenPreview() {
    RemExTheme {
        RemoteDesktopScreenContent(
            uiState = RemoteDesktopUiState(
                isStreaming = true,
                capabilityState = RemoteDesktopCapabilityState(supportsRemoteDesktop = true),
                isFullscreen = false
            ),
            hostCursor = kotlinx.coroutines.flow.emptyFlow(),
            currentBitmap = null,
            config = RemoteDesktopConfigState(quality = 70, targetFps = 60),
            onSetFullscreen = {},
            onStartStreaming = {},
            onStopStreaming = {},
            onSendText = {},
            onSendKeyPress = {},
            onSendMouseDown = { _, _, _ -> },
            onSendMouseClick = {},
            onSendMouseUp = {},
            onSendMouseMove = { _, _ -> },
            onSendMouseAbsolute = { _, _ -> },
            onSendMouseAbsoluteClick = { _, _, _ -> },
            onSendMouseScroll = { _, _ -> },
            onUpdateQuality = {},
            onUpdateTargetFps = {},
            onUpdateDirectTouch = {},
            onUpdatePointerSpeed = {},
            onUpdateScrollSensitivity = { _, _ -> },
            windowResults = emptyList(),
            windowActionError = null,
            onQueryWindows = {},
            onActivateWindow = {},
            onRaiseWindow = {},
            onMinimizeWindow = {},
            onCloseWindow = {},
            onResizeWindow = { _, _, _ -> },
            onMoveWindowToDesktop = { _, _ -> },
            getHostScreenSize = { Pair(1920, 1080) },
            frameTick = androidx.compose.runtime.mutableLongStateOf(0L),
            fps = 60f,
            showFpsOverlay = true,
            onToggleFpsOverlay = {}
        )
    }
}

/**
 * Shared colors for a toggle-style [FilledTonalIconButton] in the fullscreen overlay row
 * (RemEx-klq) — dedupes the three identical active/inactive color blocks the FPS/Keyboard/PC-keys
 * toggles previously each declared inline.
 */
@Composable
private fun toggleIconButtonColors(active: Boolean): IconButtonColors =
        IconButtonDefaults.filledTonalIconButtonColors(
                containerColor =
                        if (active) MaterialTheme.colorScheme.primaryContainer
                        else MaterialTheme.colorScheme.surfaceContainerHighest,
                contentColor =
                        if (active) MaterialTheme.colorScheme.onPrimaryContainer
                        else MaterialTheme.colorScheme.onSurfaceVariant
        )

/**
 * F-key/nav-key grid revealed by the PC-keys bar's expand chevron (RemEx-bct remnant): F1-F12 plus
 * Home/End/PgUp/PgDn/Insert. Plain (non-latching) keys routed through the same [onSendKeyPress]
 * path `utilKeys` already uses in the compact bar, so an armed/locked modifier still wraps these
 * (e.g. Ctrl+Home) for free via the existing chord logic — no new wiring needed here.
 *
 * F-key labels are literal (never localized by any OS or keyboard vendor); the five nav keys use
 * real per-locale [stringResource] content descriptions.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun ExtraKeysGrid(onSendKeyPress: (Int) -> Unit, view: android.view.View) {
        val extraKeys =
                (1..12).map { n -> Triple("F$n", "F$n", 0x6F + n) } +
                        listOf(
                                Triple(
                                        "Home",
                                        stringResource(R.string.cd_key_home),
                                        0x24
                                ),
                                Triple("End", stringResource(R.string.cd_key_end), 0x23),
                                Triple(
                                        "PgUp",
                                        stringResource(R.string.cd_key_pageup),
                                        0x21
                                ),
                                Triple(
                                        "PgDn",
                                        stringResource(R.string.cd_key_pagedown),
                                        0x22
                                ),
                                Triple(
                                        "Ins",
                                        stringResource(R.string.cd_key_insert),
                                        0x2D
                                ),
                        )
        FlowRow(
                modifier =
                        Modifier.fillMaxWidth()
                                .background(
                                        MaterialTheme.colorScheme.surfaceVariant.copy(
                                                alpha = 0.95f
                                        )
                                )
                                .navigationBarsPadding()
                                .padding(vertical = 6.dp, horizontal = 12.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
                extraKeys.forEach { (label, cd, vk) ->
                        AssistChip(
                                onClick = {
                                        view.performHapticFeedback(
                                                HapticFeedbackConstants.VIRTUAL_KEY
                                        )
                                        onSendKeyPress(vk)
                                },
                                label = { Text(label) },
                                modifier = Modifier.semantics { contentDescription = cd },
                                colors =
                                        AssistChipDefaults.assistChipColors(
                                                containerColor =
                                                        MaterialTheme.colorScheme
                                                                .secondaryContainer,
                                                labelColor =
                                                        MaterialTheme.colorScheme
                                                                .onSecondaryContainer
                                        )
                        )
                }
        }
}
