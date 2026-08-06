package com.clindsay94.remex

import com.clindsay94.remex.diagnostics.DiagnosticsScrubber
import com.clindsay94.remex.diagnostics.LogcatCollector
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the trimming rules for collected log text (RemEx-0iww).
 *
 * The headline test is [a clipped log never splits a secret]. It is the one failure in this file
 * that cannot be seen by reading the output: a bundle carrying half a reconnect secret looks exactly
 * like a bundle carrying ordinary log text, and [DiagnosticsScrubber] — the layer whose whole job is
 * to stop that — is structurally incapable of catching it, because the rule it enforces is a length
 * rule and clipping is what makes the run too short to match.
 *
 * [LogcatCollector.collect] itself is not tested here: it execs a process and needs a device. The
 * rules that decide what survives are separated from it precisely so this half can be exhaustive.
 */
class LogcatCollectorTest {

    /**
     * A real-shaped base64 SHA-256: 43 payload characters plus padding, the shape of the PAIR-1
     * reconnect secret. [DiagnosticsScrubber] redacts runs of 40 or more, so any cut through this
     * leaves two shorter runs it cannot see.
     */
    private val secret = "n8Kq2LxV9dR4tYbF7mJ3wZcA1sQeH6uP0iO5gT8kX2M="

    private fun logLines(count: Int, from: Int = 1) =
            (from until from + count).map { "08-06 11:40:0$it RemexManager: negotiated codec h264" }

    @Test
    fun `text inside both caps is returned untouched`() {
        val raw = logLines(5).joinToString("\n")
        assertEquals(raw, LogcatCollector.clipToWholeLines(raw, maxLines = 500, maxCharacters = 8192))
    }

    @Test
    fun `an empty log stays empty`() {
        assertEquals("", LogcatCollector.clipToWholeLines("", maxLines = 500, maxCharacters = 8192))
    }

    @Test
    fun `a clipped log never splits a secret`() {
        val raw =
                (logLines(10) + "08-06 11:40:11 PairingHandler: reconnect proof $secret" + logLines(10, from = 20))
                        .joinToString("\n")

        // SWEPT rather than probed at one cap, because a single character budget proves only that
        // one budget was safe. The interesting cuts are the ones that land inside the secret, and
        // sweeping is how the test finds them without the test author having to compute where they
        // are - a computation that would silently stop being right the moment a filler line changed
        // length.
        for (maxCharacters in 120..600) {
            val clipped = LogcatCollector.clipToWholeLines(raw, maxLines = 500, maxCharacters = maxCharacters)
            val scrubbed = DiagnosticsScrubber.scrub(clipped, includeNetworkInfo = false)

            // The property is not "the secret is absent" - a whole secret line that survives is
            // fine, because the scrubber redacts it. The property is that NO FRAGMENT survives,
            // which is exactly what the scrubber cannot enforce for itself.
            for (start in 0..(secret.length - 20)) {
                val fragment = secret.substring(start, start + 20)
                assertFalse(
                        "cap=$maxCharacters leaked a 20-character fragment of the secret: $fragment",
                        scrubbed.contains(fragment)
                )
            }
        }
    }

    @Test
    fun `a single line longer than the cap is dropped whole rather than shortened`() {
        val raw = "08-06 11:40:11 PairingHandler: reconnect proof $secret"
        val clipped = LogcatCollector.clipToWholeLines(raw, maxLines = 500, maxCharacters = 50)

        assertEquals(LogcatCollector.ElidedMarker, clipped)
        assertFalse(clipped.contains(secret.substring(0, 20)))
    }

    @Test
    fun `the newest lines are the ones kept`() {
        val raw = logLines(40).joinToString("\n")
        val clipped = LogcatCollector.clipToWholeLines(raw, maxLines = 5, maxCharacters = 8192)

        // The fault being reported is normally the last thing that happened, so trimming keeps the
        // tail. Keeping the head would be the easier implementation and the useless one.
        assertTrue(clipped.endsWith(raw.lines().last()))
        assertFalse(clipped.contains(raw.lines().first()))
    }

    @Test
    fun `the line cap is honoured and the elision is declared`() {
        val raw = logLines(40).joinToString("\n")
        val clipped = LogcatCollector.clipToWholeLines(raw, maxLines = 5, maxCharacters = 8192)

        assertEquals(5, clipped.lines().size)
        // The marker replaces dropped lines rather than joining the kept ones, so a reader can tell
        // a trimmed log from a short one without the result outgrowing its own cap.
        assertEquals(LogcatCollector.ElidedMarker, clipped.lines().first())
    }

    @Test
    fun `the character cap is honoured`() {
        val raw = logLines(80).joinToString("\n")
        for (maxCharacters in 200..400) {
            val clipped = LogcatCollector.clipToWholeLines(raw, maxLines = 500, maxCharacters = maxCharacters)
            assertTrue(
                    "cap=$maxCharacters produced ${clipped.length} characters",
                    clipped.length <= maxCharacters
            )
        }
    }

    @Test
    fun `nothing is elided when the log already fits`() {
        val raw = logLines(5).joinToString("\n")
        val clipped = LogcatCollector.clipToWholeLines(raw, maxLines = 500, maxCharacters = 8192)
        assertFalse(clipped.contains(LogcatCollector.ElidedMarker))
    }

    @Test
    fun `every kept line survives whole`() {
        val raw = logLines(40).joinToString("\n")
        val original = raw.lines().toSet()
        val clipped = LogcatCollector.clipToWholeLines(raw, maxLines = 500, maxCharacters = 500)

        // Whole-line cutting stated as a total property rather than as one secret's escape: every
        // line in the result is either an untouched original or the marker. A character-count
        // implementation fails this for the one line it lands inside.
        for (line in clipped.lines()) {
            assertTrue(
                    "line was not preserved whole: $line",
                    line == LogcatCollector.ElidedMarker || original.contains(line)
            )
        }
    }
}
