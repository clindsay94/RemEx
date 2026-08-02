package com.clindsay94.remex

import com.clindsay94.remex.service.TransferRateEstimator
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the throughput and ETA arithmetic (RemEx-7uh6).
 *
 * The failure that matters is not an imprecise number - it is a CONFIDENT WRONG one. A user plans
 * around "38 seconds left"; they do not plan around "calculating". So most of these tests are about
 * when the estimator must decline to answer.
 */
class TransferRateEstimatorTest {

    private val MB = 1024L * 1024L

    @Test
    fun `one observation is not enough to claim a speed`() {
        // There is no interval to measure over. Inventing a rate from a zero baseline would report
        // an absurd initial speed on the very first frame the user sees.
        val estimator = TransferRateEstimator()

        estimator.update(transferredBytes = 5 * MB, timestampMillis = 1_000)

        assertNull(estimator.smoothedRate)
        assertNull(estimator.secondsRemaining(5 * MB, 100 * MB))
    }

    @Test
    fun `a steady transfer converges on its real rate`() {
        // 10 MB every second. The first interval seeds the estimate exactly; later ones should not
        // drag it away from the truth.
        val estimator = TransferRateEstimator()
        var bytes = 0L

        for (second in 0..10) {
            estimator.update(bytes, second * 1000L)
            bytes += 10 * MB
        }

        val rate = estimator.smoothedRate
        assertNotNull(rate)
        assertEquals(10.0 * MB, rate!!, 0.5 * MB)
    }

    @Test
    fun `the estimate is weighted by elapsed time, not by how many samples arrived`() {
        // THE CORRECTNESS POINT, and the test had to be rebuilt to actually prove it. The first
        // version fed two estimators the same STEADY rate at different cadences - but a steady rate
        // produces identical instant rates either way, so every alpha converges to the same answer
        // and a fixed-alpha mutant passed it. It proved nothing.
        //
        // Discriminating requires instant rates that DIFFER, so that which one dominates is
        // visible: a fast burst (10 samples of 1 MB, 10 ms apart = 100 MB/s) followed by one long
        // slow interval (1 MB over 10 s = 0.1 MB/s).
        //
        // Time weighting says the 10-second observation swamps 100 ms of burst, so the estimate
        // must land near the SLOW rate. Sample weighting counts that long interval as just one more
        // sample and barely moves - which is backwards, because the long interval is the more
        // informative observation about what the link is doing now.
        val estimator = TransferRateEstimator()
        var bytes = 0L
        var clock = 0L

        estimator.update(bytes, clock)
        repeat(10) {
            clock += 10
            bytes += MB
            estimator.update(bytes, clock)
        }

        val afterBurst = estimator.smoothedRate!!
        assertTrue("the burst should read fast, got $afterBurst", afterBurst > 50.0 * MB)

        clock += 10_000
        bytes += MB
        estimator.update(bytes, clock)

        val afterSlowInterval = estimator.smoothedRate!!

        // WHERE THIS BOUND COMES FROM, since a threshold picked by feel is how a test ends up
        // unable to fail. A 10s interval against a 5s time constant gives alpha = 1 - e^-2 = 0.86,
        // so the correct implementation lands at 100 + 0.86 * (0.1 - 100) = ~13.6 MB/s. A
        // sample-count EWMA with a typical fixed alpha of 0.2 lands at ~80 MB/s. Quartering the
        // burst rate puts the bound at 25 MB/s: comfortably above the real answer, far below the
        // mutant. Measured both ways rather than guessed - a first attempt used a tenth, which the
        // CORRECT implementation failed.
        assertTrue(
            "a 10s slow interval must dominate a 100ms burst; got $afterSlowInterval " +
                "after $afterBurst",
            afterSlowInterval < afterBurst / 4.0
        )
    }

    @Test
    fun `a stalled transfer offers no ETA rather than a fictional one`() {
        // THE TEST THIS CLASS EXISTS FOR. A rate approaching zero yields an ETA approaching
        // infinity. Showing "14 hours remaining" that later jumps to "30 seconds" is worse than
        // showing nothing, because the user plans around the number.
        val estimator = TransferRateEstimator()

        estimator.update(0, 0)
        estimator.update(10 * MB, 1_000)
        assertNotNull(estimator.secondsRemaining(10 * MB, 100 * MB))

        // Now nothing moves for a minute.
        for (second in 2..62) {
            estimator.update(10 * MB, second * 1000L)
        }

        assertNull("a stalled transfer must not produce an ETA",
            estimator.secondsRemaining(10 * MB, 100 * MB))
    }

    @Test
    fun `bytes going backwards resets the estimate rather than reporting negative throughput`() {
        // A retried transfer restarts its counter. Carrying the old average across that boundary
        // would describe a transfer that no longer exists, and the raw delta is negative.
        val estimator = TransferRateEstimator()

        estimator.update(0, 0)
        estimator.update(50 * MB, 1_000)
        assertNotNull(estimator.smoothedRate)

        estimator.update(0, 2_000)

        assertNull("a restart must clear the estimate", estimator.smoothedRate)
    }

    @Test
    fun `two samples in the same millisecond do not divide by zero`() {
        val estimator = TransferRateEstimator()

        estimator.update(0, 1_000)
        estimator.update(5 * MB, 1_000)

        assertNull(estimator.smoothedRate)
    }

    @Test
    fun `a clock that steps backwards is ignored rather than producing a negative rate`() {
        // The reason update() documents elapsedRealtime rather than wall time. If a caller passes
        // wall time anyway, an NTP correction must not turn into a negative or infinite rate.
        val estimator = TransferRateEstimator()

        estimator.update(0, 10_000)
        estimator.update(10 * MB, 11_000)
        val healthy = estimator.smoothedRate

        estimator.update(20 * MB, 9_000)

        assertEquals("a backwards clock should leave the estimate untouched",
            healthy!!, estimator.smoothedRate!!, 0.001)
    }

    @Test
    fun `an unknown total size yields no ETA`() {
        // A streamed source has no length, and no rate can produce a time remaining without one.
        val estimator = TransferRateEstimator()
        estimator.update(0, 0)
        estimator.update(10 * MB, 1_000)

        assertNull(estimator.secondsRemaining(10 * MB, null))
        assertNull(estimator.secondsRemaining(10 * MB, 0))
    }

    @Test
    fun `a finished transfer reports zero remaining rather than a negative`() {
        val estimator = TransferRateEstimator()
        estimator.update(0, 0)
        estimator.update(100 * MB, 1_000)

        assertEquals(0.0, estimator.secondsRemaining(100 * MB, 100 * MB)!!, 0.0)
        assertEquals("over-transfer must not go negative",
            0.0, estimator.secondsRemaining(110 * MB, 100 * MB)!!, 0.0)
    }

    @Test
    fun `the ETA is roughly right for a steady transfer`() {
        // The counterpart to all the refusals: when it does answer, the answer has to be usable.
        // 10 MB/s with 100 MB left should be about 10 seconds.
        val estimator = TransferRateEstimator()
        var bytes = 0L
        for (second in 0..10) {
            estimator.update(bytes, second * 1000L)
            bytes += 10 * MB
        }

        val eta = estimator.secondsRemaining(transferredBytes = 100 * MB, totalBytes = 200 * MB)

        assertNotNull(eta)
        assertTrue("expected roughly 10s, got $eta", eta!! in 8.0..13.0)
    }

    @Test
    fun `resetRate forgets the speed without corrupting the next interval`() {
        // Used when a transfer pauses. The next observation after a resume must establish a fresh
        // baseline rather than measuring across the pause, which would read as a long stall.
        val estimator = TransferRateEstimator()
        estimator.update(0, 0)
        estimator.update(10 * MB, 1_000)

        estimator.resetRate()
        assertNull(estimator.smoothedRate)

        // Resume an hour later; the gap must not be treated as an interval.
        estimator.update(10 * MB, 3_600_000)
        assertNull(estimator.smoothedRate)

        estimator.update(20 * MB, 3_601_000)
        assertEquals(10.0 * MB, estimator.smoothedRate!!, 0.5 * MB)
    }

    // ── Staleness: a stall is silence, and silence cannot call update() (RemEx-8c3v) ──────────

    /** Ten megabytes a second, established over two samples ending at t = 1000. */
    private fun movingEstimator(): TransferRateEstimator {
        val estimator = TransferRateEstimator()
        estimator.update(0, 0)
        estimator.update(10 * MB, 1_000)
        return estimator
    }

    @Test
    fun `an estimate stops being offered once nothing has arrived for four time constants`() {
        // THE DEFECT THIS CLOSES. The estimator only advances when a frame arrives, so on a real
        // stall the last figure sat on screen indefinitely while the ETA silently became fiction.
        // The 1 KB/s floor cannot catch it: that fires as the average DECAYS, and decay needs
        // samples - which is exactly what a stall stops delivering.
        val estimator = movingEstimator()
        val staleAfterMillis = (TransferRateEstimator.StaleTimeConstants * 5.0 * 1000).toLong()

        assertNotNull(estimator.bytesPerSecondAt(1_000 + staleAfterMillis))
        assertNull(
            "a transfer silent for four time constants must stop claiming a speed",
            estimator.bytesPerSecondAt(1_000 + staleAfterMillis + 1),
        )
    }

    @Test
    fun `an ordinary gap between chunks does not blank the display`() {
        // The other direction of error. Chunks land irregularly and the app gets backgrounded, so a
        // threshold tight enough to flicker would train the user to ignore the figure entirely.
        val estimator = movingEstimator()

        assertNotNull(estimator.bytesPerSecondAt(1_000 + 5_000))
        assertNotNull(estimator.bytesPerSecondAt(1_000 + 15_000))
    }

    @Test
    fun `speed and time-remaining go blank at the same moment`() {
        // Two figures that disagreed about whether the transfer was still alive would be worse than
        // either alone - a speed with no ETA reads as "still going, cannot say how long".
        val estimator = movingEstimator()
        val stale = 1_000L + (TransferRateEstimator.StaleTimeConstants * 5.0 * 1000).toLong() + 1

        assertNotNull(estimator.bytesPerSecondAt(1_500))
        assertNotNull(estimator.secondsRemainingAt(10 * MB, 100 * MB, 1_500))

        assertNull(estimator.bytesPerSecondAt(stale))
        assertNull(estimator.secondsRemainingAt(10 * MB, 100 * MB, stale))
    }

    @Test
    fun `staleness is measured from the last sample, not from when the transfer began`() {
        // A long transfer that is still moving must never age out. Measuring from the start would
        // blank the display on every transfer that ran longer than the threshold.
        val estimator = TransferRateEstimator()
        var bytes = 0L
        for (second in 0..120) {
            estimator.update(bytes, second * 1000L)
            bytes += 10 * MB
        }

        assertNotNull(estimator.bytesPerSecondAt(120_000))
    }

    @Test
    fun `the arithmetic still holds the figure the display refuses to show`() {
        // The split is deliberate: the average is CORRECT, it is simply about a moment that has
        // passed. Clearing it would also destroy the baseline a resumed transfer smooths from.
        val estimator = movingEstimator()
        val stale = 1_000L + (TransferRateEstimator.StaleTimeConstants * 5.0 * 1000).toLong() + 1

        assertNull(estimator.bytesPerSecondAt(stale))
        assertNotNull(estimator.smoothedRate)
    }

    @Test
    fun `a fresh sample makes the estimate offerable again`() {
        // Recovery has to work without a reset: the radio comes back, one frame lands, and the
        // display must resume rather than stay blank until the transfer restarts.
        val estimator = movingEstimator()
        val stale = 1_000L + (TransferRateEstimator.StaleTimeConstants * 5.0 * 1000).toLong() + 1
        assertNull(estimator.bytesPerSecondAt(stale))

        estimator.update(20 * MB, stale)

        assertNotNull(estimator.bytesPerSecondAt(stale))
    }
}
