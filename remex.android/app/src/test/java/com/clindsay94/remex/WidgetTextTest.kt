package com.clindsay94.remex

import com.clindsay94.remex.widget.WidgetText
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the Glance truncation rule (RemEx-4g8g).
 *
 * Glance's Text has no overflow parameter, so the string must be shortened before it is handed
 * over. The failure this replaces is a hard clip mid-glyph; the failure it must not INTRODUCE is a
 * different clip mid-glyph, one code unit further in.
 */
class WidgetTextTest {

    @Test
    fun `a name that fits is left completely alone`() {
        assertEquals("CPU Package", WidgetText.ellipsize("CPU Package", 18))
    }

    @Test
    fun `a name of exactly the budget is not truncated`() {
        // Off-by-one guard: budget is a maximum, not an exclusive bound. Truncating here would spend
        // an ellipsis to say nothing was omitted.
        val exact = "x".repeat(18)

        assertEquals(exact, WidgetText.ellipsize(exact, 18))
    }

    @Test
    fun `a long host sensor name is shortened and says so`() {
        // The worst case named in the bead: HWiNFO labels are arbitrary and long.
        val result = WidgetText.ellipsize("Core Max Distance to TjMAX", 18)

        assertEquals(18, result.length)
        assertTrue(result.endsWith(WidgetText.ELLIPSIS))
        assertTrue("the surviving prefix must still identify the sensor", result.startsWith("Core Max"))
    }

    @Test
    fun `the result never exceeds the budget`() {
        // The whole point. A result one character over is a clip in a new place.
        for (n in 2..40) {
            val result = WidgetText.ellipsize("Core Max Distance to TjMAX", n)
            assertTrue("budget $n produced ${result.length} chars", result.length <= n)
        }
    }

    @Test
    fun `an emoji is never split in half`() {
        // THE TRAP THIS FUNCTION EXISTS TO AVOID REINTRODUCING. String.take counts UTF-16 code
        // units, so cutting at an arbitrary index can land BETWEEN the two halves of a surrogate
        // pair, leaving a lone surrogate that renders as a replacement box - clipping mid-glyph,
        // which is the defect being fixed, reappearing inside the fix.
        //
        // "🔥" is a fire emoji: two code units, one glyph. The budget is chosen so the
        // naive cut lands exactly between them.
        val text = "Temp 🔥 rising"

        val result = WidgetText.ellipsize(text, 7)

        assertTrue("a lone high surrogate survived", result.none { it.isHighSurrogate() && it == result.last() })
        result.forEachIndexed { i, c ->
            if (c.isHighSurrogate()) {
                assertTrue("high surrogate at $i has no low surrogate after it",
                    i + 1 < result.length && result[i + 1].isLowSurrogate())
            }
            if (c.isLowSurrogate()) {
                assertTrue("low surrogate at $i has no high surrogate before it",
                    i > 0 && result[i - 1].isHighSurrogate())
            }
        }
    }

    @Test
    fun `a trailing space is not left sitting before the ellipsis`() {
        // "Core Max …" reads as a typo rather than as truncation.
        val result = WidgetText.ellipsize("Core Max Distance", 10)

        assertEquals("Core Max" + WidgetText.ELLIPSIS, result)
    }

    @Test
    fun `a budget too small to hold anything returns the original rather than an ellipsis soup`() {
        // A caller's arithmetic bug should surface as visibly untruncated text, not as a column of
        // bare ellipses that looks deliberate.
        assertEquals("CPU", WidgetText.ellipsize("CPU", 1))
        assertEquals("CPU", WidgetText.ellipsize("CPU", 0))
        assertEquals("CPU", WidgetText.ellipsize("CPU", -5))
    }

    @Test
    fun `an empty string survives every budget`() {
        assertEquals("", WidgetText.ellipsize("", 18))
        assertEquals("", WidgetText.ellipsize("", 0))
    }

    @Test
    fun `every declared budget can hold something more than the ellipsis`() {
        // A budget of 1 would silently disable truncation at that call site, and a budget of 2 would
        // render one character plus an ellipsis - technically working, uselessly. This catches a
        // future edit that tightens one of these past the point of meaning anything.
        val budgets = listOf(
            WidgetText.SensorNameBudget,
            WidgetText.CompactSensorNameBudget,
            WidgetText.SensorCategoryBudget,
            WidgetText.SensorValueBudget,
            WidgetText.ControlLabelBudget
        )

        assertTrue(budgets.all { it >= 8 })
    }
}
