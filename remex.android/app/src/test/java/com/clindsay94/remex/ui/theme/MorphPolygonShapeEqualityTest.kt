package com.clindsay94.remex.ui.theme

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

/**
 * P1-20: [MorphPolygonShape] has a hand-written equals/hashCode (not a data class), which is easy
 * to get subtly wrong. Two [cardShape] calls with the same arguments must produce shapes Compose's
 * change-detection treats as equal, or the outline-rebuild-per-recomposition bug this row fixes
 * comes right back.
 */
class MorphPolygonShapeEqualityTest {
    @Test
    fun `same index and progress produce equal shapes with matching hashCode`() {
        val a = cardShape(5.3f, 16)
        val b = cardShape(5.3f, 16)

        assertEquals(a, b)
        assertEquals(a.hashCode(), b.hashCode())
    }

    @Test
    fun `a different progress within the same index pair is not equal`() {
        val a = cardShape(5.3f, 16)
        val b = cardShape(5.7f, 16)

        assertNotEquals(a, b)
    }

    @Test
    fun `a different index pair is not equal`() {
        val a = cardShape(2.3f, 16)
        val b = cardShape(6.3f, 16)

        assertNotEquals(a, b)
    }
}
