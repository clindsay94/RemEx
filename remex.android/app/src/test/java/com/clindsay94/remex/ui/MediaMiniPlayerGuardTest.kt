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

        // NONE ("the PC answered, nothing is playing") must stay visible: it is the bar's only
        // route to MediaNowPlayingSheet's volume/transport controls when nothing is playing.
        assertFalse(
                "MediaMiniPlayer must not also gate on NONE - that removes the phone's only path"
                        + " to volume/transport when idle",
                Regex("""playback\.status\s*!=\s*MediaPlaybackStatus\.UNKNOWN\s*&&\s*"""
                                + """\s*playback\.status\s*!=\s*MediaPlaybackStatus\.NONE""")
                        .containsMatchIn(text)
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
    fun `the wavy progress bar's amplitude is gated on PLAYING, not just drawn wavy`() {
        val text = miniPlayerSource()

        // M3 Expressive spec 4.3: the wave must flatten while paused. A regression here (dropping
        // the amplitude argument, or gating on something other than PLAYING) would compile fine and
        // render fine — the bar would just never flatten, which no unit test can see since
        // `remex.android` has no Compose render in unit tests.
        assertTrue(
                "RemexLinearWavyProgress must receive an amplitude argument",
                text.contains("RemexLinearWavyProgress(")
        )
        assertTrue(
                "the amplitude lambda must gate on playback.status == PLAYING and fall back to 0f"
                        + " otherwise",
                Regex("""amplitude\s*=\s*\{[^}]*playback\.status\s*==\s*MediaPlaybackStatus\.PLAYING"""
                                + """[\s\S]*?else[\s\S]*?0f""")
                        .containsMatchIn(text)
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
    fun `RemoteControlScreen's miniPlayerShown agrees with MediaMiniPlayer's own gate`() {
        val text = remoteControlScreenSource()

        // Same reasoning as the mini-player's own gate above: both call sites must agree, or the
        // toolbar/grid inset raises for a bar that isn't actually drawn (or vice versa).
        assertTrue(
                "miniPlayerShown must exclude UNKNOWN",
                text.contains("uiState.playback.status != MediaPlaybackStatus.UNKNOWN")
        )
        assertFalse(
                "miniPlayerShown must not also exclude NONE - that removes the phone's only path"
                        + " to volume/transport when idle",
                Regex("""uiState\.playback\.status\s*!=\s*MediaPlaybackStatus\.UNKNOWN\s*&&\s*"""
                                + """\s*uiState\.playback\.status\s*!=\s*MediaPlaybackStatus\.NONE""")
                        .containsMatchIn(text)
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

    @Test
    fun `the sheet's progress row is a seek control, and the bar never grows one`() {
        val sheetText = nowPlayingSheetSource()

        assertTrue(
                "MediaNowPlayingSheet must use a Slider for the seek control",
                sheetText.contains("Slider(")
        )
        assertTrue(
                "the Slider must commit the seek on release, not on every drag frame",
                sheetText.contains("onValueChangeFinished")
        )

        val barText = miniPlayerSource()
        assertFalse(
                "the mini-player BAR must stay a display-only indicator - dragging it is the sheet's job",
                barText.contains("Slider(")
        )
    }

    @Test
    fun `the seek slider is disabled when the host says the session cannot seek`() {
        // A SESSION THAT WILL NOT SEEK MUST NOT BE OFFERED A DRAGGABLE BAR. Apple Music accepts the
        // seek through SMTC, reports success and never moves, so the phone's optimistic jump was
        // reverted 2.5 s later every time. The host now says canSeek up front and the slider has to
        // spend it - and only a source guard can check that, because there is no Compose render
        // here to observe a disabled thumb.
        val sheetText = nowPlayingSheetSource()

        val sliderAt = sheetText.indexOf("Slider(")
        assertTrue("MediaNowPlayingSheet must still have a Slider to gate", sliderAt >= 0)

        // Bounded at MediaControlSection so this reads the SLIDER's enabled expression and cannot
        // accidentally be satisfied by the transport buttons' one further down the file.
        val controlsAt = sheetText.indexOf("MediaControlSection(", sliderAt)
        assertTrue("MediaControlSection should follow the Slider", controlsAt > sliderAt)

        val enabledLine =
                sheetText
                        .substring(sliderAt, controlsAt)
                        .lineSequence()
                        .firstOrNull { it.contains("enabled =") }
        assertTrue("the Slider must declare an enabled expression", enabledLine != null)
        assertTrue(
                "the Slider's enabled expression must gate on playback.canSeek, not just on the " +
                        "connection: $enabledLine",
                enabledLine!!.contains("playback.canSeek")
        )
    }

    @Test
    fun `the sheet no longer draws now-playing text inside the shaped controls card`() {
        val text = nowPlayingSheetSource()

        val callAt = text.lastIndexOf("MediaControlSection(")
        assertTrue("expected a MediaControlSection( call in the sheet", callAt >= 0)
        val call = text.substring(callAt)

        // The controls card's shape is the user's Remote Commands shape - a "Gem" hexagon today,
        // any expressive shape in general - whose slanted edges clipped the title/artist pinned to
        // its top edge and the volume hint pinned to its bottom (RemEx-vtorl). The sheet now draws
        // that text itself and tells the card not to draw its own copy.
        assertTrue(
                "the sheet's MediaControlSection call must pass showNowPlaying = false so the card"
                        + " does not draw its own clipped title/artist line",
                call.contains("showNowPlaying = false")
        )
        assertTrue(
                "the sheet's MediaControlSection call must use the fixed extraLarge shape, not the"
                        + " user's expressive Remote Commands shape, so the volume hint stops"
                        + " clipping",
                call.contains("shape = MaterialTheme.shapes.extraLarge")
        )
    }

    @Test
    fun `the sheet renders its own title and artist, full width and outside the shaped card`() {
        val text = nowPlayingSheetSource()

        // The title must wrap to a second line rather than clip the way the shaped card's top edge
        // used to, and the artist must scroll rather than be cut by the card's bottom edge.
        assertTrue(
                "the sheet's title Text must cap at 2 lines instead of clipping inside the card",
                text.contains("maxLines = 2")
        )
        assertTrue(
                "the artist line must scroll with an unbounded basicMarquee instead of being cut"
                        + " by the card's shape (the default three passes park it clipped again)",
                text.contains("basicMarquee(iterations = Int.MAX_VALUE)")
        )
    }

    @Test
    fun `MediaControlSection accepts an opt-out for its own now-playing line`() {
        val text = mediaControlSectionSource()

        // The sheet is the one caller that needs this; every other call site keeps the default and
        // gets the line exactly as before.
        assertTrue(
                "MediaControlSection must declare showNowPlaying: Boolean = true so the sheet can"
                        + " render title/artist itself instead",
                text.contains("showNowPlaying: Boolean = true")
        )
        assertTrue(
                "the flag must actually gate NowPlayingLine's call site - a revert back to an"
                        + " unconditional call would restore the clipped line and double-render it"
                        + " in the sheet, and nothing else here would catch that",
                text.contains("if (showNowPlaying) NowPlayingLine")
        )
    }

    private fun miniPlayerSource(): String =
            readSource("java/com/clindsay94/remex/ui/components/MediaMiniPlayer.kt")

    private fun nowPlayingSheetSource(): String =
            readSource("java/com/clindsay94/remex/ui/components/MediaNowPlayingSheet.kt")

    private fun mediaControlSectionSource(): String =
            readSource("java/com/clindsay94/remex/ui/components/MediaControlSection.kt")

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
