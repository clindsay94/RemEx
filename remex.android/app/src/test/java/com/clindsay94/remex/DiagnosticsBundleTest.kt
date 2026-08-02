package com.clindsay94.remex

import com.clindsay94.remex.diagnostics.DiagnosticsBundle
import com.clindsay94.remex.diagnostics.DiagnosticsScrubber
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins what the shareable bundle contains and, more importantly, what it cannot contain (RemEx-3rjf).
 *
 * [DiagnosticsScrubberTest] proves the redaction rules are right. These tests prove the ASSEMBLY
 * cannot route around them — a correct scrubber that some section never reaches protects nothing.
 */
class DiagnosticsBundleTest {

    // A real-shaped base64 SHA-256: 43 payload characters plus padding. This is the shape of both
    // the SPKI pin and the PAIR-1 reconnect secret, and the secret is the one that matters, because
    // it ANSWERS the host's proof-of-possession challenge.
    private val secret = "n8Kq2LxV9dR4tYbF7mJ3wZcA1sQeH6uP0iO5gT8kX2M="

    private fun env(
            appVersion: String = "2.4.0",
            hostInfoJson: String? = null,
            connectionPhase: String = "connected",
            logcat: String = "RemexManager: negotiated codec h264_nvenc"
    ) =
            DiagnosticsBundle.Environment(
                    appVersion = appVersion,
                    deviceManufacturer = "Samsung",
                    deviceModel = "SM-S938B",
                    androidApiLevel = 37,
                    hostInfoJson = hostInfoJson,
                    connectionPhase = connectionPhase,
                    logcat = logcat
            )

    @Test
    fun `a secret in the log never reaches the bundle`() {
        val bundle =
                DiagnosticsBundle.build(
                        env(logcat = "PAIR-1: stored reconnect secret $secret for host"),
                        includeNetworkInfo = false
                )

        assertFalse("the reconnect secret is in the shareable bundle", bundle.contains(secret))
        assertTrue(bundle.contains(DiagnosticsScrubber.REDACTED))
    }

    @Test
    fun `a secret smuggled through a field that should never hold one is still redacted`() {
        // THE TEST THIS CLASS EXISTS FOR. The log is the section everyone remembers to scrub. This
        // asserts the guarantee is STRUCTURAL rather than per-section: build() scrubs the assembled
        // text once at the end, so a section carrying something it never should cannot bypass it.
        // If someone later adds a section and forgets to redact it, this is the shape of the bug
        // that would otherwise ship, and the single choke point is what prevents it.
        val bundle =
                DiagnosticsBundle.build(
                        env(appVersion = secret, connectionPhase = "connected as $secret"),
                        includeNetworkInfo = false
                )

        assertFalse(bundle.contains(secret))
    }

    @Test
    fun `the network toggle still releases only addresses, never keys`() {
        // Opting in to network info is consent to share an IP. It is not consent to mail the
        // reconnect secret, and nothing in the UI would tell the user it had.
        val bundle =
                DiagnosticsBundle.build(
                        env(logcat = "connecting to 192.168.1.50 with secret $secret"),
                        includeNetworkInfo = true
                )

        assertTrue("the toggle exists because a network bug is unreadable without addresses",
                bundle.contains("192.168.1.50"))
        assertFalse("opting in to addresses is not opting in to credentials", bundle.contains(secret))
    }

    @Test
    fun `addresses are gone by default`() {
        val bundle =
                DiagnosticsBundle.build(
                        env(logcat = "connecting to 192.168.1.50"),
                        includeNetworkInfo = false
                )

        assertFalse(bundle.contains("192.168.1.50"))
    }

    @Test
    fun `the useful facts survive`() {
        // The counterpart that keeps the feature worth having. A bundle scrubbed until it is
        // unreadable defeats the feature instead of the security, which is the failure nobody
        // notices - version strings in particular look almost like dotted quads.
        val bundle = DiagnosticsBundle.build(env(), includeNetworkInfo = false)

        // Whole rendered lines, not bare substrings. `contains("37")` would be satisfied by any
        // stray 37 anywhere in the log, and `contains("connected")` is satisfied by the word
        // "disconnected" - which is the opposite state.
        assertTrue(bundle.contains("version: 2.4.0"))
        assertTrue(bundle.contains("device: Samsung SM-S938B"))
        assertTrue(bundle.contains("android API: 37"))
        assertTrue(bundle.contains("connection: connected"))
        assertTrue(bundle.contains("h264_nvenc"))
    }

    @Test
    fun `a disconnected phase is not mistaken for a connected one`() {
        // The counterpart that makes the assertion above mean something.
        val bundle =
                DiagnosticsBundle.build(
                        env(connectionPhase = "disconnected"), includeNetworkInfo = false)

        assertTrue(bundle.contains("connection: disconnected"))
        assertFalse(bundle.contains("connection: connected"))
    }

    @Test
    fun `a host that has never been heard from says so rather than showing an empty section`() {
        val bundle = DiagnosticsBundle.build(env(hostInfoJson = null), includeNetworkInfo = false)

        assertTrue(bundle.contains("no host_info received"))
    }

    /**
     * A host_info payload using the REAL wire key names.
     *
     * These come from `HostCapabilities` serialized with the camelCase naming policy, not from
     * whatever this file finds convenient. A previous version of this test invented its own fixture
     * keys and therefore asserted only "the code reads the keys the code reads" - it passed while
     * the production code read `appVersion`, `captureMode` and `negotiatedCodec`, none of which
     * have ever existed on the wire, so three of six lines were permanently inert against every
     * real host. A fixture built from the implementation's own assumptions cannot catch that.
     */
    private val realHostInfo =
            """{"version":"2.4.0.0","runtimeMode":"interactive","platform":"Windows",
               "isInteractiveSession":true,"supportsRemoteDesktop":true,
               "supportsInputSimulation":true,"inputBackend":"SendInput",
               "windowControlBackend":"Win32","macAddress":"9C:6B:00:9B:1B:D2"}"""

    @Test
    fun `host capabilities are summarised from the real wire keys`() {
        val bundle =
                DiagnosticsBundle.build(env(hostInfoJson = realHostInfo), includeNetworkInfo = false)

        assertTrue(bundle.contains("platform: Windows"))
        assertTrue(bundle.contains("runtime mode: interactive"))
        assertTrue(bundle.contains("remote desktop: true"))
        assertTrue(bundle.contains("input backend: SendInput"))
        assertTrue(bundle.contains("window control backend: Win32"))
    }

    @Test
    fun `the host version survives, in the form the rest of the app shows it`() {
        // TWO FAILURES IN ONE ASSERTION, both of which shipped in the first draft. The key was
        // `appVersion`, which does not exist, so this line read "(not reported)" against every real
        // host - and the reader of a bug report would conclude the host omitted its version and
        // suspect an ancient build.
        //
        // The second is subtler: .NET widens GetName().Version to four components, so the host
        // reports 2.4.0.0, which is a syntactically valid dotted quad. The scrubber cannot tell it
        // from an IP address by shape, so reporting it verbatim would REDACT the most useful line
        // in the section - and only when the network toggle was off, making the host version blink
        // in and out with an unrelated switch. Normalizing to the display form used everywhere else
        // (AppVersion.Normalize, RemEx-8jzu) fixes the consistency and sidesteps the rule.
        val bundle =
                DiagnosticsBundle.build(env(hostInfoJson = realHostInfo), includeNetworkInfo = false)

        assertTrue("the host version was redacted or dropped", bundle.contains("host version: 2.4.0"))
        assertFalse(bundle.contains("2.4.0.0"))
        assertFalse(bundle.contains("host version: ${DiagnosticsScrubber.REDACTED}"))
    }

    @Test
    fun `version normalization matches the PC side and leaves non-versions alone`() {
        assertEquals("2.4.0", DiagnosticsBundle.normalizeVersion("2.4.0.0"))
        assertEquals("2.4.0", DiagnosticsBundle.normalizeVersion("2.4.0"))
        assertEquals("2.4.0", DiagnosticsBundle.normalizeVersion("2.4.0+abc123"))
        assertEquals("2.4", DiagnosticsBundle.normalizeVersion("2.4"))

        // Handed back untouched rather than blanked: the host sends the literal "unknown" when it
        // cannot determine its own, and a prerelease tag is information too. Blanking would turn a
        // diagnosable value into no value.
        assertEquals("unknown", DiagnosticsBundle.normalizeVersion("unknown"))
        assertEquals("2.5.0-rc1", DiagnosticsBundle.normalizeVersion("2.5.0-rc1"))
        assertEquals("(not reported)", DiagnosticsBundle.normalizeVersion("(not reported)"))
        assertEquals("(empty)", DiagnosticsBundle.normalizeVersion("(empty)"))
        assertEquals("2", DiagnosticsBundle.normalizeVersion("2"))
        assertEquals("1..2", DiagnosticsBundle.normalizeVersion("1..2"))
        assertEquals("..", DiagnosticsBundle.normalizeVersion(".."))
        assertEquals("01.02.03", DiagnosticsBundle.normalizeVersion("01.02.03"))
    }

    @Test
    fun `a numeric run is reduced even when something follows it`() {
        // The residual hole review found: reducing only when the WHOLE string is numeric leaves
        // "2.4.0.0-rc1" untouched, and the scrubber's IPv4 rule ends on a word boundary that "-"
        // satisfies - so the bundle would render "host version: [redacted]-rc1", the exact failure
        // normalization exists to prevent, wearing a suffix. Unreachable today, because the host
        // builds this from GetName().Version which cannot carry one, but closed rather than left
        // merely unoccupied.
        assertEquals("2.4.0-rc1", DiagnosticsBundle.normalizeVersion("2.4.0.0-rc1"))
        assertEquals("2.4.0 build 7", DiagnosticsBundle.normalizeVersion("2.4.0.0 build 7"))

        // Five components reduce rather than being handed back whole - this is where it knowingly
        // diverges from AppVersion.Normalize, whose Version.TryParse rejects a fifth component and
        // returns the string intact. "1.2.3.4.5" still CONTAINS the quad "1.2.3.4", so the C#
        // behaviour would be eaten here; reducing is the safer half of the disagreement.
        assertEquals("1.2.3", DiagnosticsBundle.normalizeVersion("1.2.3.4.5"))
    }

    @Test
    fun `an absent capability is not reported as though the host had answered`() {
        // parseHostCapabilities defaults supportsInputSimulation to TRUE on purpose - an absent key
        // means an older host that should keep working, and defaulting false would brick input.
        // That is right as runtime policy and wrong in a report: "input simulation: true" would read
        // identically whether the host said true or said nothing. The concrete harm is a user
        // reporting "the keyboard does nothing", the bundle asserting the capability is present, and
        // the reader ruling out capability negotiation when the missing key WAS the answer.
        val silent = """{"version":"2.4.0.0","platform":"Windows"}"""

        val bundle = DiagnosticsBundle.build(env(hostInfoJson = silent), includeNetworkInfo = false)

        assertTrue(bundle.contains("input simulation: (not reported)"))
        assertTrue(bundle.contains("remote desktop: (not reported)"))
        assertFalse("a default was reported as if it were data", bundle.contains("input simulation: true"))
    }

    @Test
    fun `a field the host sent empty is distinguishable from one it never sent`() {
        // Opposite meanings: a key that never arrived points at a version skew, a key that arrived
        // empty points at the host itself. optString with a fallback cannot tell them apart.
        val partial = """{"version":"2.4.0.0","inputBackend":""}"""

        val bundle = DiagnosticsBundle.build(env(hostInfoJson = partial), includeNetworkInfo = false)

        assertTrue(bundle.contains("input backend: (empty)"))
        assertTrue(bundle.contains("window control backend: (not reported)"))
    }

    @Test
    fun `the host MAC address is never reported, and the scrubber would not save us`() {
        // THE ONE PRIVACY GUARANTEE THE SUITE WOULD OTHERWISE BE BLIND TO. host_info carries
        // macAddress for Wake-on-LAN (RemEx-izuj), and the scrubber DELIBERATELY lets MACs through -
        // `a mac address survives` in DiagnosticsScrubberTest pins that, because a MAC in a log line
        // is diagnostic rather than personal.
        //
        // In a bundle mailed to a public issue tracker it is neither: it is a stable hardware
        // identifier for the user's machine. The section simply does not report it, which is a
        // judgement rather than a mechanism - so if a future edit adds a `mac:` line, redaction will
        // NOT catch it and nothing else would notice. This test is what makes the omission binding.
        val bundle =
                DiagnosticsBundle.build(env(hostInfoJson = realHostInfo), includeNetworkInfo = false)

        assertFalse("the host MAC is in the shareable bundle", bundle.contains("9C:6B:00:9B:1B:D2"))

        // And it must stay out when the user opts in to network info: that toggle is about
        // addresses, which change, not about a hardware identifier, which does not.
        val optedIn =
                DiagnosticsBundle.build(env(hostInfoJson = realHostInfo), includeNetworkInfo = true)

        assertFalse(optedIn.contains("9C:6B:00:9B:1B:D2"))
    }

    @Test
    fun `the reason remote desktop is unavailable is carried, since it is usually the whole answer`() {
        val refused =
                """{"version":"2.4.0.0","supportsRemoteDesktop":false,
                   "remoteDesktopUnavailableReason":"no PipeWire portal on this session"}"""

        val bundle = DiagnosticsBundle.build(env(hostInfoJson = refused), includeNetworkInfo = false)

        assertTrue(bundle.contains("no PipeWire portal on this session"))
    }

    @Test
    fun `an unparseable host_info is reported rather than swallowed`() {
        // A malformed host_info is very often the fault being reported - a host/client version
        // skew. Silently rendering an empty section would hide the most useful line in the file.
        val bundle =
                DiagnosticsBundle.build(env(hostInfoJson = "{not json"), includeNetworkInfo = false)

        assertTrue(bundle.contains("could not be parsed"))
        assertTrue(bundle.contains("version skew"))
    }

    @Test
    fun `an empty log is called out rather than left blank`() {
        // A blank section reads as "there was nothing wrong" when it actually means collection
        // failed - on a device where logcat is unavailable to the app, that distinction is the
        // whole diagnosis.
        val bundle = DiagnosticsBundle.build(env(logcat = "   "), includeNetworkInfo = false)

        assertTrue(bundle.contains("no log lines were collected"))
    }

    @Test
    fun `every section header is present so the reader knows what is missing`() {
        val bundle = DiagnosticsBundle.build(env(), includeNetworkInfo = false)

        assertTrue(bundle.contains("[app]"))
        assertTrue(bundle.contains("[host]"))
        assertTrue(bundle.contains("[log]"))
    }

    @Test
    fun `building twice with the same input gives the same bundle`() {
        // No timestamps, no ordering that depends on a map's iteration - the preview the user
        // approves must be the text that gets sent, and a bundle that differs between the preview
        // call and the send call would break exactly that promise.
        val environment = env()

        assertEquals(
                DiagnosticsBundle.build(environment, includeNetworkInfo = false),
                DiagnosticsBundle.build(environment, includeNetworkInfo = false)
        )
    }
}
