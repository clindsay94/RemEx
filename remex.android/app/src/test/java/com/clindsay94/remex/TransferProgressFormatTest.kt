package com.clindsay94.remex

import com.clindsay94.remex.service.TransferEta
import com.clindsay94.remex.service.TransferEtaUnit
import com.clindsay94.remex.service.TransferProgressFormat
import com.clindsay94.remex.service.TransferRate
import com.clindsay94.remex.service.TransferRateUnit
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Tests for the unit-and-rounding decisions behind "12.4 MB/s · 38 seconds left" (RemEx-qmiv).
 *
 * The parent bead's rule is that a wrong number here is worse than no number, because the user
 * plans around it. So the cases that matter are the ones where an estimate must REFUSE to be shown:
 * stalled, unknown total size, and absurdly long.
 */
class TransferProgressFormatTest {

    private fun rateOf(bytesPerSecond: Double) =
        TransferProgressFormat.rate(bytesPerSecond) as TransferRate.Known

    private fun etaOf(seconds: Double) =
        TransferProgressFormat.eta(seconds) as TransferEta.Remaining

    @Test
    fun `each unit is chosen at its own boundary`() {
        assertEquals(TransferRateUnit.BytesPerSecond, rateOf(1023.0).unit)
        assertEquals(TransferRateUnit.KilobytesPerSecond, rateOf(1024.0).unit)
        assertEquals(TransferRateUnit.KilobytesPerSecond, rateOf(1024.0 * 1024 - 1).unit)
        assertEquals(TransferRateUnit.MegabytesPerSecond, rateOf(1024.0 * 1024).unit)
        assertEquals(TransferRateUnit.GigabytesPerSecond, rateOf(1024.0 * 1024 * 1024).unit)
    }

    @Test
    fun `the value is scaled into the unit that was chosen`() {
        // The unit and the number have to agree. Reporting 13002342 with the MB unit but without
        // dividing would read as "13002342.0 MB/s", which is not a plausible misreading - it is a
        // number the user cannot make any sense of at all.
        val rate = rateOf(13_002_342.0)

        assertEquals(TransferRateUnit.MegabytesPerSecond, rate.unit)
        assertEquals(12.4, rate.value, 0.05)
    }

    @Test
    fun `1024-based, matching the byte formatting already in this package`() {
        // Not an SI argument: two figures on the same notification computed on different bases would
        // not add up, and the existing formatBytes helpers are 1024-based.
        assertEquals(1.0, rateOf(1024.0).value, 0.0001)
        assertEquals(1.0, rateOf(1_048_576.0).value, 0.0001)
    }

    @Test
    fun `an unknown rate is unknown, not zero`() {
        // "0 B/s" is a claim about the transfer; "we do not know yet" is a claim about us. The
        // estimator returns null for its first observation, which is every transfer's first moment.
        assertTrue(TransferProgressFormat.rate(null) is TransferRate.Unknown)
    }

    @Test
    fun `a rate that is not a finite number is refused`() {
        // It arrives from a division. NaN rendered as "NaN MB/s" would be a defect the user reads in
        // the one place they are already anxious.
        assertTrue(TransferProgressFormat.rate(Double.NaN) is TransferRate.Unknown)
        assertTrue(TransferProgressFormat.rate(Double.POSITIVE_INFINITY) is TransferRate.Unknown)
        assertTrue(TransferProgressFormat.rate(-1.0) is TransferRate.Unknown)
    }

    @Test
    fun `a genuine zero rate is still reported as zero`() {
        // Distinct from unknown, and reachable: the estimator produces 0.0 for an interval where
        // nothing arrived. That is a fact worth showing, and it is what a stall looks like.
        val rate = TransferProgressFormat.rate(0.0)

        assertTrue(rate is TransferRate.Known)
        assertEquals(0.0, (rate as TransferRate.Known).value, 0.0001)
    }

    @Test
    fun `an eta is rounded UP so it never claims less time than remains`() {
        // Rounding to nearest lets the display reach zero while the transfer is still running, which
        // reads as a stuck progress bar rather than as a rounding artefact.
        assertEquals(4, etaOf(3.1).amount)
        assertEquals(4, etaOf(3.9).amount)
        assertEquals(3, etaOf(3.0).amount)
    }

    @Test
    fun `rounding happens BEFORE bucketing, so 59 point 6 seconds is one minute`() {
        // THE SUBTLETY THIS TEST EXISTS FOR. Bucket first and 59.6 is under a minute, so it renders
        // as "60 seconds left" - a unit nobody writes. Round first and it is a minute.
        val eta = etaOf(59.6)

        assertEquals(TransferEtaUnit.Minutes, eta.unit)
        assertEquals(1, eta.amount)
    }

    @Test
    fun `the same trap at the minute-to-hour boundary`() {
        val eta = etaOf(3599.5)

        assertEquals(TransferEtaUnit.Hours, eta.unit)
        assertEquals(1, eta.amount)
    }

    @Test
    fun `each duration unit is chosen at its own boundary`() {
        assertEquals(TransferEtaUnit.Seconds, etaOf(59.0).unit)
        assertEquals(TransferEtaUnit.Minutes, etaOf(60.0).unit)
        assertEquals(TransferEtaUnit.Minutes, etaOf(3540.0).unit)
        assertEquals(TransferEtaUnit.Hours, etaOf(3600.0).unit)
    }

    @Test
    fun `under a second is finishing, not zero seconds`() {
        // "0 seconds left" reads as a transfer that has already completed and then visibly has not.
        assertTrue(TransferProgressFormat.eta(0.0) is TransferEta.Finishing)
        assertTrue(TransferProgressFormat.eta(0.9) is TransferEta.Finishing)
        assertTrue(TransferProgressFormat.eta(1.0) is TransferEta.Remaining)
    }

    @Test
    fun `an unknown eta stays unknown`() {
        // Null is the estimator's answer for stalled, for an unknown total size, and for before
        // enough is known - three different situations that share one honest response.
        assertTrue(TransferProgressFormat.eta(null) is TransferEta.Unknown)
    }

    @Test
    fun `an absurdly long eta is refused rather than displayed`() {
        // The estimator already refuses to divide by a rate under 1 KB/s, but a very large file at a
        // rate just above that floor still yields days. "3 days remaining" that becomes "2 minutes"
        // once the radio wakes up is exactly the number the parent bead ruled out - the user plans
        // around it.
        assertTrue(TransferProgressFormat.eta(48.0 * 3600) is TransferEta.Unknown)
        assertTrue(TransferProgressFormat.eta(TransferProgressFormat.MaximumMeaningfulSeconds) is TransferEta.Remaining)
        assertTrue(TransferProgressFormat.eta(TransferProgressFormat.MaximumMeaningfulSeconds + 1) is TransferEta.Unknown)
    }

    @Test
    fun `the largest figure the display can show is 23 hours, not 24`() {
        // "24 hours" is a quantity nobody writes - at that size a person says "a day" - and a
        // 24-hour cap would put it exactly on the refusal boundary, so an estimate jittering across
        // it would alternate between a number and nothing.
        val longest = TransferProgressFormat.eta(TransferProgressFormat.MaximumMeaningfulSeconds)
            as TransferEta.Remaining

        assertEquals(TransferEtaUnit.Hours, longest.unit)
        assertEquals(23, longest.amount)
    }

    @Test
    fun `an eta that is not a finite number is refused`() {
        assertTrue(TransferProgressFormat.eta(Double.NaN) is TransferEta.Unknown)
        assertTrue(TransferProgressFormat.eta(Double.POSITIVE_INFINITY) is TransferEta.Unknown)
        assertTrue(TransferProgressFormat.eta(-5.0) is TransferEta.Unknown)
    }

    @Test
    fun `an eta never rounds up into a unit it does not belong to`() {
        // Swept, and the STEP is the point. Review caught the first version multiplying by 1.05,
        // which walks 57.41 -> 60.28 and 3456 -> 3629: it stepped straight over every boundary it
        // claimed to guard, and passed with the bucket-first bug installed. A geometric sweep is the
        // wrong instrument for a test about boundaries.
        //
        // The failure it exists to catch is "90 minutes left" where "2 hours" was meant.
        val samples = buildList {
            addAll(1..120)
            addAll(listOf(3538, 3539, 3540, 3541, 3598, 3599, 3600, 3601))
            addAll(listOf(82_798, 82_799, 82_800))
            var s = 121
            while (s < 82_800) { add(s); s += 617 }
        }

        for (seconds in samples) {
            for (offset in listOf(0.0, 0.4, 0.5, 0.9)) {
                val value = seconds + offset
                if (value > TransferProgressFormat.MaximumMeaningfulSeconds) continue

                val eta = TransferProgressFormat.eta(value) as TransferEta.Remaining
                val limit = when (eta.unit) {
                    TransferEtaUnit.Seconds -> 59
                    TransferEtaUnit.Minutes -> 59
                    TransferEtaUnit.Hours -> 23
                }

                // Never zero either: a zero-amount Remaining renders through the plural resource as
                // "0 seconds left", the exact string the Finishing state exists to avoid, arriving
                // through a different door.
                assertTrue("eta($value) = ${eta.amount} ${eta.unit}", eta.amount in 1..limit)
            }
        }
    }

    @Test
    fun `an eta is never larger than the time that actually remains`() {
        // The property behind the rounding rule, checked directly rather than inferred from it: the
        // figure shown, converted back to seconds, must cover the real remainder. A display that
        // says less time than remains counts to zero and then waits, which reads as a hang.
        //
        // Ceiling makes it an over-estimate by less than one unit, so this also bounds how far.
        var seconds = 1.0
        while (seconds <= TransferProgressFormat.MaximumMeaningfulSeconds) {
            val eta = TransferProgressFormat.eta(seconds) as TransferEta.Remaining
            val unitSeconds = when (eta.unit) {
                TransferEtaUnit.Seconds -> 1
                TransferEtaUnit.Minutes -> 60
                TransferEtaUnit.Hours -> 3600
            }
            val shown = eta.amount.toLong() * unitSeconds

            assertTrue("eta($seconds) showed ${eta.amount} ${eta.unit}", shown >= seconds)
            assertTrue("eta($seconds) overshot", shown < seconds + unitSeconds)

            seconds *= 1.05
        }
    }
}
