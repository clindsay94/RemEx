package com.clindsay94.remex.ui.screens

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.InsertDriveFile
import androidx.compose.material.icons.filled.CheckBox
import androidx.compose.material.icons.filled.CheckBoxOutlineBlank
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.CloudOff
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.DriveFileRenameOutline
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.SelectAll
import androidx.compose.material.icons.filled.Upload
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExposedDropdownMenuAnchorType
import androidx.compose.material3.ExposedDropdownMenuBox
import androidx.compose.material3.ExposedDropdownMenuDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberBottomSheetState
import androidx.compose.material3.SheetValue
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.ui.components.RemexScreenHeader
import com.clindsay94.remex.ui.theme.calculateAdaptivePadding

@OptIn(ExperimentalMaterial3Api::class, ExperimentalFoundationApi::class)
@Composable
fun FileTransferScreen(
        onNavigateToConnection: () -> Unit = {},
        vm: FileTransferViewModel = viewModel(),
) {
    val isConnected by RemexClientManager.isConnected.collectAsState()
    val remotePath by vm.remotePath.collectAsState()
    val remoteEntries by vm.remoteEntries.collectAsState()
    val remoteRoots by vm.remoteRoots.collectAsState()
    val selectedRootId by vm.selectedRootId.collectAsState()
    val isLoading by vm.isLoading.collectAsState()
    val isTransferring by vm.isTransferring.collectAsState()
    val transferProgress by vm.transferProgress.collectAsState()
    val statusText by vm.statusText.collectAsState()
    val isSelectionMode by vm.isSelectionMode.collectAsState()
    val selectedEntryNames by vm.selectedEntryNames.collectAsState()

    FileTransferScreenContent(
        isConnected = isConnected,
        remotePath = remotePath,
        remoteEntries = remoteEntries,
        remoteRoots = remoteRoots,
        selectedRootId = selectedRootId,
        isLoading = isLoading,
        isTransferring = isTransferring,
        transferProgress = transferProgress,
        statusText = statusText,
        isSelectionMode = isSelectionMode,
        selectedEntryNames = selectedEntryNames,
        onNavigateToConnection = onNavigateToConnection,
        onSelectRoot = vm::selectRoot,
        onRefreshRoots = vm::loadRemoteRoots,
        onBrowseRemote = vm::browseRemote,
        onCancelTransfer = vm::cancelTransfer,
        onNavigateInto = vm::navigateInto,
        onToggleEntrySelection = vm::toggleEntrySelection,
        onEnterSelectionMode = vm::enterSelectionMode,
        onClearSelection = vm::clearSelection,
        onSelectAll = vm::selectAll,
        onDeleteSelectedEntries = vm::deleteSelectedEntries,
        onUploadFromUri = vm::uploadFromUri,
        onDownloadToUri = vm::downloadToUri,
        onRenameEntry = vm::renameEntry,
        onDeleteEntry = vm::deleteEntry,
        onPinCurrentFolder = vm::pinCurrentFolder
    )
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalFoundationApi::class)
@Composable
fun FileTransferScreenContent(
    isConnected: Boolean,
    remotePath: String,
    remoteEntries: List<RemoteFileEntry>,
    remoteRoots: List<RemoteSharedRoot>,
    selectedRootId: String?,
    isLoading: Boolean,
    isTransferring: Boolean,
    transferProgress: Float,
    statusText: String,
    isSelectionMode: Boolean,
    selectedEntryNames: Set<String>,
    onNavigateToConnection: () -> Unit,
    onSelectRoot: (String) -> Unit,
    onRefreshRoots: () -> Unit,
    onBrowseRemote: () -> Unit,
    onCancelTransfer: () -> Unit,
    onNavigateInto: (RemoteFileEntry) -> Unit,
    onToggleEntrySelection: (RemoteFileEntry) -> Unit,
    onEnterSelectionMode: (RemoteFileEntry) -> Unit,
    onClearSelection: () -> Unit,
    onSelectAll: () -> Unit,
    onDeleteSelectedEntries: () -> Unit,
    onUploadFromUri: (android.net.Uri) -> Unit,
    onDownloadToUri: (RemoteFileEntry, android.net.Uri) -> Unit,
    onRenameEntry: (RemoteFileEntry, String) -> Unit,
    onDeleteEntry: (RemoteFileEntry) -> Unit,
    onPinCurrentFolder: () -> Unit
) {
    val selectedRoot = remoteRoots.firstOrNull { it.rootId == selectedRootId }
    val padding = calculateAdaptivePadding(1f)

    // ── File pickers ──────────────────────────────────────────────────────────
    val uploadLauncher =
            rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
                if (uri != null) onUploadFromUri(uri)
            }

    var pendingDownloadEntry by remember { mutableStateOf<RemoteFileEntry?>(null) }
    val createDocumentLauncher =
            rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("*/*")) { uri ->
                val entry = pendingDownloadEntry
                pendingDownloadEntry = null
                if (uri != null && entry != null) onDownloadToUri(entry, uri)
            }

    // ── Rename dialog ─────────────────────────────────────────────────────────
    var renameTarget by remember { mutableStateOf<RemoteFileEntry?>(null) }
    var renameText by remember { mutableStateOf("") }

    if (renameTarget != null) {
        AlertDialog(
                onDismissRequest = { renameTarget = null },
                title = { Text(stringResource(R.string.file_transfer_rename_title)) },
                text = {
                    OutlinedTextField(
                            value = renameText,
                            onValueChange = { renameText = it },
                            label = { Text(stringResource(R.string.file_transfer_rename_hint)) },
                            singleLine = true,
                            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                            keyboardActions = KeyboardActions(onDone = {
                                onRenameEntry(renameTarget!!, renameText)
                                renameTarget = null
                            }),
                    )
                },
                confirmButton = {
                    TextButton(onClick = {
                        onRenameEntry(renameTarget!!, renameText)
                        renameTarget = null
                    }) { Text(stringResource(R.string.file_transfer_rename_confirm)) }
                },
                dismissButton = {
                    TextButton(onClick = { renameTarget = null }) {
                        Text(stringResource(R.string.dialog_cancel))
                    }
                },
        )
    }

    // ── Context menu bottom sheet ─────────────────────────────────────────────
    var contextMenuEntry by remember { mutableStateOf<RemoteFileEntry?>(null) }
    if (contextMenuEntry != null) {
        val entry = contextMenuEntry!!
        ModalBottomSheet(
                onDismissRequest = { contextMenuEntry = null },
                sheetState = rememberBottomSheetState(initialValue = SheetValue.Hidden, skipPartiallyExpanded = true),
        ) {
            Column(modifier = Modifier.padding(bottom = 24.dp)) {
                Text(
                        text = entry.name,
                        style = MaterialTheme.typography.titleMedium,
                        fontWeight = FontWeight.SemiBold,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.padding(horizontal = 24.dp, vertical = 12.dp),
                )
                HorizontalDivider()

                if (!entry.isDirectory) {
                    DropdownMenuItem(
                            text = { Text(stringResource(R.string.file_transfer_download)) },
                            leadingIcon = { Icon(Icons.Default.Download, null) },
                            enabled = !isTransferring,
                            onClick = {
                                contextMenuEntry = null
                                pendingDownloadEntry = entry
                                createDocumentLauncher.launch(entry.name)
                            },
                    )
                }

                if (selectedRoot?.canRename == true) {
                    DropdownMenuItem(
                            text = { Text(stringResource(R.string.file_transfer_rename)) },
                            leadingIcon = { Icon(Icons.Default.DriveFileRenameOutline, null) },
                            onClick = {
                                val captured = entry
                                contextMenuEntry = null
                                renameText = captured.name
                                renameTarget = captured
                            },
                    )
                }

                if (selectedRoot?.canDelete == true) {
                    DropdownMenuItem(
                            text = {
                                Text(
                                        stringResource(R.string.file_transfer_delete),
                                        color = MaterialTheme.colorScheme.error
                                )
                            },
                            leadingIcon = {
                                Icon(Icons.Default.Delete, null, tint = MaterialTheme.colorScheme.error)
                            },
                            onClick = {
                                contextMenuEntry = null
                                onDeleteEntry(entry)
                            },
                    )
                }

                if (entry.isDirectory && entry.name != "..") {
                    DropdownMenuItem(
                            text = { Text(stringResource(R.string.file_transfer_pin_folder)) },
                            leadingIcon = { Icon(Icons.Default.PushPin, null) },
                            onClick = {
                                contextMenuEntry = null
                                onNavigateInto(entry)
                                onPinCurrentFolder()
                            },
                    )
                }
            }
        }
    }

    Scaffold(
            topBar = {
                RemexScreenHeader(
                        title = stringResource(R.string.screen_file_transfer_title),
                        subtitle = selectedRoot?.displayName?.let { "$it • $remotePath" }
                                        ?: remotePath,
                )
            },
    ) { innerPadding ->
        Column(
                modifier = Modifier.fillMaxSize().padding(innerPadding).padding(horizontal = padding),
        ) {
            if (!isConnected) {
                Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    Column(
                            horizontalAlignment = Alignment.CenterHorizontally,
                            verticalArrangement = Arrangement.spacedBy(16.dp),
                    ) {
                        Icon(
                                Icons.Default.CloudOff,
                                contentDescription = null,
                                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.size(48.dp),
                        )
                        Text(
                                text = stringResource(R.string.file_transfer_not_connected),
                                style = MaterialTheme.typography.bodyLarge,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                        Button(onClick = onNavigateToConnection) {
                            Text(stringResource(R.string.button_connect))
                        }
                    }
                }
                return@Scaffold
            }

            SharedRootPicker(
                    roots = remoteRoots,
                    selectedRootId = selectedRootId,
                    onSelectRoot = onSelectRoot,
                    onRefreshRoots = onRefreshRoots,
                    modifier = Modifier.padding(top = 8.dp, bottom = 8.dp),
            )

            // ── Selection mode bar vs normal action bar ───────────────────────
            if (isSelectionMode) {
                Surface(
                        color = MaterialTheme.colorScheme.secondaryContainer,
                        modifier = Modifier.fillMaxWidth(),
                ) {
                    Row(
                            modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp),
                            horizontalArrangement = Arrangement.spacedBy(4.dp),
                            verticalAlignment = Alignment.CenterVertically,
                    ) {
                        IconButton(onClick = { onClearSelection() }) {
                            Icon(Icons.Default.Close, stringResource(R.string.file_transfer_cancel_selection))
                        }
                        Text(
                                text = "${selectedEntryNames.size} ${stringResource(R.string.file_transfer_selected)}",
                                style = MaterialTheme.typography.labelLarge,
                                modifier = Modifier.weight(1f),
                        )
                        IconButton(onClick = { onSelectAll() }) {
                            Icon(Icons.Default.SelectAll, stringResource(R.string.file_transfer_select_all))
                        }
                        if (selectedRoot?.canDelete == true) {
                            IconButton(
                                    onClick = { onDeleteSelectedEntries() },
                                    enabled = selectedEntryNames.isNotEmpty() && !isLoading,
                            ) {
                                Icon(
                                        Icons.Default.Delete,
                                        stringResource(R.string.file_transfer_delete_selected),
                                        tint = if (selectedEntryNames.isNotEmpty() && !isLoading)
                                            MaterialTheme.colorScheme.error
                                        else MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                        }
                    }
                }
            } else {
                Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Button(
                            onClick = { uploadLauncher.launch("*/*") },
                            enabled = selectedRoot?.isWritable == true && !isTransferring,
                            modifier = Modifier.weight(1f),
                    ) {
                        Icon(Icons.Default.Upload, null, Modifier.size(18.dp))
                        Spacer(Modifier.size(6.dp))
                        Text(stringResource(R.string.file_transfer_upload))
                    }
                    OutlinedButton(
                            onClick = { onBrowseRemote() },
                            enabled = selectedRoot != null && !isLoading && !isTransferring,
                            modifier = Modifier.weight(1f),
                    ) {
                        Icon(Icons.Default.Refresh, null, Modifier.size(18.dp))
                        Spacer(Modifier.size(6.dp))
                        Text(stringResource(R.string.file_transfer_browse))
                    }
                    if (isTransferring) {
                        OutlinedButton(onClick = { onCancelTransfer() }, modifier = Modifier.weight(1f)) {
                            Text(stringResource(R.string.file_transfer_cancel))
                        }
                    }
                }
            }

            if (isTransferring) {
                RemexLinearWavyProgress(
                        progress = transferProgress,
                        modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
                )
            }

            if (statusText.isNotBlank()) {
                Text(
                        text = statusText,
                        style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(bottom = 8.dp),
                )
            }

            when {
                isLoading -> {
                    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                        RemexLoadingIndicator(contained = true)
                    }
                }
                remoteRoots.isEmpty() -> {
                    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                        Text(
                                stringResource(R.string.file_transfer_no_shared_folders),
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
                remoteEntries.isEmpty() -> {
                    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                        Text(
                                stringResource(R.string.file_transfer_empty),
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
                else -> {
                    LazyColumn(modifier = Modifier.weight(1f)) {
                        items(remoteEntries, key = { it.name }) { entry ->
                            RemoteFileRow(
                                    entry = entry,
                                    isTransferring = isTransferring,
                                    isSelectionMode = isSelectionMode,
                                    isSelected = entry.name in selectedEntryNames,
                                    canDelete = selectedRoot?.canDelete == true,
                                    canRename = selectedRoot?.canRename == true,
                                    onTap = {
                                        if (isSelectionMode) onToggleEntrySelection(entry)
                                        else onNavigateInto(entry)
                                    },
                                    onLongPress = { onEnterSelectionMode(entry) },
                                    onDownload = {
                                        pendingDownloadEntry = entry
                                        createDocumentLauncher.launch(entry.name)
                                    },
                                    onContextMenu = { contextMenuEntry = entry },
                            )
                            HorizontalDivider(
                                    color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.4f)
                            )
                        }
                    }
                }
            }
        }
    }
}

@Preview(showBackground = true)
@Composable
private fun FileTransferScreenPreview() {
    RemExTheme {
        FileTransferScreenContent(
            isConnected = true,
            remotePath = "C:\\Downloads",
            remoteEntries = listOf(
                RemoteFileEntry("document.pdf", false, 1024 * 1024),
                RemoteFileEntry("photos", true, 0),
                RemoteFileEntry("old_report.docx", false, 512 * 1024)
            ),
            remoteRoots = listOf(
                RemoteSharedRoot("root1", "C Drive", true, true, true, true),
                RemoteSharedRoot("root2", "Downloads", true, true, true, true)
            ),
            selectedRootId = "root2",
            isLoading = false,
            isTransferring = false,
            transferProgress = 0f,
            statusText = "Ready",
            isSelectionMode = false,
            selectedEntryNames = emptySet(),
            onNavigateToConnection = {},
            onSelectRoot = {},
            onRefreshRoots = {},
            onBrowseRemote = {},
            onCancelTransfer = {},
            onNavigateInto = {},
            onToggleEntrySelection = {},
            onEnterSelectionMode = {},
            onClearSelection = {},
            onSelectAll = {},
            onDeleteSelectedEntries = {},
            onUploadFromUri = {},
            onDownloadToUri = { _, _ -> },
            onRenameEntry = { _, _ -> },
            onDeleteEntry = {},
            onPinCurrentFolder = {}
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SharedRootPicker(
        roots: List<RemoteSharedRoot>,
        selectedRootId: String?,
        onSelectRoot: (String) -> Unit,
        onRefreshRoots: () -> Unit,
        modifier: Modifier = Modifier,
) {
    var expanded by remember { mutableStateOf(false) }
    val selectedRoot = roots.firstOrNull { it.rootId == selectedRootId }

    Row(
            modifier = modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalAlignment = Alignment.CenterVertically,
    ) {
        ExposedDropdownMenuBox(
                expanded = expanded,
                onExpandedChange = { expanded = !expanded },
                modifier = Modifier.weight(1f),
        ) {
            OutlinedTextField(
                    value = selectedRoot?.displayName
                                    ?: stringResource(R.string.file_transfer_select_root),
                    onValueChange = {},
                    readOnly = true,
                    label = { Text(stringResource(R.string.file_transfer_shared_folder)) },
                    trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded = expanded) },
                    modifier = Modifier.menuAnchor(
                                    ExposedDropdownMenuAnchorType.PrimaryNotEditable,
                                    enabled = true,
                            )
                            .fillMaxWidth(),
            )
            ExposedDropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                roots.forEach { root ->
                    DropdownMenuItem(
                            text = {
                                Column {
                                    Text(root.displayName)
                                    if (!root.isWritable) {
                                        Text(
                                                stringResource(R.string.file_transfer_read_only_root),
                                                style = MaterialTheme.typography.labelSmall,
                                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                        )
                                    }
                                }
                            },
                            onClick = { expanded = false; onSelectRoot(root.rootId) },
                    )
                }
            }
        }
        IconButton(onClick = onRefreshRoots) {
            Icon(Icons.Default.Refresh, contentDescription = stringResource(R.string.cd_refresh))
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun RemoteFileRow(
        entry: RemoteFileEntry,
        isTransferring: Boolean,
        isSelectionMode: Boolean,
        isSelected: Boolean,
        canDelete: Boolean,
        canRename: Boolean,
        onTap: () -> Unit,
        onLongPress: () -> Unit,
        onDownload: () -> Unit,
        onContextMenu: () -> Unit,
) {
    val hasContextActions = entry.name != ".." && (canDelete || canRename || !entry.isDirectory)

    Row(
            modifier = Modifier
                .fillMaxWidth()
                .combinedClickable(onClick = onTap, onLongClick = onLongPress)
                .padding(horizontal = 16.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        if (isSelectionMode && entry.name != "..") {
            Icon(
                    imageVector = if (isSelected) Icons.Default.CheckBox else Icons.Default.CheckBoxOutlineBlank,
                    contentDescription = null,
                    tint = if (isSelected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.size(24.dp),
            )
        } else {
            Icon(
                    imageVector = if (entry.isDirectory) Icons.Default.Folder else Icons.AutoMirrored.Filled.InsertDriveFile,
                    contentDescription = null,
                    tint = if (entry.isDirectory) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.size(24.dp),
            )
        }

        Column(modifier = Modifier.weight(1f)) {
            Text(
                    text = entry.name,
                    style = MaterialTheme.typography.bodyMedium,
                    fontWeight = if (entry.isDirectory) FontWeight.SemiBold else FontWeight.Normal,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
            )
            if (!entry.isDirectory && entry.sizeBytes > 0) {
                Text(
                        text = formatBytes(entry.sizeBytes),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }

        if (!isSelectionMode && entry.name != "..") {
            if (!entry.isDirectory) {
                IconButton(onClick = onDownload, enabled = !isTransferring) {
                    Icon(
                            Icons.Default.Download,
                            contentDescription = stringResource(R.string.file_transfer_download),
                            tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            if (hasContextActions) {
                IconButton(onClick = onContextMenu) {
                    Icon(
                            Icons.Default.MoreVert,
                            contentDescription = stringResource(R.string.cd_more_options),
                            tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
    }
}

private fun formatBytes(bytes: Long): String =
        when {
            bytes >= 1_073_741_824L -> "%.1f GB".format(bytes / 1_073_741_824.0)
            bytes >= 1_048_576L -> "%.1f MB".format(bytes / 1_048_576.0)
            bytes >= 1_024L -> "%.1f KB".format(bytes / 1_024.0)
            else -> "$bytes B"
        }
