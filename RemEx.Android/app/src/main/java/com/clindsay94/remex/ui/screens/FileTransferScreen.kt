package com.clindsay94.remex.ui.screens

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
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
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.InsertDriveFile
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.CloudOff
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Upload
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExposedDropdownMenuAnchorType
import androidx.compose.material3.ExposedDropdownMenuBox
import androidx.compose.material3.ExposedDropdownMenuDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
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
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.ui.components.RemexScreenHeader
import com.clindsay94.remex.ui.theme.calculateAdaptivePadding

@OptIn(ExperimentalMaterial3Api::class)
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

    val selectedRoot = remoteRoots.firstOrNull { it.rootId == selectedRootId }
    val padding = calculateAdaptivePadding(1f)

    val uploadLauncher =
            rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
                if (uri != null) {
                    vm.uploadFromUri(uri)
                }
            }

    var pendingDownloadEntry by remember { mutableStateOf<RemoteFileEntry?>(null) }
    val createDocumentLauncher =
            rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("*/*")) { uri
                ->
                val entry = pendingDownloadEntry
                pendingDownloadEntry = null
                if (uri != null && entry != null) {
                    vm.downloadToUri(entry, uri)
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
                modifier =
                        Modifier.fillMaxSize().padding(innerPadding).padding(horizontal = padding),
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
                    onSelectRoot = vm::selectRoot,
                    onRefreshRoots = vm::loadRemoteRoots,
                    modifier = Modifier.padding(top = 8.dp, bottom = 8.dp),
            )

            Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Button(
                        onClick = { uploadLauncher.launch("*/*") },
                        enabled = selectedRoot?.isWritable == true && !isTransferring,
                        modifier = Modifier.weight(1f),
                ) {
                    Icon(
                            Icons.Default.Upload,
                            contentDescription = null,
                            modifier = Modifier.size(18.dp)
                    )
                    Spacer(Modifier.size(6.dp))
                    Text(stringResource(R.string.file_transfer_upload))
                }

                OutlinedButton(
                        onClick = { vm.browseRemote() },
                        enabled = selectedRoot != null && !isLoading && !isTransferring,
                        modifier = Modifier.weight(1f),
                ) {
                    Icon(
                            Icons.Default.Refresh,
                            contentDescription = null,
                            modifier = Modifier.size(18.dp)
                    )
                    Spacer(Modifier.size(6.dp))
                    Text(stringResource(R.string.file_transfer_browse))
                }

                if (isTransferring) {
                    OutlinedButton(
                            onClick = { vm.cancelTransfer() },
                            modifier = Modifier.weight(1f),
                    ) { Text(stringResource(R.string.file_transfer_cancel)) }
                }
            }

            if (isTransferring) {
                LinearProgressIndicator(
                        progress = { transferProgress },
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
                        CircularProgressIndicator()
                    }
                }
                remoteRoots.isEmpty() -> {
                    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                        Text(
                                text = stringResource(R.string.file_transfer_no_shared_folders),
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
                remoteEntries.isEmpty() -> {
                    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                        Text(
                                text = stringResource(R.string.file_transfer_empty),
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
                                    onTap = { vm.navigateInto(entry) },
                                    onDownload = {
                                        pendingDownloadEntry = entry
                                        createDocumentLauncher.launch(entry.name)
                                    },
                            )
                            HorizontalDivider(
                                    color =
                                            MaterialTheme.colorScheme.outlineVariant.copy(
                                                    alpha = 0.4f
                                            )
                            )
                        }
                    }
                }
            }
        }
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
                    trailingIcon = {
                        ExposedDropdownMenuDefaults.TrailingIcon(expanded = expanded)
                    },
                    modifier =
                            Modifier.menuAnchor(
                                            ExposedDropdownMenuAnchorType.PrimaryNotEditable,
                                            enabled = true,
                                    )
                                    .fillMaxWidth(),
            )

            ExposedDropdownMenu(
                    expanded = expanded,
                    onDismissRequest = { expanded = false },
            ) {
                roots.forEach { root ->
                    DropdownMenuItem(
                            text = {
                                Column {
                                    Text(root.displayName)
                                    if (!root.isWritable) {
                                        Text(
                                                text =
                                                        stringResource(
                                                                R.string
                                                                        .file_transfer_read_only_root
                                                        ),
                                                style = MaterialTheme.typography.labelSmall,
                                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                        )
                                    }
                                }
                            },
                            onClick = {
                                expanded = false
                                onSelectRoot(root.rootId)
                            },
                    )
                }
            }
        }

        IconButton(onClick = onRefreshRoots) {
            Icon(Icons.Default.Refresh, contentDescription = stringResource(R.string.cd_refresh))
        }
    }
}

@Composable
private fun RemoteFileRow(
        entry: RemoteFileEntry,
        isTransferring: Boolean,
        onTap: () -> Unit,
        onDownload: () -> Unit,
) {
    Row(
            modifier =
                    Modifier.fillMaxWidth()
                            .clickable(enabled = entry.isDirectory) { onTap() }
                            .padding(horizontal = 16.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Icon(
                imageVector =
                        if (entry.isDirectory) Icons.Default.Folder
                        else Icons.AutoMirrored.Filled.InsertDriveFile,
                contentDescription = null,
                tint =
                        if (entry.isDirectory) MaterialTheme.colorScheme.primary
                        else MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(24.dp),
        )

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

        if (!entry.isDirectory) {
            IconButton(
                    onClick = onDownload,
                    enabled = !isTransferring,
            ) {
                Icon(
                        imageVector = Icons.Default.Download,
                        contentDescription = stringResource(R.string.file_transfer_download),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        } else if (entry.name != "..") {
            Icon(
                    imageVector = Icons.Default.ChevronRight,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.size(20.dp),
            )
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
