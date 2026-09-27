package com.clindsay94.remex.ui.routines

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowDropDown
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalResources
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clindsay94.remex.R
import com.clindsay94.remex.routines.RoutineExchange
import com.clindsay94.remex.routines.RoutineReasonText
import com.clindsay94.remex.routines.model.RoutineReasonArgs
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// Routines export and import (spec §1.9, §6.10; RemEx-pp0rt.11). Storage Access Framework only:
// the user picks where the file goes and which file comes in, so RemEx needs no storage permission.

/** Export (after a one-line "what is left out" confirmation) and import, as menu actions. */
class RoutineExchangeActions(val export: () -> Unit, val import: () -> Unit)

@Composable
internal fun rememberRoutineExchange(viewModel: RoutinesViewModel): RoutineExchangeActions {
    val context = LocalContext.current
    val resources = LocalResources.current
    val scope = rememberCoroutineScope()
    var confirmExport by remember { mutableStateOf(false) }

    val saver =
        rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/json")) { uri ->
            if (uri == null) return@rememberLauncherForActivityResult
            val text = viewModel.exportText()
            scope.launch {
                val written =
                    withContext(Dispatchers.IO) {
                        runCatching {
                            context.contentResolver.openOutputStream(uri, "wt")?.use { it.write(text.toByteArray(Charsets.UTF_8)) } != null
                        }.getOrDefault(false)
                    }
                viewModel.post(resources.getString(if (written) R.string.routines_export_done else R.string.routines_export_failed))
            }
        }
    val opener =
        rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
            if (uri == null) return@rememberLauncherForActivityResult
            scope.launch {
                val text =
                    withContext(Dispatchers.IO) {
                        runCatching {
                            // A routines file is small; anything past 1 MiB is not one.
                            context.contentResolver.openInputStream(uri)?.use { input ->
                                val bytes = input.readNBytes(MAX_IMPORT_BYTES + 1)
                                if (bytes.size > MAX_IMPORT_BYTES) null else bytes.toString(Charsets.UTF_8)
                            }
                        }.getOrNull()
                    }
                viewModel.openImport(text)
            }
        }

    if (confirmExport) {
        AlertDialog(
            onDismissRequest = { confirmExport = false },
            title = { Text(stringResource(R.string.routines_menu_export)) },
            text = { Text(stringResource(R.string.routines_export_note)) },
            confirmButton = {
                Button(onClick = {
                    confirmExport = false
                    saver.launch(RoutineExchange.SUGGESTED_NAME)
                }) { Text(stringResource(R.string.routines_export_confirm)) }
            },
            dismissButton = { TextButton(onClick = { confirmExport = false }) { Text(stringResource(R.string.button_cancel)) } },
        )
    }

    RoutineImportHost(viewModel)

    return remember(saver, opener) {
        RoutineExchangeActions(
            export = { confirmExport = true },
            // Some providers label a .remexroutines file octet-stream; the reader decides what it is.
            import = { opener.launch(arrayOf("application/json", "application/octet-stream", "text/plain", "*/*")) },
        )
    }
}

private const val MAX_IMPORT_BYTES = 1 shl 20

/** The review sheet for a read file, or the message for one that could not be read. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun RoutineImportHost(viewModel: RoutinesViewModel) {
    val file by viewModel.importFile.collectAsStateWithLifecycle()
    when (val result = file) {
        null -> Unit
        RoutineExchange.ReadResult.Unreadable, RoutineExchange.ReadResult.TooNew -> {
            // A failure has its own title, never the success one (§1.9).
            AlertDialog(
                onDismissRequest = viewModel::closeImport,
                title = { Text(stringResource(R.string.routines_import_failed_title)) },
                text = {
                    Text(
                        stringResource(
                            if (result == RoutineExchange.ReadResult.TooNew) R.string.routines_import_too_new else R.string.routines_import_unreadable,
                        )
                    )
                },
                confirmButton = { TextButton(onClick = viewModel::closeImport) { Text(stringResource(R.string.button_dismiss)) } },
            )
        }
        is RoutineExchange.ReadResult.Ok -> ImportReviewSheet(viewModel, result)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ImportReviewSheet(viewModel: RoutinesViewModel, file: RoutineExchange.ReadResult.Ok) {
    val context = LocalContext.current
    val pcs by viewModel.pcs.collectAsStateWithLifecycle()
    val selectedPc by viewModel.selectedPc.collectAsStateWithLifecycle()
    var pc by remember { mutableStateOf(selectedPc ?: pcs.firstOrNull()?.identity) }
    var picking by remember { mutableStateOf(false) }
    val reviews = remember(file, pc) { viewModel.reviewImport(file.routines, pc) }
    // Everything importable starts ticked; the user unticks what they do not want.
    val unticked = remember(file) { mutableStateMapOf<Int, Boolean>() }
    val chosen = reviews.filter { it.importable && unticked[it.index] != true }

    ModalBottomSheet(onDismissRequest = viewModel::closeImport) {
        Column(
            Modifier.padding(horizontal = 24.dp).padding(bottom = 24.dp).verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text(stringResource(R.string.routines_import_title), style = MaterialTheme.typography.titleLarge, modifier = Modifier.semantics { heading() })
            if (reviews.isEmpty()) {
                Text(stringResource(R.string.routines_import_empty), style = MaterialTheme.typography.bodyMedium)
            } else if (pcs.isEmpty()) {
                Text(stringResource(R.string.routines_import_no_pc), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.error)
            } else {
                // "Pick the PC it controls" (§1.9): the file never names one.
                Text(stringResource(R.string.routines_import_pc), style = MaterialTheme.typography.labelLarge)
                Box {
                    OutlinedButton(onClick = { picking = true }, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
                        Text(pcLabel(pcs.firstOrNull { it.identity == pc }?.name), modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Icon(Icons.Default.ArrowDropDown, contentDescription = null)
                    }
                    DropdownMenu(expanded = picking, onDismissRequest = { picking = false }) {
                        pcs.forEach { option ->
                            DropdownMenuItem(text = { Text(pcLabel(option.name)) }, onClick = {
                                picking = false
                                pc = option.identity
                            })
                        }
                    }
                }
                Text(stringResource(R.string.routines_import_off_note), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            reviews.forEach { review ->
                val name = review.prepared?.name?.takeIf { it.isNotBlank() } ?: stringResource(R.string.routines_import_unnamed)
                val notes =
                    buildList {
                        if (!review.importable && !review.needsHome) {
                            val reason = RoutineReasonText.message(context, review.verdict.reasonCode, RoutineReasonArgs(pc = pcs.firstOrNull { it.identity == pc }?.name))
                            add(stringResource(R.string.routines_import_invalid, reason))
                        }
                        if (review.needsHome) add(stringResource(R.string.routines_import_needs_home))
                        if (review.needsTag) add(stringResource(R.string.routines_import_needs_tag))
                        if (review.nameTaken) add(stringResource(R.string.routines_import_name_taken))
                    }
                val checked = review.importable && unticked[review.index] != true
                Row(
                    Modifier.fillMaxWidth()
                        .heightIn(min = 48.dp)
                        .toggleable(value = checked, enabled = review.importable && pcs.isNotEmpty(), role = Role.Checkbox) { unticked[review.index] = !it },
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Checkbox(checked = checked, onCheckedChange = null, enabled = review.importable && pcs.isNotEmpty())
                    Spacer(Modifier.width(12.dp))
                    Column(Modifier.weight(1f)) {
                        Text(name, style = MaterialTheme.typography.bodyLarge)
                        notes.forEach { note ->
                            Text(
                                note,
                                style = MaterialTheme.typography.bodySmall,
                                color = if (review.importable) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.error,
                            )
                        }
                    }
                }
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.End)) {
                TextButton(onClick = viewModel::closeImport) { Text(stringResource(R.string.button_cancel)) }
                Button(onClick = { viewModel.importRoutines(chosen) }, enabled = chosen.isNotEmpty() && pcs.isNotEmpty()) {
                    Text(stringResource(R.string.routines_import_action))
                }
            }
        }
    }
}
