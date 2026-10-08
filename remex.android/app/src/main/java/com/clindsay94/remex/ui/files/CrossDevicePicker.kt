package com.clindsay94.remex.ui.files

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.InsertDriveFile
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Storage
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.screens.FileManagerLogic
import com.clindsay94.remex.ui.screens.RemexLoadingIndicator
import com.clindsay94.remex.ui.screens.RemoteFileEntry
import com.clindsay94.remex.ui.theme.rememberRemexButtonShapes
import com.clindsay94.remex.ui.theme.rememberRemexIconButtonShapes
import kotlinx.coroutines.CancellationException

/** What a [CrossDevicePicker] is picking. */
enum class PickMode {
    /** A folder to send or save into: only writable folders, and a "here" button. */
    Folder,

    /** A file to check against: tapping a file picks it. */
    File,
}

/**
 * Browses the OTHER device's folders to pick a destination or a file (file browser redesign, 2026-10-08). It is
 * the same tree as the side pane, one level at a time, starting in the last folder looked at over there.
 *
 * @param list the items of one folder on [side]; throws when it can't be listed.
 * @param onPicked the chosen root id, folder path, and (for [PickMode.File]) the chosen file.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun CrossDevicePicker(
    title: String,
    confirmLabel: String,
    side: FileSide,
    mode: PickMode,
    roots: List<TreeRootInfo>,
    start: Pair<String, String>?,
    list: suspend (FileSide, String, String) -> List<RemoteFileEntry>,
    onPicked: (rootId: String, folder: String, file: RemoteFileEntry?) -> Unit,
    onDismiss: () -> Unit,
) {
    val usable = if (mode == PickMode.Folder) roots.filter { it.isWritable } else roots
    var rootId by remember { mutableStateOf(start?.first?.takeIf { s -> usable.any { it.rootId == s } }) }
    var path by remember { mutableStateOf(if (rootId != null) start?.second.orEmpty() else "") }
    var entries by remember { mutableStateOf<List<RemoteFileEntry>?>(null) }
    var failed by remember { mutableStateOf(false) }

    LaunchedEffect(rootId, path) {
        val root = rootId ?: return@LaunchedEffect
        entries = null
        failed = false
        entries = try {
            FileManagerLogic.sortEntries(
                list(side, root, path).filter { mode == PickMode.File || it.isDirectory },
                com.clindsay94.remex.ui.screens.SortOption(),
            )
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            failed = true
            emptyList()
        }
    }

    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(modifier = Modifier.padding(bottom = 16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(horizontal = 8.dp)) {
                if (rootId != null) {
                    IconButton(
                        onClick = {
                            if (path.isEmpty()) rootId = null
                            else path = path.substringBeforeLast('/', "")
                        },
                        shapes = rememberRemexIconButtonShapes(),
                    ) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.files_picker_up))
                    }
                }
                Column(Modifier.weight(1f).padding(horizontal = 8.dp)) {
                    Text(title, style = MaterialTheme.typography.titleMediumEmphasized)
                    val where = rootId?.let { id ->
                        val label = roots.firstOrNull { it.rootId == id }?.label.orEmpty()
                        if (path.isEmpty()) label else "$label/$path"
                    } ?: stringResource(if (side == FileSide.Pc) R.string.files_pc else R.string.files_this_phone)
                    Text(where, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
            HorizontalDivider()
            Box(Modifier.fillMaxWidth().height(360.dp)) {
                val root = rootId
                when {
                    root == null && usable.isEmpty() -> PickerNote(stringResource(R.string.files_picker_no_folders))
                    root == null -> LazyColumn {
                        items(usable, key = { it.rootId }) { r ->
                            PickerRow(if (r.isVolume) Icons.Default.Storage else Icons.Default.Folder, r.label) { rootId = r.rootId; path = "" }
                        }
                    }
                    entries == null -> Box(Modifier.fillMaxWidth().height(360.dp), contentAlignment = Alignment.Center) {
                        RemexLoadingIndicator(contained = true)
                    }
                    failed -> PickerNote(stringResource(R.string.file_transfer_browse_error))
                    entries.orEmpty().isEmpty() -> PickerNote(stringResource(R.string.files_picker_empty))
                    else -> LazyColumn {
                        items(entries.orEmpty(), key = { it.id ?: it.name }) { e ->
                            PickerRow(if (e.isDirectory) Icons.Default.Folder else Icons.AutoMirrored.Filled.InsertDriveFile, e.name) {
                                if (e.isDirectory) path = if (path.isEmpty()) e.name else "$path/${e.name}"
                                else onPicked(root, path, e)
                            }
                        }
                    }
                }
            }
            if (mode == PickMode.Folder) {
                Button(
                    onClick = { rootId?.let { onPicked(it, path, null) } },
                    enabled = rootId != null,
                    shapes = rememberRemexButtonShapes(),
                    contentPadding = ButtonDefaults.ContentPadding,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
                ) {
                    Text(confirmLabel)
                }
            }
        }
    }
}

@Composable
private fun PickerRow(icon: androidx.compose.ui.graphics.vector.ImageVector, label: String, onClick: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(role = Role.Button, onClick = onClick)
            .heightIn(min = 48.dp)
            .padding(horizontal = 24.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(20.dp))
        Text(label, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

@Composable
private fun PickerNote(text: String) {
    Box(Modifier.fillMaxWidth().height(360.dp).padding(24.dp), contentAlignment = Alignment.Center) {
        Text(text, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}
