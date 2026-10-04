package com.clindsay94.remex.ui.screens.sensors

import com.clindsay94.remex.data.SensorAlertDirection
import com.clindsay94.remex.data.SensorAlertRule
import com.clindsay94.remex.data.SensorAlertSeverity
import com.clindsay94.remex.data.SensorAlerts
import com.clindsay94.remex.data.SensorAlertsState
import java.util.Locale

/**
 * What the "Alert me..." sheet holds while it is open (RemEx-pp4cm.12). [thresholdText] is what was
 * typed, kept as text so a half-typed number is never rewritten under the cursor.
 */
data class SensorAlertDraft(
        val direction: SensorAlertDirection,
        val thresholdText: String,
        val severity: SensorAlertSeverity
) {
        /** The typed number, or null when it is blank or something the PC would refuse. */
        val threshold: Double? get() = SensorAlerts.parseThreshold(thresholdText)

        /** Whether the threshold box holds a usable number. */
        val isValid: Boolean get() = threshold != null

        /** True when something is typed but is not usable: the box shows its error. Blank is not an error yet. */
        val showsError: Boolean get() = thresholdText.isNotBlank() && threshold == null
}

/**
 * The decisions behind the "Alert me..." sheet, as plain JVM so they are proven off-device.
 * The sheet itself only draws what these say.
 */
object SensorAlertEditorLogic {

        /**
         * The sheet's starting point. An existing rule is shown as it is. A new one starts as "Above" the
         * sensor's current reading with a Warning severity: a real number to edit rather than a blank box,
         * and the most common ask ("tell me when it gets hotter than this").
         */
        fun draftFor(existing: SensorAlertRule?, currentValue: Double?): SensorAlertDraft =
                if (existing != null) {
                        SensorAlertDraft(existing.direction, thresholdText(existing.threshold), existing.severity)
                } else {
                        SensorAlertDraft(
                                SensorAlertDirection.ABOVE,
                                currentValue?.takeIf { SensorAlerts.isValidThreshold(it) }?.let { thresholdText(it) }.orEmpty(),
                                SensorAlertSeverity.WARNING
                        )
                }

        /**
         * A threshold as it goes in the text box: no unit, one decimal at most, none for a whole number.
         * Always plain ASCII with a dot, whatever the phone's language: [SensorAlerts.parseThreshold]
         * reads either separator back, and the typed text must round-trip.
         */
        fun thresholdText(value: Double): String = SensorAlerts.formatValue(value, null, Locale.ROOT)

        /**
         * Whether Save can do anything: the number is usable, a PC that mirrors alerts is connected, and a
         * new rule would not push the PC past its limit (an edit never would).
         */
        fun canSave(draft: SensorAlertDraft, state: SensorAlertsState, existing: SensorAlertRule?): Boolean =
                draft.isValid && state.canEdit && (existing != null || state.rules.size < SensorAlerts.MaxRules)

        /**
         * Why the sheet did not send a Save that looked sendable (RemEx-pp4cm.12). The connection can
         * drop between enabling the button and pressing it, so the state is read again at the moment of
         * failure; if none of the known blocks explains it, the rule was simply not sent
         * ([SensorAlertBlock.NOT_SENT]).
         */
        fun failureReason(state: SensorAlertsState, existing: SensorAlertRule?): SensorAlertBlock =
                blockedReason(state, existing) ?: SensorAlertBlock.NOT_SENT

        /** What the sheet says when it cannot be used, or null when it can. A string resource name, not text. */
        fun blockedReason(state: SensorAlertsState, existing: SensorAlertRule?): SensorAlertBlock? =
                when {
                        !state.connected -> SensorAlertBlock.NOT_CONNECTED
                        !state.supported -> SensorAlertBlock.PC_TOO_OLD
                        existing == null && state.rules.size >= SensorAlerts.MaxRules -> SensorAlertBlock.TOO_MANY
                        else -> null
                }
}

/** Why the "Alert me..." sheet cannot save. */
enum class SensorAlertBlock {
        /** No PC is connected, so there is nowhere to send the rule. */
        NOT_CONNECTED,

        /** The connected PC is too old to keep alerts for the phone. */
        PC_TOO_OLD,

        /** The PC already holds as many rules as it will. */
        TOO_MANY,

        /** Nothing in the connection explains it, but the rule was not sent. */
        NOT_SENT
}
