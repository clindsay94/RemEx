package com.clindsay94.remex

import com.clindsay94.remex.diagnostics.DiagnosticsBundle
import com.clindsay94.remex.diagnostics.DiagnosticsShare
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the content-addressed staging name (RemEx-0iww, from the round-1 review of this bead).
 *
 * A fixed file name rewritten in place is the seam through which "what you preview is what leaves"
 * breaks: a mail client that parks the `content://` URI in a draft resolves it again later, by which
 * point a second share can have replaced the bytes behind it. The name therefore has to be a
 * function of the content — and a function of the content ONLY, because the same report shared twice
 * must not accumulate copies, and a report the user cannot re-derive is a report the preview never
 * showed.
 *
 * The rest of [DiagnosticsShare] needs a Context and a FileProvider, so it belongs to the device
 * test. This part does not, which is why it is separated out.
 */
class DiagnosticsShareTest {

    private fun environment(logcat: String = "RemexManager: negotiated codec h264_nvenc") =
            DiagnosticsBundle.Environment(
                    appVersion = "2.4.0",
                    deviceManufacturer = "Samsung",
                    deviceModel = "SM-S938B",
                    androidApiLevel = 37,
                    hostInfoJson = null,
                    connectionPhase = "connected",
                    logcat = logcat,
            )

    @Test
    fun `the same report always stages to the same name`() {
        val bundle = DiagnosticsBundle.build(environment(), includeNetworkInfo = false)

        // Also pins that build() is still deterministic: a clock anywhere inside it would make
        // these two differ, and the preview promise would be gone with them.
        val again = DiagnosticsBundle.build(environment(), includeNetworkInfo = false)
        assertEquals(bundle, again)
        assertEquals(DiagnosticsShare.fileNameFor(bundle), DiagnosticsShare.fileNameFor(again))
    }

    @Test
    fun `flipping the network toggle stages to a different name`() {
        // The concrete failure: share with addresses hidden into a draft, come back, turn the
        // toggle on, share again elsewhere - and the untouched draft resolves to a file carrying
        // addresses its recipient was never shown.
        val environment = environment(logcat = "RemexManager: connecting to 192.168.1.100:5005")
        val withoutNetwork = DiagnosticsBundle.build(environment, includeNetworkInfo = false)
        val withNetwork = DiagnosticsBundle.build(environment, includeNetworkInfo = true)

        assertNotEquals(withoutNetwork, withNetwork)
        assertNotEquals(
                DiagnosticsShare.fileNameFor(withoutNetwork),
                DiagnosticsShare.fileNameFor(withNetwork)
        )
    }

    @Test
    fun `a changed log stages to a different name`() {
        val first = DiagnosticsBundle.build(environment(logcat = "line one"), includeNetworkInfo = false)
        val second = DiagnosticsBundle.build(environment(logcat = "line two"), includeNetworkInfo = false)

        assertNotEquals(DiagnosticsShare.fileNameFor(first), DiagnosticsShare.fileNameFor(second))
    }

    @Test
    fun `the name is a plain file name a mail client can show`() {
        val name = DiagnosticsShare.fileNameFor("anything at all")

        // THE WHOLE SHAPE, not a handful of properties of it. A length bound and a suffix check
        // both pass under the classic sign-extension bug - `"%02x".format` on an Int rather than a
        // Byte renders 0xff as "ffffffff" - so a test written that way asserts less than its own
        // name claims. Pinning the exact form is one line and cannot be satisfied by accident.
        // (Round-2 review of RemEx-0iww.)
        assertTrue(name, name.matches(Regex("""^remex-diagnostics-[0-9a-f]{8}\.txt$""")))
        // No separators and no traversal: this string is joined onto the cache directory, and
        // FileProvider would refuse a path that climbed out of its declared root anyway - loudly,
        // at the moment of sharing, which is the worst time to find out.
        assertTrue(name, name.none { it == '/' || it == '\\' })
        assertTrue(name, !name.contains(".."))
    }
}
