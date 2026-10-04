package com.clindsay94.remex.ui.components

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.platform.LocalContext
import androidx.core.content.ContextCompat

/**
 * Asks, once per run of the app, for the permission every transfer notification needs
 * (RemEx-pp4cm.8).
 *
 * **WITHOUT IT A TRANSFER IS COMPLETELY SILENT.** Posting needs POST_NOTIFICATIONS: the notifier
 * checks it and quietly does nothing when it is missing (progress, "Downloaded", "Could not
 * download", all of them), and the transfer job's own notification is hidden from the shade. The
 * only place the app asked was the Connection screen's connect flow, so a phone that never granted
 * it there (dismissed once, or restored without it) was never asked again, and its downloads showed
 * nothing at all. The Files screen is where a transfer starts, so it is where the question is asked.
 */
@Composable
fun AskForTransferNotifications() {
    val context = LocalContext.current
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {}
    LaunchedEffect(Unit) {
        val granted =
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) ==
                PackageManager.PERMISSION_GRANTED
        if (TransferNotificationAsk.shouldAsk(granted)) launcher.launch(Manifest.permission.POST_NOTIFICATIONS)
    }
}

/**
 * The once-per-process rule, apart from Compose so it can be tested. Android stops showing the
 * prompt by itself after two refusals; this only keeps the app from asking on every visit to the
 * screen before that.
 */
internal object TransferNotificationAsk {
    @Volatile private var asked = false

    fun shouldAsk(granted: Boolean): Boolean {
        if (granted || asked) return false
        asked = true
        return true
    }

    /** Forgets that the question was asked; for tests. */
    fun reset() {
        asked = false
    }
}
