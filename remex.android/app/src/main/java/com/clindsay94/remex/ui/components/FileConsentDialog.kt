package com.clindsay94.remex.ui.components

import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
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
import com.clindsay94.remex.service.FileConsentOrigin
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
    // Null deadline means the serving device sent none, so there is no countdown to run and none to
    // show. Inventing one from this phone's clock would be a guess about the only clock that matters,
    // and it is not this one (RemEx-6mxu).
    val deadline = active.expiresAtUnixMs
    var secondsLeft by remember(active.consentId) { mutableStateOf(deadline?.let(::secondsRemaining) ?: 0) }

    LaunchedEffect(active.consentId) {
        if (deadline == null) return@LaunchedEffect
        while (true) {
            val remaining = secondsRemaining(deadline)
            secondsLeft = remaining
            if (remaining <= 0) break
            delay(1_000)
        }
    }

    val isPush = active.kind == FileConsentKinds.INCOMING_PUSH
    // WHOSE FILES THE QUESTION IS ABOUT DECIDES THE WORDING, and it is not implied by the kind: the PC
    // routes a full-browse and an incoming-push question here using the same two wire values this phone
    // uses when it is the one serving (RemEx-vyhm). Same prompt, opposite meaning.
    val fromPc = active.origin == FileConsentOrigin.PAIRED_PC
    val title =
        when {
            fromPc && isPush -> stringResource(R.string.file_consent_remote_push_title)
            fromPc -> stringResource(R.string.file_consent_remote_full_browse_title)
            isPush -> stringResource(R.string.file_consent_push_title)
            else -> stringResource(R.string.file_consent_full_browse_title)
        }
    val message =
        when {
            fromPc && isPush && !active.detail.isNullOrBlank() ->
                stringResource(R.string.file_consent_remote_push_message, active.detail)
            fromPc && isPush -> stringResource(R.string.file_consent_remote_push_message_generic)
            fromPc -> stringResource(R.string.file_consent_remote_full_browse_message)
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
                // **THE MESSAGE SCROLLS; THE CONTROLS BELOW IT MUST NOT BE PUSHED OUT (RemEx-7iub).**
                // Material3 puts this slot in a Box(weight(1f, fill = false)) with no scrolling, so a
                // message taller than the dialog is CLIPPED — silently, and from the bottom, which is
                // where the Remember checkbox and the countdown live. Listing every offered file name
                // instead of five made that reachable: at a large system font, or in landscape, the
                // one prompt in the app where "how long do I have" and "does this stick" matter most
                // could lose both and give no sign it had.
                //
                // weight(1f, fill = false) hands the message whatever room is left AFTER the checkbox
                // and countdown have taken theirs, so they are laid out first and cannot be evicted.
                Text(
                    message,
                    style = MaterialTheme.typography.bodyMedium,
                    modifier =
                        Modifier.weight(1f, fill = false)
                            .verticalScroll(rememberScrollState()),
                )
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
                if (deadline != null) {
                    Text(
                        stringResource(R.string.file_consent_expires_in, secondsLeft.coerceAtLeast(0)),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
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
