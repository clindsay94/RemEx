package com.clindsay94.remex.ui

import androidx.compose.ui.unit.dp
import com.clindsay94.remex.ui.components.floatingChromeBottomPadding
import com.clindsay94.remex.ui.screens.LauncherLabelLayout
import com.clindsay94.remex.ui.screens.fpsPillTopPadding
import com.clindsay94.remex.ui.screens.launcherLabelDisplayText
import com.clindsay94.remex.ui.screens.launcherLabelFontSizes
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

    // Stand-in renderer: every character is as wide as the font size, so a tile `chars * 12f`
    // wide holds `chars` characters at 12sp and a few more at the smaller steps.
    private val sizes = launcherLabelFontSizes(startSp = 12f, floorSp = 11f)

    private fun segmentFits(tileWidth: Float): (String, Float) -> Boolean =
        { segment, size -> segment.length * size <= tileWidth }

    /** Greedy wrap that breaks only at whitespace and at the inserted zero-width spaces. */
    private fun greedyLines(tileWidth: Float): (String, Float) -> Int = { text, size ->
        var lines = 1
        var used = 0f
        for (m in Regex("([^\\s​]+)([\\s​]*)").findAll(text)) {
            val word = m.groupValues[1].length * size
            val gap = m.groupValues[2].count { it.isWhitespace() } * size
            if (used > 0f && used + word > tileWidth) {
                lines++
                used = 0f
            }
            used += word + gap
        }
        lines
    }

    private fun layoutFor(name: String, tileChars: Int): LauncherLabelLayout {
        val width = tileChars * 12f
        return launcherLabelLayout(
            launcherLabelDisplayText(name),
            sizes,
            segmentFits(width),
            greedyLines(width)
        )
    }

    @Test
    fun `font sizes step down to exactly the labelSmall floor`() {
        assertEquals(listOf(12f, 11.5f, 11f), sizes)
        assertEquals(listOf(11f), launcherLabelFontSizes(11f, 11f))
        assertEquals(listOf(10f), launcherLabelFontSizes(10f, 11f))
    }

    @Test
    fun `separators and camelCase humps become invisible break points`() {
        assertEquals(
            "BLEACH_​Rebirth_​of_​Souls",
            launcherLabelDisplayText("BLEACH_Rebirth_of_Souls")
        )
        assertEquals("GTA5_​Enhanced", launcherLabelDisplayText("GTA5_Enhanced"))
        assertEquals("Sparking​ZERO", launcherLabelDisplayText("SparkingZERO"))
        assertEquals("v1.​2-​beta", launcherLabelDisplayText("v1.2-beta"))
        // No lowercase-then-uppercase pair and no separator: nothing to add.
        assertEquals("BCUninstaller", launcherLabelDisplayText("BCUninstaller"))
        // A trailing separator or one already followed by a space gets no break point.
        assertEquals("Setup_", launcherLabelDisplayText("Setup_"))
        assertEquals("Naruto Ultimate Ninja Storm Connections",
            launcherLabelDisplayText("Naruto Ultimate Ninja Storm Connections"))
    }

    @Test
    fun `only the displayed text changes, never the name itself`() {
        for (name in listOf("BLEACH_Rebirth_of_Souls", "GTA5_Enhanced", "SparkingZERO", "v1.2-beta")) {
            assertEquals(name, launcherLabelDisplayText(name).replace("​", ""))
        }
    }

    @Test
    fun `the names from the screenshots fit a normal tile at full size`() {
        val full = LauncherLabelLayout(fontSizeSp = 12f, maxLines = 2, softWrap = true)
        // Used to ellipsise as "BLEACH_R…": now wraps after "Rebirth_" with no mid-word break.
        assertEquals(full, layoutFor("BLEACH_Rebirth_of_Souls", tileChars = 15))
        assertEquals(full, layoutFor("GTA5_Enhanced", tileChars = 15))
        assertEquals(full, layoutFor("BCUninstaller", tileChars = 15))
        assertEquals(full, layoutFor("SparkingZERO", tileChars = 15))
    }

    @Test
    fun `a name too long for two lines even at the floor keeps two lines and ellipsises the second`() {
        // Used to be one line: "Naruto Ultimate N…".
        assertEquals(
            LauncherLabelLayout(fontSizeSp = 11f, maxLines = 2, softWrap = true),
            layoutFor("Naruto Ultimate Ninja Storm Connections", tileChars = 15)
        )
    }

    @Test
    fun `the inserted break points let joined names wrap on a narrow tile`() {
        val full = LauncherLabelLayout(fontSizeSp = 12f, maxLines = 2, softWrap = true)
        assertEquals(full, layoutFor("SparkingZERO", tileChars = 10))
        assertEquals(full, layoutFor("GTA5_Enhanced", tileChars = 10))
    }

    @Test
    fun `a segment slightly too wide steps the font down before anything ellipsises`() {
        // 13 characters: 156 wide at 12sp, 149.5 at 11.5sp, 143 at the 11sp floor.
        assertEquals(
            LauncherLabelLayout(fontSizeSp = 11f, maxLines = 2, softWrap = true),
            layoutFor("BCUninstaller", tileChars = 12)
        )
    }

    @Test
    fun `a segment too wide even at the floor goes on one ellipsised line, never broken mid-word`() {
        assertEquals(
            LauncherLabelLayout(fontSizeSp = 11f, maxLines = 1, softWrap = false),
            layoutFor("BCUninstaller", tileChars = 10)
        )
    }

    @Test
    fun `blank names and runs of whitespace do not count as segments`() {
        val full = LauncherLabelLayout(fontSizeSp = 12f, maxLines = 2, softWrap = true)
        assertEquals(full, layoutFor("", tileChars = 1))
        assertEquals(full, layoutFor("  Notepad   Plus  ", tileChars = 7))
    }

    @Test
    fun `the segment measurer only ever sees single segments`() {
        val seen = mutableListOf<String>()
        launcherLabelLayout(
            launcherLabelDisplayText("Remote Desktop_Connection"),
            listOf(12f),
            { segment, _ -> seen += segment; true },
            { _, _ -> 1 }
        )
        assertEquals(listOf("Remote", "Desktop_", "Connection"), seen)
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
