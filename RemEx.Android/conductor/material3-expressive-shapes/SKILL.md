---
name: material3-expressive-shapes
description: Specialized expert guidance for Material 3 (M3) shape systems, Expressive APIs (1.3.0+), and shape morphing using the androidx.graphics.shapes library. Activate this skill whenever the user mentions "MaterialShapes", "RoundedPolygon", "Morphing", or wants to implement organic, non-standard component shapes in Jetpack Compose. This skill provides deep technical insights into coordinate scaling, path generation via forEachCubic, and smooth interpolation between geometric polygons.
---

# Material 3 Expressive Shapes & Morphing

This skill provides comprehensive guidance on implementing the latest Material 3 Expressive shape capabilities in Jetpack Compose.

## 1. The Material 3 Shape Scale
Standard M3 components use a 5-level shape scale. Each level corresponds to a `CornerBasedShape` (usually `RoundedCornerShape`) defined in the `Shapes` class.

| Scale | Default Radius | Example Components |
| :--- | :--- | :--- |
| Extra Small | 4dp | Tooltips, Small FABs |
| Small | 8dp | Chips, Snackbars |
| Medium | 12dp | Cards, Small Dialogs |
| Large | 16dp | Extended FABs, Nav Drawers |
| Extra Large | 28dp | Large Dialogs, Time Pickers |

**Nuance:** Since M3 1.3.0, "Increased" variants (e.g., `LargeIncreased` at 20dp) are available for more breathing room.

## 2. Expressive Shapes (MaterialShapes)
Expressive shapes move beyond standard rectangles to organic, geometric forms (stars, flowers, squiggles).

### Key Concepts:
- **RoundedPolygon:** The fundamental data structure. It defines vertices and corner rounding (`CornerRounding`).
- **CornerRounding:** Controls both `radius` and `smoothing`. A smoothing of `1.0f` creates a "squircle" transition.
- **Morph:** A class that calculates the mathematical transition between two `RoundedPolygon` instances.

## 3. Implementation in Jetpack Compose
Neither `RoundedPolygon` nor `Morph` implement the Compose `Shape` interface directly. You MUST wrap them.

### Converting Morph/Polygon to Shape
ALWAYS use `forEachCubic` to generate the path for a `Morph` at a specific progress.

```kotlin
class MorphPolygonShape(
    private val morph: Morph,
    private val progress: Float
) : Shape {
    private val matrix = Matrix()

    override fun createOutline(
        size: Size,
        layoutDirection: LayoutDirection,
        density: Density
    ): Outline {
        val composePath = androidx.compose.ui.graphics.Path()
        
        // 1. Generate path from morph state
        var first = true
        morph.forEachCubic(progress) { bezier ->
            if (first) {
                composePath.moveTo(bezier.anchor0X, bezier.anchor0Y)
                first = false
            }
            composePath.cubicTo(
                bezier.control0X, bezier.control0Y,
                bezier.control1X, bezier.control1Y,
                bezier.anchor1X, bezier.anchor1Y
            )
        }
        composePath.close()
        
        // 2. Scale and Translate (Normalized to Component Size)
        // RoundedPolygon is centered at 0,0 with radius 1 by default (bounds -1..1)
        matrix.reset()
        val bounds = morph.calculateBounds()
        val scaleX = size.width / (bounds[2] - bounds[0])
        val scaleY = size.height / (bounds[3] - bounds[1])
        
        matrix.translate(size.width / 2f, size.height / 2f)
        matrix.scale(scaleX, scaleY)
        
        composePath.transform(matrix)
        return Outline.Generic(composePath)
    }
}
```

## 4. Best Practices & Nuances
- **Coordinate Spaces:** Polygons are defined in a normalized space. Always scale them relative to the `Size` provided in `createOutline`.
- **Predefined Catalog:** Prefer constructing a `materialShapesList` of `RoundedPolygon` objects manually if `MaterialShapes` utility is unavailable in the classpath.
- **Vertex Matching:** `Morph` handles vertex matching automatically, but morphing between extremely simple (Triangle) and extremely complex (20-point Star) shapes can look "jagged" during transition.
- **Performance:** Instantiate `Morph(start, end)` once (using `remember`) and only update the `progress` float during animation to avoid expensive re-calculations.
- **Smoothing:** Consistent smoothing values (e.g., `0.5f` or `1.0f`) across the shapes list ensure a more cohesive "fluid" feel during the morph.

## 5. Catalog of Shapes
Common expressive shapes to include in a morphing catalog:
1. `RoundedPolygon.circle(numVertices = 4)` -> Square (rotated)
2. `RoundedPolygon.circle()` -> Circle
3. `RoundedPolygon.circle(numVertices = 3)` -> Triangle
4. `RoundedPolygon.star(numVerticesPerRadius = 5, innerRadius = 0.5f)` -> Star
5. `RoundedPolygon.star(numVerticesPerRadius = 8, innerRadius = 0.8f)` -> Cog
6. `RoundedPolygon.star(numVerticesPerRadius = 12, innerRadius = 0.9f)` -> Certificate
