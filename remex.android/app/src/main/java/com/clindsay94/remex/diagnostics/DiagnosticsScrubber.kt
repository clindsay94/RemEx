package com.clindsay94.remex.diagnostics

/**
 * Removes secrets and personal detail from a diagnostics bundle before it leaves the device.
 *
 * WHAT THIS IS PROTECTING AGAINST (RemEx-nwnn). The diagnostics bundle exists so a user can mail
 * their logs to someone who can read them. That is exactly the shape of accident that leaks a
 * credential: the user is trying to be helpful, the recipient may be a public issue tracker, and
 * nobody reads 500 lines of logcat before pressing send.
 *
 * SCRUBBED ALWAYS:
 *  - Long base64 runs. Both the pinned SPKI hash and the PAIR-1 reconnect secret are base64 of 32
 *    bytes, so a run of 40 or more base64 characters is a key, not prose. The reconnect secret is
 *    the one that actually matters: it answers the host's proof-of-possession challenge, so it is a
 *    credential rather than an identifier.
 *  - Absolute file paths, reduced to their last segment. A transfer log line naming
 *    `C:\Users\Connor\Documents\tax-2025.pdf` says far more about the person than about the bug.
 *
 * SCRUBBED UNLESS THE USER OPTS IN:
 *  - IP addresses and `host:port` pairs. These are frequently the whole point of a network bug
 *    report, so there is a toggle rather than a blanket rule — but it is OFF by default, because
 *    the safe direction is the one that loses information.
 *
 * DELIBERATELY NOT SCRUBBED: the device model, API level, app and host versions, and capability
 * flags. Those are the report's entire diagnostic value and none of them identifies a person.
 *
 * Pure and side-effect free so it can be tested exhaustively off-device — the negative cases matter
 * as much as the positive ones, since a scrubber that eats ordinary log text produces a bundle
 * nobody can debug from.
 */
object DiagnosticsScrubber {

    const val REDACTED: String = "[redacted]"

    /**
     * Forty, not forty-four.
     *
     * A base64 SHA-256 is exactly 44 characters with its padding, but the same value shows up
     * truncated in prefixes and log-friendly abbreviations, and a secret is not less of a secret for
     * being clipped. Forty is below any realistic key encoding and far above the longest ordinary
     * run of base64-legal characters in log prose — English words break on spaces and punctuation
     * long before this.
     */
    private const val MinKeyLength = 40

    private val LongBase64 = Regex("[A-Za-z0-9+/]{$MinKeyLength,}={0,2}")

    /**
     * Windows (`C:\a\b\c.txt`, `\\server\share\c.txt`) and POSIX (`/a/b/c.txt`) absolute paths.
     *
     * THE POSIX BRANCH IS ANCHORED TO A TOKEN BOUNDARY, and that is not a detail. Without it the
     * pattern has no notion of a URL scheme and happily treats the path part of one as a filesystem
     * path: review found `wss://desktop-pc:5005/ws/desktop` becoming `wss://desktop-pc:5005desktop`
     * and `https://github.com/clindsay94/remex/issues/12` becoming `https:/12`. That is this repo's
     * own remote-desktop endpoint, mangled in exactly the connection-failure lines a diagnostics
     * bundle exists to carry. Requiring the leading slash to start a token excludes both, because
     * there the slash follows a port digit or a colon.
     */
    private val AbsolutePath = Regex(
        """(?:[A-Za-z]:\\|\\\\)(?:[^\s\\/:*?"<>|]+[\\/])*([^\s\\/:*?"<>|]+)""" +
            """|(?:(?<=^)|(?<=\s))/(?:[^\s\\/:*?"<>|]+/)+([^\s\\/:*?"<>|]+)"""
    )

    /**
     * A dotted quad, optionally with a port.
     *
     * Anchored on four octets so a version string cannot match: `2.4.0` has three parts and
     * `10.0.19041` has three, and neither is an address. Octet values are not range-checked — a
     * scrubber that lets `999.1.2.3` through on a technicality is worse than one that redacts a
     * number that was never an address.
     */
    private val IpV4 = Regex("""\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(?::\d{1,5})?\b""")

    /**
     * IPv6, in the two forms that are unambiguously addresses: fully expanded, or `::`-compressed.
     *
     * A COLON-COUNTING PATTERN IS NOT SAFE HERE, which review caught before this shipped. Digits are
     * hex digits, so `(?:[0-9A-Fa-f]{0,4}:){2,7}...` matches any `HH:MM:SS` — and the collection step
     * this scrubber exists for is `logcat -d`, whose every line begins with a time of day. The
     * default path would have redacted the timestamp on essentially the whole bundle, silently,
     * while looking like it was protecting something. It also ate durations and `a:b:c` triples.
     *
     * Requiring either eight groups or a literal `::` costs nothing real: those are the forms an
     * address is actually written in, and neither a clock nor a MAC address can produce one.
     */
    private val IpV6 = Regex(
        // Boundaries are hex-aware rather than \b: a word boundary sits happily BEFORE a colon,
        // so "03:04:05" offered ":04:05" to the compressed-form branch and matched. Caught by the
        // tests below, after a first tightening attempt still ate every timestamp.
        """(?<![0-9A-Fa-f:])(?:""" +
            // Fully expanded: exactly eight groups.
            """(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}""" +
            // Or compressed, which must contain a literal "::" - what a clock cannot produce.
            """|(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4})*)?::(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4})*)?""" +
        """)(?![0-9A-Fa-f:])"""
    )

    /**
     * Scrubs one bundle.
     *
     * @param text the collected diagnostics.
     * @param includeNetworkInfo when true, addresses are kept — the user's explicit choice, since a
     *   network bug is often unreadable without them. Everything else is scrubbed regardless.
     */
    fun scrub(text: String, includeNetworkInfo: Boolean = false): String {
        // Keys first: a base64 run can contain slashes, and reducing it to a "path" afterwards would
        // leave a recognisable tail of the secret behind rather than removing it.
        var result = LongBase64.replace(text, REDACTED)

        result = AbsolutePath.replace(result) { match ->
            // Two alternatives, so two groups: whichever branch matched holds the file name.
            match.groupValues[1].ifEmpty { match.groupValues[2] }
        }

        if (!includeNetworkInfo) {
            result = IpV4.replace(result, REDACTED)
            result = IpV6.replace(result, REDACTED)
        }

        return result
    }
}
