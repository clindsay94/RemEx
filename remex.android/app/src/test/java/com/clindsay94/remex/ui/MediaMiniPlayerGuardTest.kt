package com.clindsay94.remex.ui

import java.io.File
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The docked mini-player, the computed toolbar occlusion, and the media card leaving the command
 * grid (RemEx-vtorl.5) — a source guard, like [MediaPlaybackFaceTest], because `remex.android` has
 * no Compose render in unit tests: a regression here is a wrong picture or a card parked behind
 * the toolbar, and neither throws.
 */
class MediaMiniPlayerGuardTest {

    @Test
    fun `the mini-player is hidden for exactly the UNKNOWN reading`() {
        val text = miniPlayerSource()

        // Same rule as NowPlayingLine (RemEx-nmvz6): no reading, no bar. If this ever drifted to
        // checking title/artist instead, a PC that answered with an empty track would wrongly show
        // a bar with nothing on it.
        assertTrue(
                "MediaMiniPlayer must gate its visibility on playback.status != UNKNOWN",
                text.contains("playback.status != MediaPlaybackStatus.UNKNOWN")
        )
    }

    @Test
    fun `the play-pause button is its own target, not the row's`() {
        val text = miniPlayerSource()

        // The row itself opens the sheet; the play/pause button must be a second, independent
        // click target so tapping it does not also open the sheet underneath.
        assertTrue(
                "MediaMiniPlayer must have an IconButton whose onClick is onPlayPause",
                Regex("""IconButton\(\s*onClick\s*=\s*onPlayPause""").containsMatchIn(text)
        )
        assertTrue(
                "the row's own click target must remain onOpen, distinct from onPlayPause",
                text.contains("onClick = onOpen")
        )
    }

    @Test
    fun `the now-playing sheet uses the unified bottom sheet API, not the deprecated one`() {
        val text = nowPlayingSheetSource()

        // material3 1.5.0-alpha20+ unified rememberModalBottomSheetState and
        // rememberStandardBottomSheetState behind rememberBottomSheetState; the old names are
        // deprecated. A regression back to the deprecated call is exactly the kind of thing lint
        // catches too late to matter here - this fails the build first.
        assertTrue(
                "MediaNowPlayingSheet must call rememberBottomSheetState",
                text.contains("rememberBottomSheetState(")
        )
        assertFalse(
                "MediaNowPlayingSheet must not call the deprecated rememberModalBottomSheetState",
                text.contains("rememberModalBottomSheetState(")
        )
    }

    @Test
    fun `the toolbar occlusion is computed, and no use site reads a bare constant`() {
        val text = remoteControlScreenSource()

        // The bug this replaces: a bare 104.dp constant that only accounted for the toolbar's own
        // footprint, so a docked mini-player could sit behind it (or the toolbar could sit behind
        // the mini-player) with nothing ever recomputing the inset.
        assertFalse(
                "the old bare FloatingToolbarOcclusion constant must not be re-declared",
                Regex("""val\s+FloatingToolbarOcclusion\s*=\s*\d+\.dp""").containsMatchIn(text)
        )
        assertTrue(
                "an occlusion must still be computed from the toolbar-only base",
                Regex("""val\s+ToolbarOnlyOcclusion\s*=\s*\d+\.dp""").containsMatchIn(text)
        )
        assertTrue(
                "rememberFloatingToolbarOcclusion must exist and take whether the mini-player shows",
                text.contains("rememberFloatingToolbarOcclusion(miniPlayerShown)")
        )

        // The grid's bottom inset: must be the val this composable derived from
        // rememberFloatingToolbarOcclusion, not a literal dp value.
        assertTrue(
                "the grid must first bind a val to rememberFloatingToolbarOcclusion(...)",
                Regex("""val\s+toolbarOcclusion\s*=\s*rememberFloatingToolbarOcclusion""")
                        .containsMatchIn(text)
        )
        assertTrue(
                "the grid's contentPadding bottom must read that computed value",
                Regex("""bottom\s*=\s*toolbarOcclusion\b""").containsMatchIn(text)
        )

        // The RemEx-tgl1 bring-into-view maths: CommandCard must take the SAME computed value as a
        // parameter (not re-read a module constant), and the grid must pass its toolbarOcclusion
        // through to it - that's what keeps the two call sites from drifting apart.
        assertTrue(
                "CommandCard must accept the occlusion value as a parameter",
                Regex("""toolbarOcclusion:\s*androidx\.compose\.ui\.unit\.Dp""").containsMatchIn(text)
        )
        assertTrue(
                "CommandCard's toPx() line must read its own toolbarOcclusion parameter",
                text.contains("toolbarOcclusion.toPx()")
        )
        assertTrue(
                "the grid's CommandCard call must forward the computed occlusion",
                text.contains("toolbarOcclusion = toolbarOcclusion,")
        )
    }

    @Test
    fun `the media card no longer sits inside the command grid`() {
        val text = remoteControlScreenSource()

        // Scoped to the grid body specifically (between LazyVerticalGrid( and the floating
        // toolbar that follows it): MediaControlSection is still called from
        // MediaNowPlayingSheet.kt, so a whole-file `!contains` would pass for the wrong reason if
        // this file ever imported it back in for some other purpose.
        val gridStart = text.indexOf("LazyVerticalGrid(")
        val toolbarStart = text.indexOf("HorizontalFloatingToolbar(")
        assertTrue("expected to find both the grid and the toolbar", gridStart >= 0 && toolbarStart > gridStart)
        val gridBody = text.substring(gridStart, toolbarStart)

        assertFalse(
                "MediaControlSection must not be called from inside the command grid any more",
                gridBody.contains("MediaControlSection(")
        )
    }

    private fun miniPlayerSource(): String =
            readSource("java/com/clindsay94/remex/ui/components/MediaMiniPlayer.kt")

    private fun nowPlayingSheetSource(): String =
            readSource("java/com/clindsay94/remex/ui/components/MediaNowPlayingSheet.kt")

    private fun remoteControlScreenSource(): String =
            readSource("java/com/clindsay94/remex/ui/screens/RemoteControlScreen.kt")

    /**
     * A source file with comments stripped, so guards that name identifiers in prose (as this
     * file's own explanations do) cannot pass by matching their own commentary instead of the
     * implementation. Mirrors [MediaPlaybackFaceTest]'s `mediaSectionSource`.
     */
    private fun readSource(relative: String): String {
        val file = File("src/main/$relative").takeIf { it.isFile } ?: File("app/src/main/$relative")
        assertTrue("expected to find $relative at ${file.path}", file.isFile)
        return file.readText()
                .replace(Regex("""/\*.*?\*/""", RegexOption.DOT_MATCHES_ALL), "")
                .replace(Regex("""//.*"""), "")
    }
}
