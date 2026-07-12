package com.clindsay94.remex.ui.components

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clindsay94.remex.R
import com.clindsay94.remex.service.FileConsentKinds
import com.clindsay94.remex.service.FileConsentManager
import kotlinx.coroutines.delay

/**
 * Foreground consent dialog (plan §2 / WP9). Observes the single active prompt raised by
 * [FileConsentManager] and, while the app is visible, mirrors the parallel high-priority notification:
 * a full-device-browse grant or an incoming file push from the paired PC, with **Allow** / **Deny** and
 * a "Remember" checkbox that persists the grant. A live countdown reflects the 60-second auto-deny
 * window; whichever responder (dialog, notification, or timeout) fires first wins.
 *
 * Rendered once at the app root ([com.clindsay94.remex.MainActivity]); emits nothing when no prompt is
 * active.
 */
@Composable
fun FileConsentDialogHost() {
    val prompt by FileConsentManager.activePrompt.collectAsStateWithLifecycle()
    val active = prompt ?: return

    // Reset per prompt: a new consentId is a distinct request, so "remember" must not leak across.
    var rememberChoice by remember(active.consentId) { mutableStateOf(false) }
    var secondsLeft by remember(active.consentId) { mutableStateOf(secondsRemaining(active.expiresAtUnixMs)) }

    LaunchedEffect(active.consentId) {
        while (true) {
            val remaining = secondsRemaining(active.expiresAtUnixMs)
            secondsLeft = remaining
            if (remaining <= 0) break
            delay(1_000)
        }
    }

    val isPush = active.kind == FileConsentKinds.INCOMING_PUSH
    val title =
        if (isPush) stringResource(R.string.file_consent_push_title)
        else stringResource(R.string.file_consent_full_browse_title)
    val message =
        when {
            isPush && !active.detail.isNullOrBlank() ->
                stringResource(R.string.file_consent_push_message, active.detail)
            isPush -> stringResource(R.string.file_consent_push_message_generic)
            else -> stringResource(R.string.file_consent_full_browse_message)
        }

    AlertDialog(
        onDismissRequest = { FileConsentManager.resolve(active.consentId, granted = false, remember = false) },
        title = { Text(title) },
        text = {
            Column {
                Text(message, style = MaterialTheme.typography.bodyMedium)
                Spacer(Modifier.height(16.dp))
                Row(
                    modifier =
                        Modifier.fillMaxWidth().padding(vertical = 4.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(4.dp),
                ) {
                    Checkbox(checked = rememberChoice, onCheckedChange = { rememberChoice = it })
                    Text(
                        stringResource(R.string.file_consent_remember),
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
                Text(
                    stringResource(R.string.file_consent_expires_in, secondsLeft.coerceAtLeast(0)),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        },
        confirmButton = {
            TextButton(
                onClick = { FileConsentManager.resolve(active.consentId, granted = true, remember = rememberChoice) }
            ) {
                Text(stringResource(R.string.file_consent_allow))
            }
        },
        dismissButton = {
            TextButton(
                onClick = { FileConsentManager.resolve(active.consentId, granted = false, remember = false) }
            ) {
                Text(stringResource(R.string.file_consent_deny))
            }
        },
    )
}

private fun secondsRemaining(expiresAtUnixMs: Long): Int =
    ((expiresAtUnixMs - System.currentTimeMillis()) / 1_000L).toInt()
