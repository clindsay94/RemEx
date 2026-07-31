package com.clindsay94.remex.ui.screens

/** A whole-pixel mouse movement ready to send to the host. */
internal data class MouseMove(val x: Int, val y: Int)

/**
 * Turns a stream of per-frame trackpad deltas into at most one message every [throttleMs], without
 * losing any movement (RemEx-3uhp).
 *
 * Two separate problems, and it is worth being clear that only one of them is performance.
 *
 * **Movement was being thrown away.** The trackpad passed `dragAmount.x.toInt()` straight through, so
 * every frame was truncated INDEPENDENTLY. A slow, careful drag moves well under a pixel per frame at
 * 120 Hz — 0.6 px truncates to 0, every frame, and the pointer simply does not move. Accumulating the
 * fraction and only ever emitting whole pixels means a drag of any speed eventually delivers every
 * pixel the user asked for.
 *
 * **And it was doing far too much work.** Each delta became two `JSONObject`s, a `toString`, a JNI
 * marshal and a native deserialize — up to 120 times a second, on the main thread. Now a frame costs
 * two float additions and a comparison; the expensive part happens about 30 times a second.
 *
 * **THE THROTTLE GATES SENDING, NOT ACCUMULATING, and that distinction is the whole design.** The
 * Remote Desktop screen throttles by *dropping* the callback, which is correct there because it is
 * throttling stylus HOVER — absolute positions, where only the newest matters and a dropped one costs
 * nothing. These are relative deltas. Drop one and that movement is gone for good and the pointer
 * drifts away from where the user's finger says it should be. So every delta is accumulated and the
 * clock only decides when the accumulated total is flushed.
 *
 * Pure, with the clock passed in, so the accounting can be tested on the JVM rather than by dragging
 * a finger across a phone and forming an opinion.
 */
internal class MouseMoveThrottle(private val throttleMs: Long = DEFAULT_THROTTLE_MS) {

    private var accumulatedX = 0f
    private var accumulatedY = 0f

    /** Null until the first send, so the very first movement is never delayed. */
    private var lastSentAtMs: Long? = null

    /**
     * Accumulates one frame's delta and returns the movement to send, or null to send nothing yet.
     */
    fun onDelta(deltaX: Float, deltaY: Float, nowMs: Long): MouseMove? {
        accumulatedX += deltaX
        accumulatedY += deltaY

        val due = lastSentAtMs?.let { nowMs - it >= throttleMs } ?: true
        return if (due) takeAccumulated(nowMs) else null
    }

    /**
     * Emits whatever is still accumulated, ignoring the throttle.
     *
     * Called when a drag ends. Without it the last fraction of a gesture — up to one throttle
     * interval of real movement — sits in the accumulator until the user happens to drag again,
     * which shows up as the pointer stopping slightly short every single time.
     */
    fun flush(nowMs: Long): MouseMove? {
        val move = takeAccumulated(nowMs)
        // Clear the deadline so the NEXT gesture starts responsive. Without this a drag beginning
        // within one interval of the previous drag's end has its first frame withheld — which is
        // precisely the start-of-gesture lag the immediate-first-send exists to avoid, and
        // flick-flick-flick repositioning makes that the common case rather than a rare one.
        lastSentAtMs = null
        return move
    }

    private fun takeAccumulated(nowMs: Long): MouseMove? {
        val x = accumulatedX.toInt()
        val y = accumulatedY.toInt()
        if (x == 0 && y == 0) {
            // Nothing whole to send. Deliberately does NOT stamp the clock: a gesture too slow to
            // produce a pixel must not have its next chance pushed a further interval away.
            return null
        }

        // Subtract rather than zero, so the sub-pixel remainder survives into the next send. Zeroing
        // here would reintroduce the truncation bug one throttle interval at a time.
        accumulatedX -= x
        accumulatedY -= y
        lastSentAtMs = nowMs
        return MouseMove(x, y)
    }

    internal companion object {
        /** ~30 Hz, matching the Remote Desktop screen's own move throttle. */
        const val DEFAULT_THROTTLE_MS = 33L
    }
}
