package com.clindsay94.remex.ui.screens

/**
 * Pure math for "pan-follow": when the remote desktop is zoomed in, keep the host cursor on
 * screen by panning the view, mimicking the Microsoft Windows App. Edge-triggered — it only
 * nudges the view once the cursor enters an edge deadzone, leaving a comfortable center region
 * where the view does not move. Kept free of Compose/Android types so it is unit-testable.
 */
object PanFollowCalculator {

    /**
     * @param cursorLocalX/Y the host cursor projected into the view box's local pixel space
     *   (the same space [panX]/[panY] live in), WITHOUT edge clamping, so a cursor that has
     *   drifted off the visible region still yields a proportional pan delta.
     * @param panX/panY the current pan offset.
     * @param zoom the current zoom factor (1 = not zoomed).
     * @param imageWidth/imageHeight the view box size (== the laid-out image container).
     * @param marginFraction deadzone size as a fraction of the smaller view dimension.
     * @return the new (panX, panY), clamped to ±imageSize*(zoom-1)/2. Returns the input pan
     *   unchanged when not zoomed, when the image size is non-positive, or when the cursor is
     *   inside the deadzone.
     */
    fun compute(
        cursorLocalX: Float,
        cursorLocalY: Float,
        panX: Float,
        panY: Float,
        zoom: Float,
        imageWidth: Float,
        imageHeight: Float,
        marginFraction: Float = 0.15f,
    ): Pair<Float, Float> {
        if (zoom <= 1f || imageWidth <= 0f || imageHeight <= 0f) return panX to panY

        val margin = minOf(imageWidth, imageHeight) * marginFraction

        var newPanX = panX
        if (cursorLocalX < margin) {
            newPanX = panX + (margin - cursorLocalX)
        } else if (cursorLocalX > imageWidth - margin) {
            newPanX = panX + ((imageWidth - margin) - cursorLocalX)
        }

        var newPanY = panY
        if (cursorLocalY < margin) {
            newPanY = panY + (margin - cursorLocalY)
        } else if (cursorLocalY > imageHeight - margin) {
            newPanY = panY + ((imageHeight - margin) - cursorLocalY)
        }

        val maxPanX = imageWidth * (zoom - 1f) / 2f
        val maxPanY = imageHeight * (zoom - 1f) / 2f
        return newPanX.coerceIn(-maxPanX, maxPanX) to newPanY.coerceIn(-maxPanY, maxPanY)
    }
}
