package com.clindsay94.remex.ui.components

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.service.ConflictAction
import com.clindsay94.remex.service.FileConflictCodes

/** What the sheet is asking about. */
data class FileConflictPrompt(
    /** Binds an answer to this prompt, so a late dismissal cannot resolve the next one. */
    val token: Long,
    val fileName: String,
    val errorCode: String,
    val actions: List<ConflictAction>,
    /** True when more items follow, which is the only time "apply to all" is worth offering. */
    val hasRemaining: Boolean,
)

/**
 * Asks what to do about a filename collision (RemEx-agpn).
 *
 * **FAIL-CLOSED: DISMISSING IS SKIP, NEVER REPLACE.** A swipe-down, a back press and a tap outside
 * all resolve to [ConflictAction.Skip], because the alternative is that a gesture the user makes to
 * get rid of a dialog overwrites their file. Skip is the only answer that cannot lose data, so it is
 * the one every ambiguous exit maps to.
 *
 * The available actions are decided by `FileConflictPolicy`, not here — in particular this sheet
 * never assumes Replace is on the list. That is what keeps a different-kind collision, whose replace
 * would delete a whole directory tree, from being offered one.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FileConflictSheet(
    prompt: FileConflictPrompt,
    onResolved: (Long, ConflictAction, Boolean) -> Unit,
) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    var applyToAll by remember(prompt.fileName) { mutableStateOf(false) }

    ModalBottomSheet(
        // Every dismissal path lands here, and it answers Skip. Passing applyToAll along would let a
        // stray swipe silently skip the whole batch, so a dismissal is always a one-off.
        onDismissRequest = { onResolved(prompt.token, ConflictAction.Skip, false) },
        sheetState = sheetState,
    ) {
        Column(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 24.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text(
                text = stringResource(R.string.file_conflict_title, prompt.fileName),
                style = MaterialTheme.typography.headlineSmallEmphasized,
            )

            Text(
                // THE BODY MUST NOT ASSERT A CAUSE THE CLIENT DOES NOT KNOW. Review found this
                // testing only for the different-kind code, so resolved_name_unusable rendered "there
                // is already a file with this name" - factually false, the name is too long - to a
                // user who had just chosen Keep both. An unrecognised code now says nothing about
                // why, because saying nothing is the only honest option.
                text = stringResource(
                    when (prompt.errorCode) {
                        FileConflictCodes.DESTINATION_EXISTS -> R.string.file_conflict_body_exists
                        FileConflictCodes.DESTINATION_IS_DIFFERENT_KIND ->
                            R.string.file_conflict_body_different_kind
                        FileConflictCodes.RESOLVED_NAME_UNUSABLE ->
                            R.string.file_conflict_body_name_unusable
                        else -> R.string.file_conflict_body_unknown
                    },
                ),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            if (prompt.hasRemaining) {
                // OFFERED ONLY WHEN SOMETHING ACTUALLY REMAINS. On the last item "apply to all" is a
                // checkbox that changes nothing, which teaches the user it does nothing.
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .fillMaxWidth()
                        // One toggleable row rather than a bare Checkbox: the label is part of the
                        // target, and the row reads as a single control to a screen reader.
                        .toggleable(
                            value = applyToAll,
                            role = Role.Checkbox,
                            onValueChange = { applyToAll = it },
                        )
                        .padding(vertical = 4.dp),
                ) {
                    Checkbox(checked = applyToAll, onCheckedChange = null)
                    Text(
                        text = stringResource(R.string.file_conflict_apply_to_all),
                        style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.padding(start = 8.dp),
                    )
                }
            }

            // KEEP BOTH IS THE FILLED BUTTON WHERE IT IS AVAILABLE, because it is the answer that
            // cannot lose anything. Replace is outlined rather than emphasised for the same reason:
            // visual weight is a recommendation, and recommending the destructive answer is wrong.
            for (action in prompt.actions) {
                when (action) {
                    ConflictAction.KeepBoth -> Button(
                        onClick = { onResolved(prompt.token, action, applyToAll) },
                        modifier = Modifier.fillMaxWidth(),
                    ) { Text(stringResource(R.string.file_conflict_keep_both)) }

                    ConflictAction.Replace -> OutlinedButton(
                        onClick = { onResolved(prompt.token, action, applyToAll) },
                        modifier = Modifier.fillMaxWidth(),
                    ) { Text(stringResource(R.string.file_conflict_replace)) }

                    ConflictAction.Skip -> TextButton(
                        onClick = { onResolved(prompt.token, action, applyToAll) },
                        modifier = Modifier.fillMaxWidth(),
                    ) { Text(stringResource(R.string.file_conflict_skip)) }
                }
            }
        }
    }
}
