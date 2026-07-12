package com.clindsay94.remex.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/**
 * Resolves a file-sharing consent prompt from its notification Accept/Decline actions (plan WP9). The
 * notification is a background-safe channel for the prompt raised by [FileConsentManager]; tapping an
 * action broadcasts here, which forwards the decision to the coordinator and clears the notification.
 *
 * Notification actions accept/deny for this one request only (`remember = false`); persisting an
 * auto-accept grant is done via the foreground dialog's "Remember" checkbox or the settings toggle,
 * because a notification action cannot carry that choice.
 */
class FileConsentActionReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION_RESOLVE) return
        val consentId = intent.getStringExtra(EXTRA_CONSENT_ID) ?: return
        val granted = intent.getBooleanExtra(EXTRA_GRANTED, false)
        FileConsentManager.resolve(consentId, granted, remember = false)
        FileTransferNotificationManager.cancelConsent(context, consentId)
    }

    companion object {
        const val ACTION_RESOLVE = "com.clindsay94.remex.action.FILE_CONSENT_RESOLVE"
        const val EXTRA_CONSENT_ID = "consentId"
        const val EXTRA_GRANTED = "granted"
    }
}
