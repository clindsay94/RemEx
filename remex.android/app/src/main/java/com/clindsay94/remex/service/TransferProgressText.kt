package com.clindsay94.remex.service

import android.content.Context
import com.clindsay94.remex.R
import java.util.Locale

/**
 * Renders [TransferProgressFormat]'s decisions as localized text (RemEx-qmiv).
 *
 * The only layer here that needs a `Context`, kept separate so the unit choice and the rounding —
 * which are decisions, not translations — stay testable without one.
 *
 * **EVERY NUMBER IS FORMATTED AGAINST THE CONFIGURED LOCALE, NOT THE DEFAULT ONE.** A user who sets
 * the app to French on an English phone must see "12,4 Mo/s", and `"%.1f".format(x)` would give them
 * "12.4" because it reads `Locale.getDefault()` rather than the resources the rest of the string
 * came from. Mixing a default-locale number into a resource-locale sentence is the specific bug this
 * indirection exists to prevent.
 */
object TransferProgressText {

    /**
     * The suffix appended to a transfer's progress line, or null when there is nothing to add.
     *
     * NULL RATHER THAN A PLACEHOLDER. Until the estimator has seen enough, the caller keeps showing
     * exactly what it showed before rather than a "calculating" that flickers in and out on every
     * chunk — and a number invented to fill the gap is the thing the parent bead specifically ruled
     * out, because a user plans around it.
     */
    fun progressSuffix(context: Context, rate: TransferRate, eta: TransferEta): String? {
        val rateText = rateText(context, rate) ?: return null
        val etaText = etaText(context, eta) ?: return rateText

        return context.getString(R.string.file_transfer_rate_and_eta, rateText, etaText)
    }

    /** The throughput on its own, or null when it is not known. */
    fun rateText(context: Context, rate: TransferRate): String? {
        if (rate !is TransferRate.Known) return null

        val unitRes = when (rate.unit) {
            TransferRateUnit.BytesPerSecond -> R.string.file_transfer_rate_bytes
            TransferRateUnit.KilobytesPerSecond -> R.string.file_transfer_rate_kilobytes
            TransferRateUnit.MegabytesPerSecond -> R.string.file_transfer_rate_megabytes
            TransferRateUnit.GigabytesPerSecond -> R.string.file_transfer_rate_gigabytes
        }

        // Bytes per second get no decimal: a tenth of a byte is noise, and the figure is already
        // the least interesting of the four.
        val pattern = if (rate.unit == TransferRateUnit.BytesPerSecond) "%.0f" else "%.1f"

        return context.getString(unitRes, String.format(localeOf(context), pattern, rate.value))
    }

    /** The time remaining on its own, or null when it is not known. */
    fun etaText(context: Context, eta: TransferEta): String? = when (eta) {
        TransferEta.Unknown -> null
        TransferEta.Finishing -> context.getString(R.string.file_transfer_eta_finishing)
        is TransferEta.Remaining -> {
            val pluralRes = when (eta.unit) {
                TransferEtaUnit.Seconds -> R.plurals.file_transfer_eta_seconds
                TransferEtaUnit.Minutes -> R.plurals.file_transfer_eta_minutes
                TransferEtaUnit.Hours -> R.plurals.file_transfer_eta_hours
            }

            // getQuantityString, never a plain getString with a count. Polish and Ukrainian take
            // four forms and Hindi takes two with a different boundary, so a single "%d seconds"
            // string would be grammatically wrong in those languages for most values.
            context.resources.getQuantityString(pluralRes, eta.amount, eta.amount)
        }
    }

    /**
     * The locale the app's resources are actually being resolved in.
     *
     * Read from the configuration rather than from `Locale.getDefault()` so an in-app language
     * override — which RemEx supports with live switching — moves the decimal separator too.
     */
    private fun localeOf(context: Context): Locale =
        context.resources.configuration.locales.get(0) ?: Locale.getDefault()
}
