package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.sortControlsFitInline
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Covers the Task Manager sort row's fit decision (RemEx-k0vy).
 *
 * The row could not fit its label, four equal-width sort chips and the direction button on one line
 * at large font scales, so the chip labels ellipsized down to nothing legible. The fix stacks the
 * chips onto their own full-width line when they do not fit — and the whole question of *when* they
 * do not fit is this one function, which is why it is pure.
 *
 * That matters because the alternative is a font-scale threshold, and a threshold is a single number
 * that has to be right for every language at every scale. It cannot be, and being wrong is silent:
 * too high and the labels clip, too low and the row wastes a line forever. Measuring is checkable;
 * a magic number is not.
 *
 * Pixel values below are a real 360dp-wide phone at density 2.75 (xxhdpi) — the common case, not a
 * convenient one.
 */
class SortControlsFitTest {

    private companion object {
        const val DENSITY = 2.75f

        fun dp(value: Float): Int = (value * DENSITY).toInt()

        /** 360dp screen minus the section's 16dp horizontal padding on each side. */
        val AVAILABLE = dp(360f - 32f)
        val ROW_SPACING = dp(8f)
        val DIRECTION_BUTTON = dp(48f)
        val CHIP_PADDING = dp(6f)
        val CHIP_SPACING = dp(4f)

        /** "Sort by:" at labelMedium 12sp. */
        val LABEL_AT_1X = dp(48f)

        /** "Name" — the widest of CPU / RAM / Name / PID — at labelLarge 14sp. */
        val WIDEST_CHIP_AT_1X = dp(30f)
    }

    private fun fits(
            widestChipLabelPx: Int,
            sortLabelWidthPx: Int = LABEL_AT_1X,
            availableWidthPx: Int = AVAILABLE,
            chipCount: Int = 4,
    ): Boolean =
            sortControlsFitInline(
                    availableWidthPx = availableWidthPx,
                    sortLabelWidthPx = sortLabelWidthPx,
                    widestChipLabelPx = widestChipLabelPx,
                    chipCount = chipCount,
                    chipHorizontalPaddingPx = CHIP_PADDING,
                    chipSpacingPx = CHIP_SPACING,
                    rowSpacingPx = ROW_SPACING,
                    directionButtonWidthPx = DIRECTION_BUTTON,
            )

    @Test
    fun `default English at 1x fits on one line`() {
        // The row must NOT start stacking for everybody — that would trade a rare bug for a
        // permanent one. This is the positive control: without it every assertion below could pass
        // with a function that always returns false.
        assertTrue(fits(widestChipLabelPx = WIDEST_CHIP_AT_1X))
    }

    @Test
    fun `the 1_6x combined ceiling does not fit, which is the bug`() {
        // MAX_COMBINED_FONT_SCALE is 1.6, so this is the worst case the app itself permits: both the
        // label and the chips grow while the screen does not. Before this change the chips simply
        // absorbed the shortfall and ellipsized.
        val scale = 1.6f
        assertFalse(
                fits(
                        widestChipLabelPx = (WIDEST_CHIP_AT_1X * scale).toInt(),
                        sortLabelWidthPx = (LABEL_AT_1X * scale).toInt(),
                )
        )
    }

    @Test
    fun `system scale can exceed the app ceiling and that must stack too`() {
        // The app clamps only its OWN contribution and never counteracts the system setting, so on
        // API 34+ non-linear scaling the system alone reaches 2.0 and wins (Type.kt,
        // clampedAppFontScale). The bead assumed a 1.5 ceiling; there is not one.
        val scale = 2.0f
        assertFalse(
                fits(
                        widestChipLabelPx = (WIDEST_CHIP_AT_1X * scale).toInt(),
                        sortLabelWidthPx = (LABEL_AT_1X * scale).toInt(),
                )
        )
    }

    @Test
    fun `sizing follows the widest label, not the average`() {
        // Every chip carries weight(1f), so they are all as wide as the row's equal share and the
        // WIDEST label decides whether any of them clips. A function that averaged, or that summed
        // the four real labels, would report a comfortable fit here — three of these are tiny.
        val oneLongLabel = dp(90f)
        assertFalse(fits(widestChipLabelPx = oneLongLabel))
    }

    @Test
    fun `a wider screen fits what a phone cannot`() {
        // Tablet / landscape: the same content at the same scale must go back to one line rather
        // than stay stacked, or the decision is not really about fit.
        val scale = 1.6f
        assertTrue(
                fits(
                        widestChipLabelPx = (WIDEST_CHIP_AT_1X * scale).toInt(),
                        sortLabelWidthPx = (LABEL_AT_1X * scale).toInt(),
                        availableWidthPx = dp(800f - 32f),
                )
        )
    }

    @Test
    fun `exact fit is allowed and one pixel more is not`() {
        // The boundary is where an off-by-one hides. Solve for the largest chip label that fits,
        // then assert both sides of it.
        val remaining = AVAILABLE - LABEL_AT_1X - DIRECTION_BUTTON - 2 * ROW_SPACING
        val perChip = (remaining - 3 * CHIP_SPACING) / 4
        val exact = perChip - 2 * CHIP_PADDING

        assertTrue(fits(widestChipLabelPx = exact))
        assertFalse(fits(widestChipLabelPx = exact + 1))
    }

    @Test
    fun `a label pinned at its 120dp cap stacks even at 1x`() {
        // NOT a regression, and worth pinning because it looks like one. The label is capped at
        // 120dp, and a locale whose "Sort by" reaches that cap leaves the chips 144dp on a 360dp
        // phone against the 180dp four English chips need — so this row stacks at DEFAULT font
        // scale in the longer languages. It was already broken there; the chips were being squeezed
        // to about 24dp of text each and ellipsizing. Stacking is the fix arriving, not a new bug,
        // but a future reader seeing a two-line sort row at 1x deserves to find this test rather
        // than file it.
        assertFalse(fits(widestChipLabelPx = WIDEST_CHIP_AT_1X, sortLabelWidthPx = dp(120f)))
    }

    @Test
    fun `a small phone stacks at 1x in English`() {
        // English at 1x needs a 324dp-wide window to stay inline, so a 320dp device — or any split
        // screen or freeform window that narrow — stacks. Recording the number here means changing
        // any of the paddings tells you it moved.
        assertFalse(fits(widestChipLabelPx = WIDEST_CHIP_AT_1X, availableWidthPx = dp(320f - 32f)))
        assertTrue(fits(widestChipLabelPx = WIDEST_CHIP_AT_1X, availableWidthPx = dp(324f - 32f)))
    }

    @Test
    fun `fewer chips need less room`() {
        // chipCount is a parameter, not a constant 4, and the (chipCount - 1) spacing term is the
        // kind of thing that is wrong for every value but the one it was written against.
        val tooWideForFour = dp(60f)
        assertFalse(fits(widestChipLabelPx = tooWideForFour, chipCount = 4))
        assertTrue(fits(widestChipLabelPx = tooWideForFour, chipCount = 2))
    }

    @Test
    fun `no chips is not a layout problem`() {
        // Guard: an empty list would otherwise compute (chipCount - 1) spacing of -1.
        assertTrue(fits(widestChipLabelPx = 0, chipCount = 0))
    }
}
