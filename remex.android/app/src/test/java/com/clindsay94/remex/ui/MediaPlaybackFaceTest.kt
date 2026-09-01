package com.clindsay94.remex.ui

import com.clindsay94.remex.data.MediaPlaybackSnapshot
import com.clindsay94.remex.data.MediaPlaybackStatus
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The play/pause button reports the PC's state instead of drawing a fixed triangle (RemEx-xx6xf).
 *
 * THE BUG THIS REPLACES WAS A HARDCODED ICON. `Icons.Default.PlayArrow` was passed unconditionally,
 * so the one control on the screen whose job is to say which way a toggle will go could not — it
 * looked identical whether the PC was playing, paused, or asleep. Nothing failed; there was simply
 * nothing to fail.
 *
 * The parse half is ordinary logic and tested as such. The RENDER half is a source guard, because
 * `remex.android` has no Compose render in unit tests: a regression here is a wrong picture, and a
 * wrong picture throws nothing.
 */
class MediaPlaybackFaceTest {

    @Test
    fun `every wire token maps to its state`() {
        assertEquals(MediaPlaybackStatus.PLAYING, MediaPlaybackStatus.fromToken("playing"))
        assertEquals(MediaPlaybackStatus.PAUSED, MediaPlaybackStatus.fromToken("paused"))
        assertEquals(MediaPlaybackStatus.STOPPED, MediaPlaybackStatus.fromToken("stopped"))
        assertEquals(MediaPlaybackStatus.NONE, MediaPlaybackStatus.fromToken("none"))
    }

    @Test
    fun `an unrecognised token degrades to UNKNOWN rather than to a guess`() {
        // THE WHOLE REASON THE WIRE FORM IS A STRING. A host that learns a new state must degrade an
        // older phone to "say nothing" - which renders as the neutral triangle - rather than to
        // whichever enum member happens to be first. An ordinal on the wire would make that
        // impossible to get right.
        assertEquals(MediaPlaybackStatus.UNKNOWN, MediaPlaybackStatus.fromToken("buffering"))
        assertEquals(MediaPlaybackStatus.UNKNOWN, MediaPlaybackStatus.fromToken(""))
        assertEquals(MediaPlaybackStatus.UNKNOWN, MediaPlaybackStatus.fromToken(null))
    }

    @Test
    fun `a payload parses into a snapshot`() {
        val snapshot =
                MediaPlaybackSnapshot.parse(
                        """{"status":"playing","title":"Sound of Silence","artist":"Simon & Garfunkel"}"""
                )

        assertEquals(MediaPlaybackStatus.PLAYING, snapshot.status)
        assertEquals("Sound of Silence", snapshot.title)
        assertEquals("Simon & Garfunkel", snapshot.artist)
    }

    @Test
    fun `blank metadata becomes null rather than an empty string`() {
        val snapshot = MediaPlaybackSnapshot.parse("""{"status":"paused","title":"","artist":"  "}""")

        assertEquals(MediaPlaybackStatus.PAUSED, snapshot.status)
        assertNull(snapshot.title)
        assertNull(snapshot.artist)
    }

    @Test
    fun `malformed json degrades instead of throwing`() {
        // THIS RUNS ON A JNI CALLBACK. An exception escaping the parse takes down the delivery thread
        // for every other message type as well, so a media reading - the least important thing on
        // that path - would become the reason file transfer and telemetry stopped arriving.
        assertEquals(MediaPlaybackSnapshot.Unknown, MediaPlaybackSnapshot.parse("not json"))
        assertEquals(MediaPlaybackSnapshot.Unknown, MediaPlaybackSnapshot.parse(""))
    }

    @Test
    fun `the default snapshot is UNKNOWN, which is what the row falls back to`() {
        assertEquals(MediaPlaybackStatus.UNKNOWN, MediaPlaybackSnapshot.Unknown.status)
    }

    @Test
    fun `the button draws a pause face only while the PC is playing`() {
        val text = mediaSectionSource()

        // Pause is the face for PLAYING and for nothing else. The convention is that a transport
        // button shows what the next press will DO, and it is the only convention that makes a toggle
        // legible - a pause bar means "playing, press to pause".
        assertTrue(
                "the play/pause button must choose its icon from the playback status",
                text.contains("if (playbackStatus == MediaPlaybackStatus.PLAYING) Icons.Default.Pause")
        )

        // AND THE ICON MUST STILL BE ABLE TO BE THE TRIANGLE. A version that always drew Pause would
        // be just as wrong as the hardcoded PlayArrow this replaced, and would pass an assertion that
        // only looked for the word "Pause".
        assertTrue(
                "an unknown or paused PC must still get the play triangle",
                text.contains("else Icons.Default.PlayArrow")
        )
    }

    @Test
    fun `an unknown state is announced as the generic label, not as Play`() {
        val text = mediaSectionSource()

        // A SCREEN READER MUST NOT BE TOLD SOMETHING THE PHONE DOES NOT KNOW. "Play" for an unknown
        // state is a claim about the user's machine; rc_media_play_pause is what the button announced
        // before this feature existed and is still the only honest thing to say about a toggle whose
        // position is unknown. The icon can fall back silently; the label cannot.
        assertTrue(
                "UNKNOWN must keep the generic play-or-pause label",
                Regex("""MediaPlaybackStatus\.UNKNOWN\s*->\s*R\.string\.rc_media_play_pause""")
                        .containsMatchIn(text)
        )
        assertTrue(
                "PLAYING must announce Pause",
                Regex("""MediaPlaybackStatus\.PLAYING\s*->\s*R\.string\.rc_media_pause""")
                        .containsMatchIn(text)
        )
    }

    /**
     * The composable's source with comments stripped.
     *
     * Stripping matters here as much as in the sibling guards: this file explains at length why the
     * icon is chosen the way it is, naming both `Icons.Default.Pause` and `rc_media_play_pause` in
     * prose. A guard that reads its own explanation as the implementation is worse than no guard.
     */
    private fun mediaSectionSource(): String {
        val relative = "java/com/clindsay94/remex/ui/components/MediaControlSection.kt"
        val file =
                File("src/main/$relative").takeIf { it.isFile } ?: File("app/src/main/$relative")
        assertTrue("expected to find MediaControlSection at ${file.path}", file.isFile)
        return file.readText()
                .replace(Regex("""/\*.*?\*/""", RegexOption.DOT_MATCHES_ALL), "")
                .replace(Regex("""//.*"""), "")
    }
}
