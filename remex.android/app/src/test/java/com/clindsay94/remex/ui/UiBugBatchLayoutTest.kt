package com.clindsay94.remex.ui

import androidx.compose.ui.unit.dp
import com.clindsay94.remex.ui.components.floatingChromeBottomPadding
import com.clindsay94.remex.ui.screens.LauncherLabelLayout
import com.clindsay94.remex.ui.screens.fpsPillTopPadding
import com.clindsay94.remex.ui.screens.launcherLabelLayout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the layout rules behind the Android UI bug batch (RemEx-wqo7a.1): the bottom padding under
 * floating chrome, the launcher label's line-break policy, and where the FPS pill sits.
 */
class UiBugBatchLayoutTest {

    // ── Floating chrome bottom padding ────────────────────────────────────────────

    @Test
    fun `padding includes the nav bar inset the floating element is lifted by`() {
        // Three-button navigation: 48.dp. Leaving it out is exactly the bug.
        val padding = floatingChromeBottomPadding(floatingFootprint = 88.dp, navBarInset = 48.dp)
        assertEquals(88.dp + 48.dp + 16.dp, padding)
    }

    @Test
    fun `gesture navigation still clears the footprint plus the gap`() {
        assertEquals(
            88.dp + 24.dp + 16.dp,
            floatingChromeBottomPadding(floatingFootprint = 88.dp, navBarInset = 24.dp)
        )
    }

    @Test
    fun `padding always exceeds what the floating element covers`() {
        for (inset in listOf(0.dp, 16.dp, 24.dp, 48.dp)) {
            val covered = 104.dp + inset
            assertTrue(floatingChromeBottomPadding(104.dp, inset, gap = 0.dp) >= covered)
        }
    }

    @Test
    fun `negative inputs never shrink the padding below the footprint`() {
        assertEquals(72.dp, floatingChromeBottomPadding(72.dp, navBarInset = (-10).dp, gap = (-4).dp))
    }

    // ── App Launcher label line-break policy ──────────────────────────────────────

    /** A stand-in measurer: a word fits when it is at most [maxChars] characters. */
    private fun fits(maxChars: Int): (String) -> Boolean = { it.length <= maxChars }

    private val wrapAtWords = LauncherLabelLayout(maxLines = 2, softWrap = true)
    private val oneLineEllipsis = LauncherLabelLayout(maxLines = 1, softWrap = false)

    @Test
    fun `single overlong token goes on one ellipsised line instead of breaking mid-word`() {
        // The names from the screenshots.
        assertEquals(oneLineEllipsis, launcherLabelLayout("BCUninstaller", fits(10)))
        assertEquals(oneLineEllipsis, launcherLabelLayout("GTA5_Enhanced", fits(10)))
        assertEquals(oneLineEllipsis, launcherLabelLayout("SparkingZERO", fits(10)))
    }

    @Test
    fun `multi-word name whose words all fit wraps at word boundaries over two lines`() {
        assertEquals(wrapAtWords, launcherLabelLayout("Visual Studio Code", fits(10)))
        assertEquals(wrapAtWords, launcherLabelLayout("Steam", fits(10)))
    }

    @Test
    fun `one overlong word in a multi-word name forces the single-line layout`() {
        assertEquals(oneLineEllipsis, launcherLabelLayout("Grand TheftAutoFive", fits(10)))
    }

    @Test
    fun `runs of whitespace and blank names do not count as words`() {
        assertEquals(wrapAtWords, launcherLabelLayout("  Notepad   Plus  ", fits(7)))
        assertEquals(wrapAtWords, launcherLabelLayout("", fits(1)))
    }

    @Test
    fun `the measurer only ever sees single words`() {
        val seen = mutableListOf<String>()
        launcherLabelLayout("Remote Desktop Connection") { seen += it; true }
        assertEquals(listOf("Remote", "Desktop", "Connection"), seen)
    }

    // ── Remote Desktop FPS pill placement ─────────────────────────────────────────

    @Test
    fun `outside fullscreen the pill keeps its usual inset`() {
        assertEquals(12.dp, fpsPillTopPadding(isFullscreen = false, fullscreenControlsHeight = 0.dp))
        assertEquals(12.dp, fpsPillTopPadding(isFullscreen = false, fullscreenControlsHeight = 48.dp))
    }

    @Test
    fun `in fullscreen the pill sits below the control row, never under it`() {
        val rowHeight = 56.dp
        val rowBottom = 16.dp + rowHeight // the row's edge padding plus its measured height
        assertTrue(fpsPillTopPadding(isFullscreen = true, fullscreenControlsHeight = rowHeight) > rowBottom)
    }

    @Test
    fun `before the control row is measured the pill still clears one button`() {
        assertTrue(fpsPillTopPadding(isFullscreen = true, fullscreenControlsHeight = 0.dp) > 16.dp + 48.dp)
    }
}
