package com.clindsay94.remex.service

import kotlin.math.ceil

/** Which unit a throughput figure is expressed in. */
enum class TransferRateUnit {
    BytesPerSecond,
    KilobytesPerSecond,
    MegabytesPerSecond,
    GigabytesPerSecond,
}

/** Which unit a time-remaining figure is expressed in. */
enum class TransferEtaUnit {
    Seconds,
    Minutes,
    Hours,
}

/**
 * A throughput figure, or the honest absence of one.
 *
 * [Unknown] is a first-class state rather than a zero, because "0 B/s" is a claim about the
 * transfer and "we do not know yet" is a claim about us.
 */
sealed interface TransferRate {
    data object Unknown : TransferRate

    data class Known(val value: Double, val unit: TransferRateUnit) : TransferRate
}

/**
 * A time-remaining figure.
 *
 * Three states, not two. [Finishing] exists because rendering the last fraction of a second as
 * "0 seconds left" reads as a transfer that has already completed and then visibly does not.
 */
sealed interface TransferEta {
    data object Unknown : TransferEta

    data object Finishing : TransferEta

    data class Remaining(val amount: Int, val unit: TransferEtaUnit) : TransferEta
}

/**
 * Turns [TransferRateEstimator]'s numbers into the shape a localized string needs (RemEx-qmiv).
 *
 * **STILL NOT TEXT, AND THAT IS THE POINT.** The estimator deliberately returns numbers because
 * "12.4 MB/s — 38s left" is a localized string. This layer picks the UNIT and the ROUNDING, which
 * are decisions rather than translations, and stops there. The unit symbol, the spacing, the
 * decimal separator and the plural form all belong to the resource layer, which is why nothing here
 * touches a `Context` and everything here is testable without one.
 *
 * The split matters for more than tidiness: byte-rate symbols are not universal (Ukrainian writes
 * МБ/с) and second/minute/hour words take three or four plural forms in Polish and Ukrainian, so
 * any function that returned a finished string from here would have to be wrong in at least one of
 * the nine languages this app ships in.
 */
object TransferProgressFormat {

    /**
     * Picks the unit for a throughput figure.
     *
     * @param bytesPerSecond as reported by [TransferRateEstimator.bytesPerSecond]; null means the
     *   estimator has nothing to say yet.
     */
    fun rate(bytesPerSecond: Double?): TransferRate {
        // NOT FINITE MEANS NOT KNOWN. The estimator guards its own arithmetic, but this is fed from
        // a division and a NaN rendered as "NaN MB/s" would be a defect visible to the user in the
        // one place they are already anxious.
        if (bytesPerSecond == null || !bytesPerSecond.isFinite() || bytesPerSecond < 0.0) {
            return TransferRate.Unknown
        }

        return when {
            bytesPerSecond >= BytesPerGigabyte ->
                TransferRate.Known(bytesPerSecond / BytesPerGigabyte, TransferRateUnit.GigabytesPerSecond)
            bytesPerSecond >= BytesPerMegabyte ->
                TransferRate.Known(bytesPerSecond / BytesPerMegabyte, TransferRateUnit.MegabytesPerSecond)
            bytesPerSecond >= BytesPerKilobyte ->
                TransferRate.Known(bytesPerSecond / BytesPerKilobyte, TransferRateUnit.KilobytesPerSecond)
            else -> TransferRate.Known(bytesPerSecond, TransferRateUnit.BytesPerSecond)
        }
    }

    /**
     * Picks the unit and the rounded amount for a time-remaining figure.
     *
     * @param secondsRemaining as reported by [TransferRateEstimator.secondsRemaining]; null means
     *   stalled, unknown total size, or not enough observed yet.
     */
    fun eta(secondsRemaining: Double?): TransferEta {
        if (secondsRemaining == null || !secondsRemaining.isFinite() || secondsRemaining < 0.0) {
            return TransferEta.Unknown
        }

        // Under a second there is no honest number to show: "0 seconds left" reads as finished and
        // "1 second" is a rounding-up of something smaller. Say what is actually true instead.
        if (secondsRemaining < 1.0) return TransferEta.Finishing

        // AN ABSURD ETA IS NOT AN ETA. The estimator already refuses to divide by a rate under
        // 1 KB/s, but a very large file at a rate just above that floor still yields days. A number
        // that large is fiction the user would nevertheless plan around, and the same reasoning that
        // produced the estimator's floor applies to its output.
        if (secondsRemaining > MaximumMeaningfulSeconds) return TransferEta.Unknown

        // ROUNDED UP, THEN BUCKETED - IN THAT ORDER, and the order is the whole subtlety. Bucketing
        // first gives "60 seconds left" for 59.6s, because the value is under a minute before it is
        // rounded and over one afterwards. Rounding up rather than to nearest keeps the figure from
        // ever claiming less time than remains, so the display never counts to zero and then waits.
        val seconds = ceil(secondsRemaining).toInt()
        if (seconds < SecondsPerMinute) return TransferEta.Remaining(seconds, TransferEtaUnit.Seconds)

        val minutes = ceil(secondsRemaining / SecondsPerMinute).toInt()
        if (minutes < MinutesPerHour) return TransferEta.Remaining(minutes, TransferEtaUnit.Minutes)

        return TransferEta.Remaining(
            ceil(secondsRemaining / SecondsPerHour).toInt(),
            TransferEtaUnit.Hours,
        )
    }

    /**
     * Beyond this an estimate stops being information.
     *
     * Long enough that a genuinely huge overnight transfer still reports, short enough that the
     * display never offers a figure in days off a rate barely above the estimator's stall floor.
     *
     * TWENTY-THREE HOURS RATHER THAN TWENTY-FOUR, so the largest figure the display can ever show
     * is "23 hours left". A 24-hour cap makes "24 hours" the top bucket, which is a quantity nobody
     * writes — at that size a person says "a day" — and it sits exactly on the refusal boundary, so
     * an estimate jittering across it alternates between a number and nothing.
     *
     * The flicker is not eliminated, only moved somewhere a transfer is unlikely to sit: removing it
     * entirely needs hysteresis, and that means giving a pure function memory for a case that
     * requires hovering at a specific threshold for minutes.
     */
    const val MaximumMeaningfulSeconds: Double = 23.0 * 60.0 * 60.0

    // 1024-based, matching the existing formatBytes helpers in this package. Consistency matters
    // more than the SI argument here: two figures on the same notification computed on different
    // bases would not add up.
    private const val BytesPerKilobyte = 1024.0
    private const val BytesPerMegabyte = 1024.0 * 1024.0
    private const val BytesPerGigabyte = 1024.0 * 1024.0 * 1024.0

    private const val SecondsPerMinute = 60
    private const val MinutesPerHour = 60
    private const val SecondsPerHour = 3600
}
