package com.clindsay94.remex.service

import kotlin.math.exp

/**
 * Rolling throughput and time-remaining for a file transfer (RemEx-7uh6).
 *
 * A transfer shows percent and raw bytes today, which makes a 4 GB copy an anxiety exercise. This
 * is the arithmetic half: it returns NUMBERS, never formatted text, because "12.4 MB/s — 38s left"
 * is a localized string and belongs with the notification and queue-row work rather than here.
 *
 * **The estimate is time-weighted, not sample-weighted, and that is the correctness point.** A
 * plain N-sample EWMA assumes samples arrive at a fixed cadence. Transfer progress does not: chunks
 * land irregularly, the app gets backgrounded, the radio sleeps. Weighting by sample count means a
 * burst of ten quick chunks moves the average ten times as far as one chunk over ten seconds,
 * which is backwards — the ten-second gap is the more informative observation. Using
 * `alpha = 1 - exp(-dt / tau)` makes the weight depend on ELAPSED TIME, so the estimate behaves the
 * same whether progress arrives in a trickle or a flood.
 */
class TransferRateEstimator(
    /**
     * How quickly the estimate forgets. Larger is smoother and slower to react.
     *
     * Five seconds is chosen so a momentary stall does not blank the display while a genuine
     * slowdown still shows within a few seconds. It is the number most worth tuning against a real
     * transfer.
     */
    private val timeConstantSeconds: Double = 5.0
) {
    // @Volatile ON ALL THREE, because the writer and the readers are different threads: the
    // transfer engine feeds update() on its IO dispatcher while the notification and the queue
    // row read bytesPerSecond from Main. Holding the estimator in a ConcurrentHashMap safely
    // publishes the REFERENCE and says nothing about later field writes, so without these the
    // reader could see a stale rate indefinitely - and it would work anyway today only by
    // accident, because the caller happens to write a StateFlow afterwards. That accident is one
    // reordered line away from vanishing.
    @Volatile private var lastTimestampMillis: Long? = null
    @Volatile private var lastBytes: Long = 0
    @Volatile private var smoothedBytesPerSecond: Double? = null

    /** The current smoothed throughput, or null before enough is known to say. */
    val bytesPerSecond: Double? get() = smoothedBytesPerSecond

    /**
     * Feeds a progress observation.
     *
     * @param transferredBytes Total bytes transferred so far, not a delta.
     * @param timestampMillis A monotonic clock reading. Pass `SystemClock.elapsedRealtime()`, NOT
     *   wall-clock time: a wall clock can step backwards or jump on an NTP sync, and a negative
     *   interval turns into a negative or infinite rate.
     */
    fun update(transferredBytes: Long, timestampMillis: Long) {
        val previousTime = lastTimestampMillis
        val previousBytes = lastBytes

        lastTimestampMillis = timestampMillis
        lastBytes = transferredBytes

        // First observation establishes a baseline and nothing else - there is no interval to
        // measure a rate over, and inventing one from zero would report an absurd initial speed.
        if (previousTime == null) return

        val elapsedSeconds = (timestampMillis - previousTime) / 1000.0

        // Non-positive interval: two samples in the same millisecond, or a clock that went
        // backwards. Either way there is no rate to compute, and dividing would produce infinity.
        if (elapsedSeconds <= 0.0) return

        val deltaBytes = transferredBytes - previousBytes

        // BYTES GOING BACKWARDS MEANS A RESTART, NOT NEGATIVE THROUGHPUT. A retried transfer
        // resets its counter, and carrying the old average across that boundary would describe a
        // transfer that no longer exists. Drop the estimate and re-establish from here.
        if (deltaBytes < 0) {
            smoothedBytesPerSecond = null
            return
        }

        val instantRate = deltaBytes / elapsedSeconds
        val previousRate = smoothedBytesPerSecond

        smoothedBytesPerSecond = if (previousRate == null) {
            instantRate
        } else {
            val alpha = 1.0 - exp(-elapsedSeconds / timeConstantSeconds)
            previousRate + alpha * (instantRate - previousRate)
        }
    }

    /**
     * Seconds until the transfer finishes, or null when that cannot honestly be said.
     *
     * **NULL IS A REAL ANSWER AND MUST BE SHOWN AS ONE.** It means "not known yet" or "stalled",
     * and the UI has to say something like "calculating" rather than render a placeholder number.
     * A stalled transfer with a rate approaching zero yields an ETA approaching infinity, and
     * showing a user "14 hours remaining" that then jumps to "30 seconds" is worse than showing
     * nothing: it is a number they will plan around.
     *
     * @param totalBytes Total size, or null/0 when the size is unknown - a streamed source has no
     *   length, and no rate can produce an ETA without one.
     */
    fun secondsRemaining(transferredBytes: Long, totalBytes: Long?): Double? {
        val rate = smoothedBytesPerSecond ?: return null
        if (totalBytes == null || totalBytes <= 0L) return null

        val remaining = totalBytes - transferredBytes
        if (remaining <= 0L) return 0.0

        // A rate this low is indistinguishable from stalled, and any ETA derived from it is
        // fiction. One byte per second would put a 4 GB transfer at 136 years.
        if (rate < MinimumMeaningfulBytesPerSecond) return null

        return remaining / rate
    }

    /** Forgets the estimate without discarding the byte baseline - used when a transfer pauses. */
    fun resetRate() {
        smoothedBytesPerSecond = null
        lastTimestampMillis = null
    }

    companion object {
        /**
         * Below this, the transfer is treated as stalled and no ETA is offered.
         *
         * 1 KB/s: slow enough that a genuinely crawling transfer still reports, fast enough that a
         * dead one does not produce a number measured in days.
         */
        const val MinimumMeaningfulBytesPerSecond: Double = 1024.0
    }
}
