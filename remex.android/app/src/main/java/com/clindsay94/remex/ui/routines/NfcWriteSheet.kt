package com.clindsay94.remex.ui.routines

import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import android.content.Intent
import android.provider.Settings
import android.view.HapticFeedbackConstants
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.runtime.produceState
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clindsay94.remex.routines.RoutineNfcBinding
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.SheetValue
import androidx.compose.material3.rememberBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.res.vectorResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.LifecycleResumeEffect
import com.clindsay94.remex.R
import com.clindsay94.remex.routines.NfcTokens
import com.clindsay94.remex.routines.nfc.NfcHaptic
import com.clindsay94.remex.routines.nfc.NfcRoutineTag
import com.clindsay94.remex.routines.nfc.NfcTagIo
import com.clindsay94.remex.routines.nfc.NfcTokenVerifier
import com.clindsay94.remex.routines.nfc.NfcWriteRules
import com.clindsay94.remex.routines.nfc.NfcWriteState
import com.clindsay94.remex.ui.screens.RemexLoadingIndicator
import com.clindsay94.remex.ui.theme.LocalReducedMotion
import kotlinx.coroutines.launch

/**
 * The NFC tag write sheet (routines spec 1.5, A11; R-UX-14, R-SYS-41). Reads in reader mode while it
 * listens, so a tag held to the phone here is never dispatched to NfcRoutineActivity and never runs.
 *
 * @param rotate "Rewrite tag": a NEW token is written, and only once the tag is written is it stored,
 *   so every older tag for this routine stops working then (spec §8.3.2), never before.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun NfcWriteSheet(
    viewModel: RoutinesViewModel,
    routineId: String,
    routineName: String,
    rotate: Boolean,
    onDismiss: () -> Unit,
) {
    val context = LocalContext.current
    val view = LocalView.current
    val scope = rememberCoroutineScope()
    val activity = remember(context) { context.findActivity() }
    val adapter = remember(context) { NfcTagIo.adapter(context) }
    var state by remember { mutableStateOf(NfcWriteRules.initial(adapter != null, adapter?.isEnabled == true)) }
    var token by remember { mutableStateOf<String?>(null) }
    var committed by remember { mutableStateOf<String?>(null) }
    var replaceConfirmedFor by remember { mutableStateOf<String?>(null) }
    val sheetState = rememberBottomSheetState(initialValue = SheetValue.Hidden, enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded))

    LaunchedEffect(routineId, rotate) {
        val existing = viewModel.nfcBinding(routineId)?.token
        committed = existing
        token = if (rotate || existing == null) NfcTokens.mint() else existing
    }
    // Back from system NFC settings: pick the state up again.
    LifecycleResumeEffect(adapter) {
        if (state == NfcWriteState.Off && adapter?.isEnabled == true) state = NfcWriteState.Waiting
        onPauseOrDispose { }
    }
    LaunchedEffect(state) {
        when (NfcWriteRules.haptic(state)) {
            NfcHaptic.CONFIRM -> view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
            NfcHaptic.REJECT -> view.performHapticFeedback(HapticFeedbackConstants.REJECT)
            NfcHaptic.NONE -> Unit
        }
    }

    val currentState by rememberUpdatedState(state)
    val currentToken by rememberUpdatedState(token)
    val currentCommitted by rememberUpdatedState(committed)
    val currentReplace by rememberUpdatedState(replaceConfirmedFor)
    // Reader mode stays on for the WHOLE life of the sheet, in every state: a tag held to the phone
    // while it shows Success, an error or "Replace?" must be swallowed here, never dispatched to
    // NfcRoutineActivity, or a second touch after writing would run the routine. Only the listening
    // states act on a tag; the rest ignore it.
    DisposableEffect(activity) {
        if (activity != null) {
            NfcTagIo.enableReader(activity) { tag ->
                // The NFC reader thread: inspect and write while the tag is still in contact.
                when (currentState) {
                    NfcWriteState.Waiting -> {
                        val writeToken = currentToken ?: return@enableReader
                        val message = NfcTagIo.message(context, NfcRoutineTag(routineId, writeToken))
                        val inspection = NfcTagIo.inspect(tag)
                        val preflight =
                            NfcWriteRules.preflight(inspection, routineId, message.byteArrayLength, currentReplace) { id -> viewModel.routineName(id) }
                        when (preflight) {
                            is NfcWriteRules.Preflight.Stop -> scope.launch { state = preflight.state }
                            NfcWriteRules.Preflight.Write -> {
                                scope.launch { state = NfcWriteState.Writing }
                                val outcome = NfcTagIo.write(tag, message)
                                scope.launch {
                                    val next = NfcWriteRules.afterWrite(outcome)
                                    if (next == NfcWriteState.Success) {
                                        viewModel.commitNfcToken(routineId, writeToken)
                                        committed = writeToken
                                    }
                                    state = next
                                }
                            }
                        }
                    }
                    NfcWriteState.TestWaiting -> {
                        val tagRead = NfcRoutineTag.parse(NfcTagIo.readUri(tag))
                        val matches = NfcTokenVerifier().matches(tagRead, routineId, currentCommitted)
                        scope.launch { state = NfcWriteRules.afterTestRead(matches) }
                    }
                    else -> Unit
                }
            }
        }
        onDispose { if (activity != null) NfcTagIo.disableReader(activity) }
    }

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState, sheetMaxWidth = 640.dp) {
        Column(
            Modifier.fillMaxWidth().padding(horizontal = 24.dp).padding(bottom = 24.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Text(
                stringResource(R.string.routines_nfc_sheet_title),
                style = MaterialTheme.typography.titleLarge,
                modifier = Modifier.fillMaxWidth().semantics { heading() },
            )
            NfcIllustration(ImageVector.vectorResource(R.drawable.ic_routine_nfc))
            Text(
                nfcStateText(state, routineName),
                style = MaterialTheme.typography.bodyLarge,
                textAlign = TextAlign.Center,
                modifier = Modifier.fillMaxWidth().semantics { liveRegion = LiveRegionMode.Polite },
            )
            val busy = state == NfcWriteState.Waiting || state == NfcWriteState.Writing || state == NfcWriteState.TestWaiting
            if (busy && !LocalReducedMotion.current) RemexLoadingIndicator(modifier = Modifier.size(48.dp))
            NfcActions(
                state = state,
                onTurnOn = { context.startActivity(Intent(Settings.ACTION_NFC_SETTINGS)) },
                onReplace = { other ->
                    replaceConfirmedFor = other
                    state = NfcWriteState.Waiting
                },
                onRetry = { state = if (state == NfcWriteState.TestFailed) NfcWriteState.TestWaiting else NfcWriteState.Waiting },
                onTest = { state = NfcWriteState.TestWaiting },
                onDone = onDismiss,
            )
        }
    }
}

/**
 * The `nfc.tap` trigger card's tag line (spec 1.5): "Tag written 12 Sep" or "No tag written yet",
 * then "Write tag" / "Write another tag" and "Rewrite tag". A routine must be saved first, because
 * the tag names the stored routine's id.
 *
 * @param routineId the saved routine, or null while it is new or has unsaved edits.
 */
@Composable
internal fun NfcTagActions(viewModel: RoutinesViewModel, routineId: String?, enabled: Boolean, onWrite: (rotate: Boolean) -> Unit) {
    if (routineId == null) {
        Text(stringResource(R.string.routines_nfc_save_first), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        return
    }
    val revision by viewModel.nfcRevision.collectAsStateWithLifecycle()
    val binding by produceState<RoutineNfcBinding?>(null, routineId, revision) { value = viewModel.nfcBinding(routineId) }
    var confirmRewrite by remember { mutableStateOf(false) }
    val written = binding?.writtenAtUnixMs?.takeIf { it > 0 }
    Text(
        if (written != null) stringResource(
            R.string.routines_nfc_written_on,
            RoutineTimeText.date(java.time.Instant.ofEpochMilli(written).atZone(java.time.ZoneId.systemDefault()).toLocalDate(), appLocale()),
        ) else stringResource(R.string.routines_nfc_not_written),
        style = MaterialTheme.typography.bodyMedium,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        FilledTonalButton(onClick = { onWrite(false) }, enabled = enabled, modifier = Modifier.heightIn(min = 48.dp)) {
            Text(stringResource(if (binding == null) R.string.routines_nfc_write_tag else R.string.routines_nfc_write_another))
        }
        if (binding != null) {
            TextButton(onClick = { confirmRewrite = true }, enabled = enabled, modifier = Modifier.heightIn(min = 48.dp)) {
                Text(stringResource(R.string.routines_nfc_rewrite))
            }
        }
    }
    if (confirmRewrite) {
        AlertDialog(
            onDismissRequest = { confirmRewrite = false },
            title = { Text(stringResource(R.string.routines_nfc_rewrite_title)) },
            text = { Text(stringResource(R.string.routines_nfc_rewrite_body)) },
            confirmButton = {
                Button(onClick = {
                    confirmRewrite = false
                    onWrite(true)
                }) { Text(stringResource(R.string.routines_nfc_rewrite)) }
            },
            dismissButton = { TextButton(onClick = { confirmRewrite = false }) { Text(stringResource(R.string.button_cancel)) } },
        )
    }
}

@Composable
private fun NfcIllustration(icon: ImageVector) {
    Surface(shape = CircleShape, color = MaterialTheme.colorScheme.secondaryContainer, modifier = Modifier.size(96.dp)) {
        Box(contentAlignment = Alignment.Center) {
            Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.onSecondaryContainer, modifier = Modifier.size(48.dp))
        }
    }
}

@Composable
private fun nfcStateText(state: NfcWriteState, routineName: String): String =
    when (state) {
        NfcWriteState.NoHardware -> stringResource(R.string.routines_trigger_nfc_no_hardware)
        NfcWriteState.Off -> stringResource(R.string.routines_nfc_off)
        NfcWriteState.Waiting, NfcWriteState.Writing -> stringResource(R.string.routines_nfc_waiting)
        is NfcWriteState.ExistingTag -> stringResource(R.string.routines_nfc_existing, state.otherName, routineName)
        NfcWriteState.Success -> stringResource(R.string.routines_nfc_success, routineName)
        NfcWriteState.ReadOnly -> stringResource(R.string.routines_nfc_read_only)
        NfcWriteState.TooSmall -> stringResource(R.string.routines_nfc_too_small)
        NfcWriteState.LostContact -> stringResource(R.string.routines_nfc_lost)
        NfcWriteState.TestWaiting -> stringResource(R.string.routines_nfc_test_waiting)
        NfcWriteState.TestSuccess -> stringResource(R.string.routines_nfc_test_ok, routineName)
        NfcWriteState.TestFailed -> stringResource(R.string.routines_nfc_test_wrong, routineName)
    }

@Composable
private fun NfcActions(
    state: NfcWriteState,
    onTurnOn: () -> Unit,
    onReplace: (String) -> Unit,
    onRetry: () -> Unit,
    onTest: () -> Unit,
    onDone: () -> Unit,
) {
    val cancel = stringResource(R.string.button_cancel)
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterHorizontally), modifier = Modifier.fillMaxWidth()) {
        val tall = Modifier.heightIn(min = 48.dp)
        when (state) {
            NfcWriteState.Off -> {
                TextButton(onClick = onDone, modifier = tall) { Text(cancel) }
                Button(onClick = onTurnOn, modifier = tall) { Text(stringResource(R.string.routines_nfc_turn_on)) }
            }
            is NfcWriteState.ExistingTag -> {
                TextButton(onClick = onDone, modifier = tall) { Text(cancel) }
                Button(onClick = { onReplace(state.otherRoutineId) }, modifier = tall) { Text(stringResource(R.string.routines_nfc_replace)) }
            }
            NfcWriteState.Writing -> Unit
            NfcWriteState.Success -> {
                OutlinedButton(onClick = onTest, modifier = tall) { Text(stringResource(R.string.routines_nfc_test_it)) }
                Button(onClick = onDone, modifier = tall) { Text(stringResource(R.string.button_done)) }
            }
            NfcWriteState.ReadOnly, NfcWriteState.TooSmall, NfcWriteState.LostContact, NfcWriteState.TestFailed -> {
                TextButton(onClick = onDone, modifier = tall) { Text(if (state == NfcWriteState.TestFailed) stringResource(R.string.button_done) else cancel) }
                Button(onClick = onRetry, modifier = tall) { Text(stringResource(R.string.routines_nfc_try_again)) }
            }
            NfcWriteState.TestSuccess -> Button(onClick = onDone, modifier = tall) { Text(stringResource(R.string.button_done)) }
            NfcWriteState.NoHardware, NfcWriteState.Waiting, NfcWriteState.TestWaiting ->
                TextButton(onClick = onDone, modifier = tall) { Text(cancel) }
        }
    }
}

internal fun Context.findActivity(): Activity? {
    var current: Context? = this
    while (current is ContextWrapper) {
        if (current is Activity) return current
        current = current.baseContext
    }
    return null
}
