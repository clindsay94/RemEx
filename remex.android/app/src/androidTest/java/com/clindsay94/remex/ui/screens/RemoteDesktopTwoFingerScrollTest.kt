package com.clindsay94.remex.ui.screens

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onRoot
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.unit.dp
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.clindsay94.remex.ui.theme.RemExTheme
import java.util.concurrent.CopyOnWriteArrayList
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Two fingers dragged together on the remote-desktop surface must reach onSendMouseScroll
 * (RemEx-pp4cm.10). Drives the real gesture state machine with synthetic multi-touch.
 */
@RunWith(AndroidJUnit4::class)
class RemoteDesktopTwoFingerScrollTest {
    @get:Rule val composeTestRule = createComposeRule()

    private val scrolls = CopyOnWriteArrayList<Pair<Int, Int>>()

    private fun setScreen(fullscreen: Boolean, wDp: Int, hDp: Int, metaReady: Boolean) {
        composeTestRule.setContent {
            RemExTheme {
                Box(Modifier.size(wDp.dp, hDp.dp)) {
                    RemoteDesktopScreenContent(
                        uiState = RemoteDesktopUiState(
                            isStreaming = true,
                            capabilityState = RemoteDesktopCapabilityState(supportsRemoteDesktop = true),
                            isFullscreen = fullscreen,
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
                        onSendMouseScroll = { x, y -> scrolls.add(x to y) },
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
                        frameTick = remember { mutableLongStateOf(0L) },
                        fps = 60f,
                        showFpsOverlay = false,
                        onToggleFpsOverlay = {},
                        streamPixelWidth = 1920,
                        streamPixelHeight = 1080,
                        desktopMetaReady = metaReady,
                    )
                }
            }
        }
        composeTestRule.waitForIdle()
    }

    /** Two fingers 120 px apart dragged by [dy] per step, paced at real time like a hand, with a little span jitter. */
    private fun twoFingerDrag(dy: Float, steps: Int = 40, jitter: Boolean = true) {
        var cx = 0f
        var cy = 0f
        composeTestRule.onRoot().performTouchInput {
            cx = centerX
            cy = centerY
            down(0, Offset(cx - 60f, cy))
            down(1, Offset(cx + 60f, cy))
        }
        for (i in 1..steps) {
            Thread.sleep(8)
            val wob = if (jitter) (if (i % 2 == 0) 1.5f else -1.5f) else 0f
            composeTestRule.onRoot().performTouchInput {
                updatePointerTo(0, Offset(cx - 60f - wob, cy + dy * i))
                updatePointerTo(1, Offset(cx + 60f + wob, cy + dy * i))
                move(8)
            }
        }
        composeTestRule.onRoot().performTouchInput {
            up(0)
            up(1)
        }
        composeTestRule.waitForIdle()
    }

    private fun report(name: String) {
        val sumY = scrolls.sumOf { it.second }
        android.util.Log.i("RD2F", "$name sends=${scrolls.size} sumY=$sumY first=${scrolls.firstOrNull()}")
    }

    @Test
    fun landscape_metaReady_sendsScroll() {
        setScreen(fullscreen = false, wDp = 900, hDp = 360, metaReady = true)
        twoFingerDrag(-6f)
        report("landscape")
        assertTrue("landscape: no scroll sent", scrolls.isNotEmpty())
    }

    @Test
    fun landscape_fullscreen_sendsScroll() {
        setScreen(fullscreen = true, wDp = 900, hDp = 360, metaReady = true)
        twoFingerDrag(-6f)
        report("landscape-fullscreen")
        assertTrue("landscape fullscreen: no scroll sent", scrolls.isNotEmpty())
    }

    @Test
    fun unzoomedPortrait_sendsScroll() {
        // Before the host's real screen size is known the view is not fitted, so it is at 1x.
        setScreen(fullscreen = false, wDp = 400, hDp = 800, metaReady = false)
        twoFingerDrag(-6f)
        report("portrait-unfitted")
        assertTrue("unzoomed portrait: no scroll sent", scrolls.isNotEmpty())
    }
}
