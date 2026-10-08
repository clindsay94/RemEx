package com.clindsay94.remex.ui.files

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.FolderShared
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.PhoneAndroid
import androidx.compose.material.icons.filled.Storage
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.screens.FileManagerLogic
import com.clindsay94.remex.ui.screens.RemexLoadingIndicator
import com.clindsay94.remex.ui.theme.rememberRemexIconButtonShapes

/**
 * The folder tree (file browser redesign, 2026-10-08): "This phone" with its shared folders and phone-only
 * bookmarks, then "PC" with its shared folders and drives. Tapping a row opens it in the contents pane; the
 * chevron shows or hides the folders inside without leaving the current one.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun FileTreePane(
    rows: List<TreeRow>,
    selectedKey: String?,
    pcConnected: Boolean,
    canBrowsePcDevice: Boolean,
    /** A volumes request is already waiting for the PC: the row stays but can't be tapped again. */
    browsePcDevicePending: Boolean,
    onOpen: (TreeRow) -> Unit,
    onToggle: (TreeRow) -> Unit,
    onAddPhoneFolder: () -> Unit,
    onSetShared: (String, Boolean) -> Unit,
    onRemoveBookmark: (String) -> Unit,
    onBrowsePcDevice: () -> Unit,
    modifier: Modifier = Modifier,
) {
    LazyColumn(modifier = modifier, contentPadding = androidx.compose.foundation.layout.PaddingValues(vertical = 8.dp)) {
        rows.forEachIndexed { index, row ->
            item(key = row.key) {
                TreeRowItem(
                    row = row,
                    selected = row.key == selectedKey,
                    onOpen = { onOpen(row) },
                    onToggle = { onToggle(row) },
                    onSetShared = onSetShared,
                    onRemoveBookmark = onRemoveBookmark,
                )
            }
            val next = rows.getOrNull(index + 1)
            val lastOfSide = next == null || next.side != row.side
            if (!lastOfSide) return@forEachIndexed
            // Closing rows for each device: what can be added there, or why it's empty.
            val deviceOpen = rows.firstOrNull { it.side == row.side && it.kind == TreeRow.Kind.Device }?.expanded == true
            if (!deviceOpen) return@forEachIndexed
            when (row.side) {
                FileSide.Phone -> item(key = "phone-add") {
                    ActionRow(Icons.Default.Add, stringResource(R.string.files_add_folder), onAddPhoneFolder)
                }
                FileSide.Pc -> when {
                    !pcConnected -> item(key = "pc-offline") { NoteRow(stringResource(R.string.files_pc_not_connected)) }
                    canBrowsePcDevice && rows.none { it.side == FileSide.Pc && it.root?.isVolume == true } ->
                        item(key = "pc-volumes") {
                            ActionRow(
                                Icons.Default.Storage,
                                stringResource(R.string.file_manager_browse_device),
                                onBrowsePcDevice,
                                enabled = FileManagerLogic.browseDeviceEnabled(canBrowsePcDevice, browsePcDevicePending),
                            )
                        }
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun TreeRowItem(
    row: TreeRow,
    selected: Boolean,
    onOpen: () -> Unit,
    onToggle: () -> Unit,
    onSetShared: (String, Boolean) -> Unit,
    onRemoveBookmark: (String) -> Unit,
) {
    val isDevice = row.kind == TreeRow.Kind.Device
    val label = when {
        isDevice && row.side == FileSide.Phone -> stringResource(R.string.files_this_phone)
        isDevice -> stringResource(R.string.files_pc)
        else -> row.label.ifBlank { stringResource(R.string.files_unnamed_folder) }
    }
    val icon = when {
        isDevice && row.side == FileSide.Phone -> Icons.Default.PhoneAndroid
        isDevice -> Icons.Default.Computer
        row.root?.isVolume == true && row.kind == TreeRow.Kind.Root -> Icons.Default.Storage
        row.kind == TreeRow.Kind.Root && row.root?.isShared == true && row.side == FileSide.Phone -> Icons.Default.FolderShared
        else -> Icons.Default.Folder
    }
    Surface(
        color = if (selected) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surface.copy(alpha = 0f),
        shape = RoundedCornerShape(24.dp),
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 8.dp)
            .semantics { this.selected = selected },
    ) {
        Row(
            modifier = Modifier
                .clickable(role = Role.Button, onClick = onOpen)
                .heightIn(min = 48.dp)
                .padding(start = (12 + (row.depth - 1).coerceAtLeast(0) * 16).dp, end = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Icon(
                icon,
                contentDescription = null,
                tint = if (isDevice) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(20.dp),
            )
            Text(
                text = label,
                style = if (isDevice) MaterialTheme.typography.titleSmallEmphasized else MaterialTheme.typography.bodyMedium,
                color = if (selected) MaterialTheme.colorScheme.onSecondaryContainer else MaterialTheme.colorScheme.onSurface,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            if (row.side == FileSide.Phone && row.kind == TreeRow.Kind.Root && row.root != null) {
                PhoneFolderMenu(row.root, onSetShared, onRemoveBookmark)
            }
            Expander(row, onToggle)
        }
    }
}

@Composable
private fun Expander(row: TreeRow, onToggle: () -> Unit) {
    val description = stringResource(if (row.expanded) R.string.files_tree_collapse else R.string.files_tree_expand)
    when {
        row.loading -> Box(Modifier.size(48.dp), contentAlignment = Alignment.Center) {
            RemexLoadingIndicator(modifier = Modifier.size(24.dp))
        }
        row.failed -> IconButton(onClick = onToggle, shapes = rememberRemexIconButtonShapes()) {
            Icon(
                Icons.Default.ErrorOutline,
                contentDescription = stringResource(R.string.files_tree_load_failed),
                tint = MaterialTheme.colorScheme.error,
            )
        }
        !row.expandable -> Spacer(Modifier.width(48.dp))
        else -> IconButton(onClick = onToggle, shapes = rememberRemexIconButtonShapes()) {
            Icon(
                if (row.expanded) Icons.Default.KeyboardArrowDown else Icons.AutoMirrored.Filled.KeyboardArrowRight,
                contentDescription = description,
            )
        }
    }
}

/** A phone folder's menu: share it with the PC or not, and (for a bookmark) take it off this list. */
@Composable
private fun PhoneFolderMenu(root: TreeRootInfo, onSetShared: (String, Boolean) -> Unit, onRemoveBookmark: (String) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { open = true }, shapes = rememberRemexIconButtonShapes()) {
            Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.files_folder_options, root.label))
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            DropdownMenuItem(
                text = { Text(stringResource(R.string.files_share_with_pc)) },
                // The row is the one target; the switch only shows the state.
                trailingIcon = { Switch(checked = root.isShared, onCheckedChange = null) },
                onClick = { onSetShared(root.rootId, !root.isShared) },
            )
            if (root.isBookmark) {
                DropdownMenuItem(
                    text = { Text(stringResource(R.string.files_remove_bookmark)) },
                    onClick = { open = false; onRemoveBookmark(root.rootId) },
                )
            }
        }
    }
}

@Composable
private fun ActionRow(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    label: String,
    onClick: () -> Unit,
    enabled: Boolean = true,
) {
    val color = if (enabled) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface.copy(alpha = 0.38f)
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 8.dp)
            .clickable(enabled = enabled, role = Role.Button, onClick = onClick)
            .heightIn(min = 48.dp)
            .padding(start = 28.dp, end = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Icon(icon, contentDescription = null, tint = color, modifier = Modifier.size(20.dp))
        Text(label, style = MaterialTheme.typography.labelLarge, color = color)
    }
}

@Composable
private fun NoteRow(text: String) {
    Text(
        text = text,
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.padding(start = 44.dp, end = 12.dp, top = 8.dp, bottom = 8.dp),
    )
}
