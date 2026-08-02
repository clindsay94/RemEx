package com.clindsay94.remex.diagnostics

import org.json.JSONObject

/**
 * Assembles the shareable diagnostics bundle (RemEx-3rjf).
 *
 * EVERYTHING LEAVES THROUGH ONE DOOR. [build] composes the sections and then scrubs the whole
 * assembled text exactly once, at the end. Scrubbing each section as it is written would be the
 * obvious alternative and it is the wrong one: it makes redaction a thing every future section has
 * to remember to do, and the section somebody forgets will be the one carrying a credential. With a
 * single choke point there is no path from a section to the output that skips
 * [DiagnosticsScrubber], and [DiagnosticsBundleTest] pins that by feeding a secret through a
 * section that has no business containing one.
 *
 * THE POINT OF THE FEATURE is that a user can mail their logs to someone who can read them, which
 * is exactly the shape of accident that leaks a credential: the user is being helpful, the
 * recipient may be a public issue tracker, and nobody reads 500 lines of logcat before pressing
 * send. So the bundle is built to be shown to the user in full BEFORE it is sent, and what is
 * previewed has to be the scrubbed text or they are approving something other than what leaves.
 *
 * The section headings and labels here are deliberately NOT localized. This text is read by whoever
 * is debugging the problem, not by the person sending it — the same reason log messages stay in
 * English (CLAUDE.md, Localization). The screen around it is user-facing and is localized normally.
 */
object DiagnosticsBundle {

    /** Everything the bundle reports, gathered by the caller so that [build] stays pure. */
    data class Environment(
            val appVersion: String,
            val deviceManufacturer: String,
            val deviceModel: String,
            val androidApiLevel: Int,
            /** The raw `host_info` JSON, or null if the phone has never heard from a host. */
            val hostInfoJson: String?,
            /** Human-readable connection phase, e.g. "connected", "disconnected". */
            val connectionPhase: String,
            /** Recent log lines, already truncated by the caller. */
            val logcat: String
    )

    /**
     * Builds the bundle and returns it SCRUBBED and ready to preview or send.
     *
     * @param includeNetworkInfo Passed straight through to [DiagnosticsScrubber.scrub]. Off by
     *   default at every layer: a network bug is unreadable without addresses, but that is the
     *   user's call to make explicitly, and the toggle releases ONLY addresses — never keys or
     *   paths.
     */
    fun build(environment: Environment, includeNetworkInfo: Boolean): String {
        val raw = buildString {
            appendLine("=== RemEx diagnostics ===")
            appendLine()

            appendLine("[app]")
            appendLine("version: ${environment.appVersion}")
            appendLine("device: ${environment.deviceManufacturer} ${environment.deviceModel}")
            appendLine("android API: ${environment.androidApiLevel}")
            appendLine("connection: ${environment.connectionPhase}")
            appendLine()

            appendLine("[host]")
            appendLine(describeHost(environment.hostInfoJson))
            appendLine()

            appendLine("[log]")
            append(environment.logcat.ifBlank { "(no log lines were collected)" })
        }

        // THE ONLY EXIT. Do not add a `return` above this line.
        return DiagnosticsScrubber.scrub(raw, includeNetworkInfo)
    }

    /**
     * Summarises the host's advertised capabilities.
     *
     * A MALFORMED `host_info` IS ITSELF A DIAGNOSTIC, so a parse failure is reported rather than
     * swallowed — the bundle exists to explain a fault, and "the host sent something this build
     * could not read" is very often the fault. Reporting nothing would hide the most useful line in
     * the file behind an empty section. The raw text is not echoed back: it goes through the same
     * scrubber as everything else, but a payload nobody could parse is also a payload nobody can
     * vouch for the shape of.
     */
    private fun describeHost(hostInfoJson: String?): String {
        if (hostInfoJson.isNullOrBlank()) {
            return "no host_info received (the phone has not completed a session this run)"
        }

        return try {
            val json = JSONObject(hostInfoJson)
            buildString {
                appendLine("host version: ${normalizeVersion(json.reportedString("version"))}")
                appendLine("platform: ${json.reportedString("platform")}")
                appendLine("runtime mode: ${json.reportedString("runtimeMode")}")
                appendLine("interactive session: ${json.reportedBoolean("isInteractiveSession")}")
                appendLine("remote desktop: ${json.reportedBoolean("supportsRemoteDesktop")}")
                appendLine("input simulation: ${json.reportedBoolean("supportsInputSimulation")}")
                appendLine("input backend: ${json.reportedString("inputBackend")}")
                appendLine("window control backend: ${json.reportedString("windowControlBackend")}")
                // Labelled as the FIELD it is, not as a statement. "unavailable because: (not
                // reported)" renders on every healthy host, since the reason is null when remote
                // desktop works - sitting directly under "remote desktop: true" that reads as a
                // contradiction, and it is the one line in this section that can be misread
                // backwards at a glance.
                append(
                        "remote desktop unavailable reason: " +
                                json.reportedString("remoteDesktopUnavailableReason")
                )
            }
        } catch (e: org.json.JSONException) {
            "host_info could not be parsed (${e.javaClass.simpleName}) - " +
                    "this is itself worth reporting, it usually means a host/client version skew"
        }
    }

    private const val NOT_REPORTED = "(not reported)"

    /**
     * Reads a string field, distinguishing ABSENT from present-and-empty.
     *
     * `optString(key, fallback)` cannot tell those apart, and in a bug report they mean opposite
     * things: a key that never arrived points at the host, while a key that arrived empty points at
     * whatever the host was trying to describe. The report has to say which.
     *
     * `(not reported)` covers two cases for the STRING fields specifically, because three of them
     * are nullable on the wire: an older host that never sent the key, and a current host that sent
     * null because it had nothing to say. The booleans are non-nullable and always serialized, so
     * for those an absent key really does mean a version skew.
     */
    private fun JSONObject.reportedString(key: String): String =
            if (has(key) && !isNull(key)) optString(key).ifBlank { "(empty)" } else NOT_REPORTED

    /**
     * Reads a boolean field WITHOUT applying the runtime default.
     *
     * `parseHostCapabilities` deliberately defaults `supportsInputSimulation` to true, because an
     * absent key means an older host that should keep working and defaulting false would brick
     * input. That is correct as runtime POLICY and wrong in a report: `input simulation: true` would
     * read identically whether the host said true or said nothing at all. The concrete harm is a
     * user reporting "the keyboard does nothing", the bundle asserting the capability is present,
     * and whoever reads it ruling out capability negotiation — when the missing key was the answer.
     */
    private fun JSONObject.reportedBoolean(key: String): String =
            if (has(key)) optBoolean(key).toString() else NOT_REPORTED

    /**
     * Reduces a host-reported version to the display form, e.g. `2.4.0.0` to `2.4.0`.
     *
     * Mirrors `AppVersion.Normalize` on the PC side (RemEx-8jzu), which exists because the host
     * builds its version from `GetName().Version` and .NET always widens that to four components,
     * so the same machine got labelled `2.4.0` and `2.4.0.0` one divider apart. Matching it here
     * keeps the bundle consistent with every other place the repo shows a remote version.
     *
     * IT ALSO KEEPS THE VERSION OUT OF THE SCRUBBER'S IPv4 RULE, which is not a coincidence worth
     * relying on but is worth writing down: `2.4.0.0` is a syntactically valid dotted quad, octets
     * and all, so the scrubber cannot tell it from an address by shape and would redact the single
     * most useful line in the section — and it would do so only when the network toggle was off,
     * making the host version appear and disappear with an unrelated switch. Three components do
     * not match, which is pinned on the scrubber's side by its own version-survival test.
     *
     * A value that is not a version is handed back untouched rather than blanked: the host sends
     * the literal `unknown` when it cannot determine its own, and a prerelease tag like `2.5.0-rc1`
     * is information too. Blanking would turn a diagnosable value into no value.
     */
    internal fun normalizeVersion(raw: String): String {
        val trimmed = raw.substringBefore('+').trim()
        if (trimmed.isEmpty()) return raw

        // Reduce the leading NUMERIC RUN and keep whatever follows it. Matching the whole string
        // instead would leave "2.4.0.0-rc1" untouched, and the scrubber's IPv4 rule ends on a word
        // boundary that the "-" satisfies — so the bundle would render "host version: [redacted]-rc1",
        // the exact failure normalization exists to prevent, just wearing a suffix. The PC-side
        // mirror has the same hole; it is unreachable there because the host builds this from
        // GetName().Version, which cannot carry a suffix. Closed here rather than left unoccupied.
        val core = Regex("""^\d+(?:\.\d+)+""").find(trimmed)?.value ?: return trimmed
        val parts = core.split('.')

        // Five components reduce to three rather than being handed back whole, which is where this
        // deliberately diverges from AppVersion.Normalize: .NET's Version rejects a fifth component
        // and returns the string untouched, but "1.2.3.4.5" still contains the dotted quad
        // "1.2.3.4" and would be eaten. Reducing is the safer half of the disagreement.
        return parts.take(if (parts.size >= 3) 3 else 2).joinToString(".") +
                trimmed.substring(core.length)
    }
}
