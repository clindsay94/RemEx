package com.clindsay94.remex

import com.clindsay94.remex.diagnostics.DiagnosticsScrubber
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins what may and may not leave the device in a diagnostics bundle (RemEx-nwnn).
 *
 * BOTH DIRECTIONS ARE LOAD-BEARING. A scrubber that misses a reconnect secret mails a credential to
 * whoever the user happened to share with. A scrubber that eats ordinary log text produces a bundle
 * nobody can debug from, which quietly defeats the feature instead of the security. Most of these
 * tests are the second kind, because that is the failure nobody notices.
 */
class DiagnosticsScrubberTest {

    // A real-shaped base64 SHA-256: 43 payload characters plus padding.
    private val spkiHash = "n8Kq2LxV9dR4tYbF7mJ3wZcA1sQeH6uP0iO5gT8kX2M="

    @Test
    fun `the reconnect secret never survives`() {
        // THE ONE THAT MATTERS MOST. The SPKI hash identifies a host; the reconnect secret ANSWERS
        // the host's proof-of-possession challenge, so it is a credential.
        val line = "PAIR-1: stored reconnect secret $spkiHash for host"

        val scrubbed = DiagnosticsScrubber.scrub(line)

        assertFalse("the secret is still in the bundle", scrubbed.contains(spkiHash))
        assertTrue(scrubbed.contains(DiagnosticsScrubber.REDACTED))
    }

    @Test
    fun `a truncated key is still redacted`() {
        // Log lines abbreviate hashes for readability. A secret is not less of a secret for being
        // clipped, which is why the threshold sits below a full base64 SHA-256 rather than at it.
        val clipped = spkiHash.take(41)

        assertFalse(DiagnosticsScrubber.scrub("pin prefix $clipped").contains(clipped))
    }

    @Test
    fun `ordinary log prose is left completely alone`() {
        // The negative case that keeps the feature useful. None of this is a key, a path, or an
        // address, and all of it is what someone reading the bundle actually needs.
        val line = "RemexManager: Discovered host is verified and trusted; codec negotiated h264_nvenc"

        assertEquals(line, DiagnosticsScrubber.scrub(line))
    }

    @Test
    fun `version numbers are not mistaken for addresses`() {
        // The trap in an IPv4 rule: three-part version strings look almost like a dotted quad. The
        // pattern requires FOUR octets precisely so these survive - and they are among the most
        // useful lines in the bundle.
        val line = "app 2.4.0 (36) on Android API 34, host 2.4.0 platform Windows 10.0.19041"

        assertEquals(line, DiagnosticsScrubber.scrub(line))
    }

    @Test
    fun `an ip address is removed by default and kept when the user opts in`() {
        val line = "Connecting to 192.168.1.50:5005"

        assertFalse(DiagnosticsScrubber.scrub(line).contains("192.168.1.50"))
        assertTrue(
            "the toggle exists because a network bug is unreadable without addresses",
            DiagnosticsScrubber.scrub(line, includeNetworkInfo = true).contains("192.168.1.50:5005")
        )
    }

    @Test
    fun `opting in to network info still does not release keys or paths`() {
        // The toggle is about ADDRESSES only. A user who wants their IP included has not agreed to
        // mail their reconnect secret, and nothing in the UI would tell them they had.
        val line = "secret $spkiHash sent to 10.0.0.7 for C:\\Users\\Connor\\Documents\\tax-2025.pdf"

        val scrubbed = DiagnosticsScrubber.scrub(line, includeNetworkInfo = true)

        assertFalse(scrubbed.contains(spkiHash))
        assertFalse(scrubbed.contains("Connor"))
        assertTrue(scrubbed.contains("10.0.0.7"))
    }

    @Test
    fun `a windows path is reduced to its file name`() {
        // Basename only: the transfer that failed is the diagnostic fact; the folder it sat in says
        // more about the person than the bug.
        val scrubbed = DiagnosticsScrubber.scrub("upload failed C:\\Users\\Connor\\Documents\\tax-2025.pdf")

        assertTrue(scrubbed.contains("tax-2025.pdf"))
        assertFalse(scrubbed.contains("Connor"))
        assertFalse(scrubbed.contains("Documents"))
    }

    @Test
    fun `a posix path is reduced to its file name`() {
        val scrubbed = DiagnosticsScrubber.scrub("wrote /storage/emulated/0/Download/holiday.jpg")

        assertTrue(scrubbed.contains("holiday.jpg"))
        assertFalse(scrubbed.contains("emulated"))
    }

    @Test
    fun `a key is not salvaged into a path`() {
        // Order matters: base64 contains slashes, so scrubbing paths first would reduce a secret to
        // its tail and leave that tail in the bundle looking like a filename.
        val slashy = "AbCd+efGh/ijKlMnOpQrStUvWxYz0123456789/AbCdEfGh="

        val scrubbed = DiagnosticsScrubber.scrub("token $slashy")

        assertFalse(scrubbed.contains("AbCdEfGh"))
        assertEquals("token ${DiagnosticsScrubber.REDACTED}", scrubbed)
    }

    @Test
    fun `an ipv6 address is removed by default`() {
        val scrubbed = DiagnosticsScrubber.scrub("bound to fe80::1c2d:3e4f:5a6b:7c8d")

        assertFalse(scrubbed.contains("fe80::1c2d"))
    }

    @Test
    fun `every line of a multi-line bundle is scrubbed`() {
        // Bundles are hundreds of lines; a rule that only fired on the first would be worse than
        // useless, since the preview would look convincingly clean.
        val bundle = """
            line one $spkiHash
            line two 192.168.1.50
            line three C:\Users\Connor\a.txt
        """.trimIndent()

        val scrubbed = DiagnosticsScrubber.scrub(bundle)

        assertFalse(scrubbed.contains(spkiHash))
        assertFalse(scrubbed.contains("192.168.1.50"))
        assertFalse(scrubbed.contains("Connor"))
        assertTrue(scrubbed.contains("a.txt"))
    }

    @Test
    fun `a logcat timestamp is not mistaken for an ipv6 address`() {
        // THE DEFECT REVIEW CAUGHT, now pinned. Digits are hex digits, so a colon-counting IPv6
        // pattern matches any HH:MM:SS - and the collection step this scrubber exists for is
        // `logcat -d`, whose every line starts with a time of day. The first version redacted the
        // timestamp on essentially the whole bundle, by default, while looking protective.
        val line = "01-02 03:04:05.678  1234  1234 D RemexManager: Discovered host is verified"

        assertEquals(line, DiagnosticsScrubber.scrub(line))
    }

    @Test
    fun `durations and colon-separated triples survive`() {
        // Same root cause, other shapes it ate.
        assertEquals("elapsed 01:03:45", DiagnosticsScrubber.scrub("elapsed 01:03:45"))
        assertEquals("bitrate:8000:kbps", DiagnosticsScrubber.scrub("bitrate:8000:kbps"))
    }

    @Test
    fun `a mac address survives`() {
        // The host now reports its MAC for Wake-on-LAN (RemEx-izuj), so it appears in these logs.
        // Six two-hex groups is neither an eight-group address nor `::`-compressed, so it is kept -
        // and it is diagnostic rather than personal.
        val line = "host MAC 9C:6B:00:9B:1B:D2 adapter Ethernet"

        assertEquals(line, DiagnosticsScrubber.scrub(line))
    }

    @Test
    fun `urls are not reduced to a basename`() {
        // THE SECOND DEFECT REVIEW CAUGHT. The path rule had no notion of a scheme, so it treated a
        // URL path as a filesystem path: wss://desktop-pc:5005/ws/desktop became
        // wss://desktop-pc:5005desktop - this repo's own endpoint, mangled in exactly the
        // connection-failure lines a bundle exists to carry.
        val wss = "connecting wss://desktop-pc:5005/ws/desktop"
        val https = "see https://github.com/clindsay94/remex/issues/12"

        assertEquals(wss, DiagnosticsScrubber.scrub(wss, includeNetworkInfo = true))
        assertEquals(https, DiagnosticsScrubber.scrub(https, includeNetworkInfo = true))
    }

    @Test
    fun `a real ipv6 address is still removed`() {
        // The counterpart: tightening the pattern must not have stopped it working, in either form.
        val compressed = DiagnosticsScrubber.scrub("bound to fe80::1c2d:3e4f:5a6b:7c8d")
        val expanded = DiagnosticsScrubber.scrub("bound to 2001:0db8:0000:0000:0000:ff00:0042:8329")

        assertFalse(compressed.contains("fe80::1c2d"))
        assertFalse(expanded.contains("2001:0db8"))
    }
}
