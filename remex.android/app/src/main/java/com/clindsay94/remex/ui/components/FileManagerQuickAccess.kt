package com.clindsay94.remex.ui.components

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Storage
import androidx.compose.material3.AssistChip
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.screens.FileManagerLogic
import com.clindsay94.remex.ui.screens.RemoteSharedRoot
import com.clindsay94.remex.ui.screens.RemoteVolume

/**
 * Quick-access strip (plan WP7): curated shared roots as selectable chips, plus a "This PC" volumes
 * row when a full-browse grant is held. Tapping a chip switches the active browsing root.
 */
@Composable
fun FileManagerQuickAccess(
    roots: List<RemoteSharedRoot>,
    volumes: List<RemoteVolume>,
    selectedRootId: String?,
    canBrowseDevice: Boolean,
    /**
     * True while a `file_volumes_request` is awaiting the PC's consent answer (RemEx-c7v4n). Disables
     * re-tap and swaps the leading icon for a spinner so the wait is visible on the control itself,
     * rather than only in the status line above the list. Deliberately no countdown here — that is a
     * separate, not-yet-approved option.
     */
    browseDevicePending: Boolean = false,
    onSelectRoot: (String) -> Unit,
    onSelectVolume: (RemoteVolume) -> Unit,
    onBrowseDevice: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(modifier = modifier, verticalArrangement = Arrangement.spacedBy(4.dp)) {
        if (roots.isNotEmpty()) {
            Text(
                text = stringResource(R.string.file_manager_quick_access),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Row(
                modifier = Modifier.horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                roots.forEach { root ->
                    // Selecting a read-only root silently disables upload, new-folder, paste,
                    // rename and delete. Mark it on the chip so the reason is available BEFORE
                    // the buttons go grey, rather than only after (RemEx-dc57).
                    //
                    // The read-only fact rides on the ICON's contentDescription, deliberately NOT
                    // on the chip's stateDescription. FilterChip is a selectable component, so
                    // Compose already publishes a selected/not-selected state for it, and
                    // stateDescription REPLACES that rather than adding to it — marking these
                    // chips that way would stop a screen-reader user hearing which folder is the
                    // active one. Describing the icon keeps both facts. The writable folder icon
                    // stays null-described because there it really is decorative: the chip's own
                    // label already names the folder.
                    val readOnly = !root.isWritable
                    val readOnlyLabel = stringResource(R.string.file_transfer_read_only_root)
                    FilterChip(
                        selected = root.rootId == selectedRootId,
                        onClick = { onSelectRoot(root.rootId) },
                        label = { Text(root.displayName) },
                        leadingIcon = {
                            Icon(
                                if (readOnly) Icons.Default.Lock else Icons.Default.Folder,
                                contentDescription = if (readOnly) readOnlyLabel else null,
                                modifier = Modifier.padding(1.dp),
                            )
                        },
                    )
                }
            }
        }

        if (volumes.isEmpty() && canBrowseDevice) {
            // Full-device browse is opt-in and consent-gated on the PC; only request on an explicit tap.
            // While pending, disable re-tap and swap the icon for a spinner so the wait shows on the
            // control the user just pressed, not only in the status line (review, RemEx-c7v4n round 2).
            AssistChip(
                onClick = onBrowseDevice,
                enabled = FileManagerLogic.browseDeviceEnabled(canBrowseDevice, browseDevicePending),
                label = { Text(stringResource(R.string.file_manager_browse_device)) },
                leadingIcon = {
                    if (browseDevicePending) {
                        CircularProgressIndicator(modifier = Modifier.size(16.dp), strokeWidth = 2.dp)
                    } else {
                        Icon(Icons.Default.Computer, contentDescription = null)
                    }
                },
            )
        }

        if (volumes.isNotEmpty()) {
            Text(
                text = stringResource(R.string.file_manager_volumes),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Row(
                modifier = Modifier.horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                volumes.forEach { volume ->
                    val freeLabel = FileManagerLogic.formatBytes(volume.freeBytes)
                    FilterChip(
                        selected = volume.id == selectedRootId,
                        onClick = { onSelectVolume(volume) },
                        label = {
                            Text(
                                text = stringResource(
                                    R.string.file_manager_volume_chip_label,
                                    volume.label,
                                    freeLabel,
                                )
                            )
                        },
                        leadingIcon = { Icon(Icons.Default.Storage, contentDescription = null) },
                        colors = FilterChipDefaults.filterChipColors(),
                    )
                }
            }
        }
    }
}
