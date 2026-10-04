package com.clindsay94.remex.ui.theme

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.layout.layout
import androidx.compose.ui.unit.Constraints
import androidx.graphics.shapes.Morph
import kotlin.math.roundToInt

/**
 * The sizing arithmetic behind [shapeSafeArea], kept free of Compose so it can be unit tested.
 *
 * A card whose shape leaves a fraction [s] of its width and height usable pads its content by
 * `(1 - s) / 2` of the CARD's size on each side. The card's size is not known before its content is
 * measured, so the content is measured in the safe fraction of what the card may be, and the card
 * is then sized to `content / s`.
 */
internal object ShapeSafeAreaMath {
    /** Constraints for the content: the safe fraction of what the card may be. */
    fun innerConstraints(c: Constraints, s: Float): Constraints = Constraints(
        minWidth = (c.minWidth * s).toInt(),
        maxWidth = if (c.hasBoundedWidth) (c.maxWidth * s).toInt() else Constraints.Infinity,
        minHeight = (c.minHeight * s).toInt(),
        maxHeight = if (c.hasBoundedHeight) (c.maxHeight * s).toInt() else Constraints.Infinity,
    )

    /** The card's size along one axis for content of [inner] px, clamped to the card's constraints. */
    fun outerSize(inner: Int, s: Float, min: Int, max: Int): Int = (inner / s).roundToInt().coerceIn(min, max)

    /** The empty margin on one side of the card when the card is [outer] px and content [inner] px. */
    fun margin(outer: Int, inner: Int): Int = (outer - inner) / 2
}

/**
 * Confines a card's content to the safe rectangle of its [shapeIndex] (RemEx-pp4cm.14), so a Slanted
 * or Clover card cannot clip its label or value at an edge. Put it on the content, inside the clip.
 * Rounded rectangle and cut corner use the whole card (they are padded by [cardInnerPadding]).
 */
fun Modifier.shapeSafeArea(shapeIndex: Float): Modifier {
    val s = CardShapes.safeFraction(shapeIndex)
    if (s >= 1f) return this
    return layout { measurable, constraints ->
        val placeable = measurable.measure(ShapeSafeAreaMath.innerConstraints(constraints, s))
        val w = ShapeSafeAreaMath.outerSize(placeable.width, s, constraints.minWidth, constraints.maxWidth)
        val h = ShapeSafeAreaMath.outerSize(placeable.height, s, constraints.minHeight, constraints.maxHeight)
        layout(w, h) {
            placeable.place(ShapeSafeAreaMath.margin(w, placeable.width), ShapeSafeAreaMath.margin(h, placeable.height))
        }
    }
}

/** LRU-free cache: there are only [CardShapes.polygonOptions].size static polygon shapes. */
private val polygonShapes = java.util.concurrent.ConcurrentHashMap<Int, MorphPolygonShape>()

/**
 * A static polygon from [materialShapesList] as a card shape. The same instance is returned for the
 * same index, so Compose sees an unchanged shape across recompositions and skips the outline rebuild.
 */
internal fun polygonShape(materialIndex: Int): MorphPolygonShape =
    polygonShapes.getOrPut(materialIndex) {
        val polygon = materialShapesList[materialIndex]
        MorphPolygonShape(Morph(polygon, polygon), 0f)
    }

/**
 * A small live swatch of a card shape for the pickers: the real shape at a card-like 40x28dp, with
 * its measured safe rectangle drawn inside so the choice shows how much room content gets.
 */
@Composable
fun ShapeSwatch(
    shapeIndex: Float,
    cornerRadiusDp: Int,
    shapeColor: Color,
    contentColor: Color,
    modifier: Modifier = Modifier,
    width: Dp = 40.dp,
    height: Dp = 28.dp,
) {
    val safe = CardShapes.safeFraction(shapeIndex)
    Box(
        modifier = modifier
            .size(width, height)
            .clip(cardShape(shapeIndex, (cornerRadiusDp * height.value / 100f).toInt().coerceAtLeast(3)))
            .background(shapeColor),
        contentAlignment = Alignment.Center,
    ) {
        Box(
            Modifier
                .size(width * safe - 10.dp, height * safe - 10.dp)
                .clip(RoundedCornerShape(2.dp))
                .background(contentColor.copy(alpha = 0.35f))
        )
    }
}