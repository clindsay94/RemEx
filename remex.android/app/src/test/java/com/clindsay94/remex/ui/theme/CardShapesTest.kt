package com.clindsay94.remex.ui.theme

import androidx.compose.foundation.shape.CutCornerShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.unit.Constraints
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.ui.screens.DashboardShapes
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.sin

/**
 * RemEx-kq10x.5 cut the tile shapes to two; RemEx-pp4cm.14 brought a curated set back. Every offered
 * shape is a safe container: its measured inscribed rectangle clears [CardShapes.MIN_SAFE_FRACTION],
 * the card pads content to it, and every saved value (old or new) still resolves to an offered shape.
 */
@OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class)
class CardShapesTest {

    @Test
    fun `offered shapes are the rounded rectangle, the cut corner, then the six polygons`() {
        assertEquals(
            listOf(
                CardShapes.ROUNDED_RECTANGLE, CardShapes.CUT_CORNER, CardShapes.SLANTED, CardShapes.CLOVER,
                CardShapes.CLAM_SHELL, CardShapes.ARCH, CardShapes.SUNNY, CardShapes.PIXEL_CIRCLE,
            ),
            CardShapes.options,
        )
        assertEquals(8, CardShapes.options.size)
    }

    @Test
    fun `offered polygons keep the index they always had in the expressive list`() {
        // Saved values from before the cut-back are still in DataStore; these must read as themselves.
        assertSame(androidx.compose.material3.MaterialShapes.Arch, materialShapesList[CardShapes.ARCH.toInt()])
        assertSame(androidx.compose.material3.MaterialShapes.Slanted, materialShapesList[CardShapes.SLANTED.toInt()])
        assertSame(androidx.compose.material3.MaterialShapes.ClamShell, materialShapesList[CardShapes.CLAM_SHELL.toInt()])
        assertSame(androidx.compose.material3.MaterialShapes.Clover4Leaf, materialShapesList[CardShapes.CLOVER.toInt()])
        assertSame(androidx.compose.material3.MaterialShapes.Sunny, materialShapesList[CardShapes.SUNNY.toInt()])
        assertSame(androidx.compose.material3.MaterialShapes.PixelCircle, materialShapesList[CardShapes.PIXEL_CIRCLE.toInt()])
    }

    @Test
    fun `every offered shape reads back as itself`() {
        CardShapes.options.forEach { assertEquals(it, CardShapes.sanitize(it), 0f) }
        assertEquals(CardShapes.CUT_CORNER, CardShapes.sanitize(25.2f), 0f) // mid-animation snaps
        assertEquals(CardShapes.ARCH, CardShapes.sanitize(5.3f), 0f)
    }

    @Test
    fun `every legacy index resolves to an offered shape`() {
        for (old in 0..25) {
            val resolved = CardShapes.sanitize(old.toFloat())
            assertTrue("old index $old -> $resolved", resolved in CardShapes.options)
        }
    }

    @Test
    fun `removed shapes map to their nearest offered cousin by character`() {
        val rounded = CardShapes.ROUNDED_RECTANGLE
        val cut = CardShapes.CUT_CORNER
        // Smooth round shapes and the square keep reading as the rounded rectangle.
        listOf(0, 1, 6, 7, 12, 14, 15, 16, 17).forEach { assertEquals("index $it", rounded, CardShapes.sanitize(it.toFloat()), 0f) }
        // Angular shapes become the cut corner.
        listOf(2, 3, 4, 11).forEach { assertEquals("index $it", cut, CardShapes.sanitize(it.toFloat()), 0f) }
        // Scalloped and bursting shapes land on their cousin.
        assertEquals(CardShapes.CLOVER, CardShapes.sanitize(13f), 0f) // Flower
        assertEquals(CardShapes.CLOVER, CardShapes.sanitize(19f), 0f) // 8-leaf clover
        assertEquals(CardShapes.SUNNY, CardShapes.sanitize(21f), 0f) // Soft burst
        assertEquals(CardShapes.SUNNY, CardShapes.sanitize(22f), 0f) // Soft boom
        assertEquals(CardShapes.CLAM_SHELL, CardShapes.sanitize(9f), 0f) // Fan
    }

    @Test
    fun `inherit, NaN, infinity and out of range values read as the rounded rectangle`() {
        listOf(-1f, 26f, 99f, Float.NaN, Float.POSITIVE_INFINITY, Float.NEGATIVE_INFINITY).forEach {
            assertEquals("value $it", CardShapes.ROUNDED_RECTANGLE, CardShapes.sanitize(it), 0f)
        }
    }

    @Test
    fun `every offered polygon clears the safe fraction floor, so content can never clip`() {
        CardShapes.polygonOptions.forEach {
            val s = CardShapes.safeFraction(it)
            assertTrue("shape $it safe fraction $s below ${CardShapes.MIN_SAFE_FRACTION}", s >= CardShapes.MIN_SAFE_FRACTION)
            assertTrue("shape $it safe fraction $s is not a real inset", s < 1f)
        }
    }

    @Test
    fun `the floor rejects the shapes that were cut for clipping`() {
        // Triangle, Diamond, Heart and Soft Boom leave well under the floor: they must stay out.
        listOf(2, 3, 12, 22).forEach {
            val s = CardShapeSafety.safeFraction(materialShapesList[it])
            assertTrue("old index $it measures $s", s < CardShapes.MIN_SAFE_FRACTION)
        }
    }

    @Test
    fun `rounded rectangle and cut corner use the whole card`() {
        assertEquals(1f, CardShapes.safeFraction(CardShapes.ROUNDED_RECTANGLE), 0f)
        assertEquals(1f, CardShapes.safeFraction(CardShapes.CUT_CORNER), 0f)
        assertEquals(1f, CardShapes.safeFraction(DashboardShapes.SHAPE_PRESET_INHERIT), 0f)
    }

    @Test
    fun `cardShape renders each kind, and a removed shape as its cousin`() {
        assertEquals(RoundedCornerShape(16.dp), cardShape(CardShapes.ROUNDED_RECTANGLE, 16))
        assertEquals(RoundedCornerShape(16.dp), cardShape(0f, 16)) // legacy Circle
        assertEquals(RoundedCornerShape(16.dp), cardShape(DashboardShapes.SHAPE_PRESET_INHERIT, 16))
        assertEquals(CutCornerShape(8.dp), cardShape(CardShapes.CUT_CORNER, 16))
        assertEquals(CutCornerShape(8.dp), cardShape(3f, 16)) // legacy Diamond
        // A polygon is one stable instance, so recomposition does not rebuild its outline.
        assertSame(cardShape(CardShapes.SLANTED, 16), cardShape(CardShapes.SLANTED, 24))
        assertNotEquals(cardShape(CardShapes.SLANTED, 16), cardShape(CardShapes.CLOVER, 16))
        assertTrue(cardShape(18f, 16) is MorphPolygonShape) // Clover is back
    }

    @Test
    fun `a full-width strip never takes a polygon`() {
        assertEquals(RoundedCornerShape(16.dp), CardShapes.stripShapeFor(CardShapes.CLOVER, 16))
        assertEquals(CutCornerShape(8.dp), CardShapes.stripShapeFor(CardShapes.CUT_CORNER, 16))
        assertTrue(CardShapes.isPolygon(CardShapes.PIXEL_CIRCLE))
        assertFalse(CardShapes.isPolygon(CardShapes.CUT_CORNER))
    }

    @Test
    fun `every offered shape has a name`() {
        val names = CardShapes.options.map { CardShapes.nameRes(it) }
        assertEquals("names must be distinct", names.size, names.toSet().size)
    }

    @Test
    fun `default corner radius matches the PC default`() {
        // remex.core/Models/DashboardProfile.cs: CornerRadius = 16.
        assertEquals(16, CardShapes.DEFAULT_CORNER_RADIUS_DP)
    }

    @Test
    fun `every dashboard category defaults to the rounded rectangle`() {
        DashboardShapes.CardCategory.entries.forEach {
            assertEquals(it.name, CardShapes.ROUNDED_RECTANGLE, DashboardShapes.defaultShapeFor(it), 0f)
        }
    }

    // --- the safe-inset math ---------------------------------------------------------------

    private fun ellipse(n: Int = 360, rx: Float = 3f, ry: Float = 1f): FloatArray =
        FloatArray(n * 2) { i ->
            val a = (i / 2) * 2.0 * PI / n
            if (i % 2 == 0) (rx * cos(a)).toFloat() else (ry * sin(a)).toFloat()
        }

    @Test
    fun `a rectangle leaves the whole card, an ellipse 0_707, a diamond one half`() {
        val square = floatArrayOf(0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f)
        assertEquals(1f, CardShapeSafety.safeFraction(CardShapeSafety.normalize(square)), 0f)

        // Normalising stretches any ellipse to the unit circle-in-square, whatever its aspect.
        assertEquals(0.7071f, CardShapeSafety.safeFraction(CardShapeSafety.normalize(ellipse())), 0.01f)
        assertEquals(0.7071f, CardShapeSafety.safeFraction(CardShapeSafety.normalize(ellipse(rx = 1f, ry = 1f))), 0.01f)

        val diamond = floatArrayOf(0.5f, 0f, 1f, 0.5f, 0.5f, 1f, 0f, 0.5f)
        assertEquals(0.5f, CardShapeSafety.safeFraction(CardShapeSafety.normalize(diamond)), 0.01f)
    }

    @Test
    fun `a notch that cuts into the middle shrinks the safe rectangle`() {
        // A C shape: the unit square with a bite taken out of the centre-left.
        val c = floatArrayOf(
            0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f, 0f, 0.65f, 0.6f, 0.65f, 0.6f, 0.35f, 0f, 0.35f,
        )
        val s = CardShapeSafety.safeFraction(CardShapeSafety.normalize(c))
        assertTrue("notch at the centre must shrink the fraction, got $s", s < 0.5f)
    }

    @Test
    fun `a polygon's safe fraction is the measurement of its own drawn outline`() {
        val outline = CardShapeSafety.normalizedOutline(materialShapesList[CardShapes.SLANTED.toInt()])
        assertEquals(CardShapeSafety.safeFraction(outline), CardShapes.safeFraction(CardShapes.SLANTED), 1e-4f)
    }

    @Test
    fun `safe area sizing - content in the safe fraction grows into a card of content over s`() {
        val s = 0.75f
        // Wrap-content card: 150px of content needs a 200px card, 25px of margin each side.
        assertEquals(200, ShapeSafeAreaMath.outerSize(150, s, 0, Constraints.Infinity))
        assertEquals(25, ShapeSafeAreaMath.margin(200, 150))
        // Fixed card: the content is measured inside s of it and the card stays the fixed size.
        val fixed = Constraints.fixed(400, 300)
        val inner = ShapeSafeAreaMath.innerConstraints(fixed, s)
        assertEquals(300, inner.maxWidth)
        assertEquals(225, inner.maxHeight)
        assertEquals(400, ShapeSafeAreaMath.outerSize(inner.maxWidth, s, fixed.minWidth, fixed.maxWidth))
        assertEquals(300, ShapeSafeAreaMath.outerSize(inner.maxHeight, s, fixed.minHeight, fixed.maxHeight))
    }

    @Test
    fun `safe area sizing - unbounded axes stay unbounded and results respect the card's constraints`() {
        val c = Constraints(minWidth = 0, maxWidth = 500, minHeight = 0, maxHeight = Constraints.Infinity)
        val inner = ShapeSafeAreaMath.innerConstraints(c, 0.5f)
        assertEquals(250, inner.maxWidth)
        assertFalse(inner.hasBoundedHeight)
        // Never larger than the card may be, never smaller than its minimum.
        assertEquals(500, ShapeSafeAreaMath.outerSize(400, 0.5f, 0, 500))
        assertEquals(120, ShapeSafeAreaMath.outerSize(10, 0.5f, 120, 500))
    }

    @Test
    fun `a card that is exactly s of its constraints loses no pixels to rounding`() {
        for (s in listOf(0.7f, 0.75f, 0.82f)) {
            for (w in listOf(99, 160, 333, 1080)) {
                val c = Constraints.fixed(w, w)
                val inner = ShapeSafeAreaMath.innerConstraints(c, s)
                val outer = ShapeSafeAreaMath.outerSize(inner.maxWidth, s, c.minWidth, c.maxWidth)
                assertEquals("s=$s w=$w", w, outer)
                assertTrue(ShapeSafeAreaMath.margin(outer, inner.maxWidth) >= 0)
            }
        }
    }
}
