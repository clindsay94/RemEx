package com.clindsay94.remex.widget

/**
 * Text truncation for Glance widgets, where the framework cannot do it (RemEx-4g8g).
 *
 * WHY THIS EXISTS AT ALL. `androidx.glance.text.Text` takes only text, modifier, style and
 * maxLines — there is no `overflow` parameter and no `TextOverflow` type, verified by compiling
 * against Glance 1.2.0-rc01 rather than by reading release notes: the attempt fails with
 * "No parameter with name 'overflow' found" and "Unresolved reference 'TextOverflow'". So
 * `maxLines = 1` on its own hard-clips mid-glyph, exactly as the app screens did before RemEx-wdit
 * fixed them with an attribute that does not exist here. Truncating the string before it is handed
 * to Glance is the only mechanism available today.
 *
 * SENSOR NAMES ARE THE WORST CASE and the reason this is not cosmetic: they come from the host, so
 * they are arbitrary, and HWiNFO produces labels like "Core Max Distance to TjMAX". A hard clip
 * turns that into something the user cannot identify; an ellipsis at least says "there is more".
 */
object WidgetText {

    /** What a truncated string ends with. One character, so it costs one character of budget. */
    const val ELLIPSIS: Char = '…'

    /**
     * Returns [text] unchanged if it fits in [maxChars], otherwise shortened to exactly [maxChars]
     * characters ending in an ellipsis.
     *
     * @param maxChars The character budget. See [SensorNameBudget] and friends for how these are
     *   chosen; they are ESTIMATES from font size and column width, not measurements, because Glance
     *   renders in the launcher's process and this code cannot measure the resulting text.
     */
    fun ellipsize(text: String, maxChars: Int): String {
        // A budget that cannot hold even the ellipsis has no sensible answer; returning the original
        // is the least destructive one, and it keeps a caller's arithmetic bug visible as untruncated
        // text rather than as a string of bare ellipses.
        if (maxChars <= 1) return text
        if (text.length <= maxChars) return text

        // SURROGATE PAIRS ARE THE TRAP. String.take() counts UTF-16 code units, so cutting at an
        // arbitrary index can land BETWEEN the two halves of an emoji or a supplementary-plane CJK
        // character, leaving a lone surrogate that renders as a replacement box. That is clipping
        // mid-glyph - the exact defect this function exists to remove - reintroduced by the fix.
        var cut = maxChars - 1
        if (Character.isHighSurrogate(text[cut - 1])) cut--

        // Trailing space before an ellipsis reads as a typo ("Core Max …"), so drop it. Done after
        // the surrogate step because trimming can only shorten, never re-split a pair.
        return text.substring(0, cut).trimEnd() + ELLIPSIS
    }

    /**
     * Budget for a host-supplied sensor NAME in the medium/large grid cell (9-10sp, ~half width).
     */
    const val SensorNameBudget: Int = 18

    /** Budget for a sensor name in the compact single-row layout (9sp, three across). */
    const val CompactSensorNameBudget: Int = 12

    /** Budget for a sensor CATEGORY, rendered smaller and less important than the name (8sp). */
    const val SensorCategoryBudget: Int = 16

    /**
     * Budget for a formatted VALUE, e.g. "42.0 °C".
     *
     * Larger and bolder than the name, so fewer characters fit — but values are short by
     * construction, so this is a backstop against a pathological unit rather than an expected path.
     */
    const val SensorValueBudget: Int = 12

    /** Budget for a localized control label on the remote-control widget. */
    const val ControlLabelBudget: Int = 14
}
