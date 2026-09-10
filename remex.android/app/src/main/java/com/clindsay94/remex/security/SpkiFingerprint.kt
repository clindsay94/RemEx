package com.clindsay94.remex.security

/**
 * Formats an SPKI pin so a person can compare two of them by eye (RemEx-vnps).
 *
 * **THE DISPLAY IS TRUNCATED. THE COMPARISON IS NOT.** That separation is the whole reason this is
 * a class rather than a `substring` at the call site: showing a short prefix is what makes visual
 * comparison possible at all, but deciding whether two certificates differ by comparing the
 * SHORTENED forms would call two different certificates identical whenever they happen to share a
 * prefix. On a screen that is about to offer "clear my pin", that is the wrong direction to be
 * wrong in — the user would be told nothing changed and shown a button that discards their
 * protection anyway.
 *
 * The pin itself is public certificate material — a hash of a public key — so displaying it leaks
 * nothing. What it does is let a suspicious user check the new fingerprint against what their PC
 * reports, which is the only way a human can tell "I reinstalled RemEx" from "someone is
 * intercepting this connection".
 */
object SpkiFingerprint {

    /** Characters shown, before grouping. */
    private const val DisplayLength = 16

    /** Characters per visual group. */
    private const val GroupSize = 4

    /**
     * Renders a pin as short, grouped text for on-screen comparison.
     *
     * **GROUPED, BECAUSE AN UNGROUPED RUN IS NOT ACTUALLY COMPARABLE.** A person asked to check
     * sixteen unbroken characters against another sixteen will glance at the first three and the
     * last two and call it a match — which defeats the point of showing it. Four-character groups
     * are the same reason card numbers and licence keys are grouped.
     *
     * Returns a marker rather than an empty string when there is nothing to show, so a dialog
     * comparing old against new can never render a blank next to a real value and read as though
     * one side is simply missing.
     */
    fun forDisplay(spkiPin: String?): String {
        val normalized = normalize(spkiPin) ?: return Unavailable

        return normalized
            .take(DisplayLength)
            .chunked(GroupSize)
            .joinToString(" ")
    }

    /** Shown when a pin is absent or unusable. */
    const val Unavailable: String = "(none)"

    /**
     * Whether two pins are genuinely the same certificate.
     *
     * **COMPARES THE FULL VALUE, NEVER THE DISPLAY FORM**, and ordinally: base64 is
     * case-significant, so folding case would treat two different certificates as one. The
     * `sha256/` prefix and surrounding whitespace are normalized away first, because the app writes
     * pins both ways in different places and two spellings of ONE pin must not read as a
     * certificate change — which would send the user to a scary dialog about a machine that did
     * nothing.
     */
    fun isSameCertificate(a: String?, b: String?): Boolean {
        val left = normalize(a) ?: return false
        val right = normalize(b) ?: return false
        return left == right
    }

    /**
     * Strips decoration without touching case.
     *
     * Deliberately the same treatment [HostIdentity] applies, so the two cannot disagree about
     * whether a host has changed.
     */
    private fun normalize(spkiPin: String?): String? {
        val trimmed = spkiPin?.trim()?.removePrefix("sha256/")?.trim()
        return if (trimmed.isNullOrEmpty()) null else trimmed
    }
}
