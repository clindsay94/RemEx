package com.clindsay94.remex.ui.screens

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
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
    val isLoading by vm.isLoading.collectAsState()
    val isTransferring by vm.isTransferring.collectAsState()
    val transferProgress by vm.transferProgress.collectAsState()
    val statusText by vm.statusText.collectAsState()

    val padding = calculateAdaptivePadding()

    Scaffold(
        topBar = {
            RemexScreenHeader(
                title = stringResource(R.string.screen_file_transfer_title),
                subtitle = remotePath,
            )
        },
    ) { innerPadding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(innerPadding)
                .padding(horizontal = padding),
        ) {
            if (!isConnected) {
                Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
                        Icon(Icons.Default.CloudOff, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(48.dp))
                        Text(
                            text = stringResource(R.string.file_transfer_not_connected),
                            style = MaterialTheme.typography.bodyLarge,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                        Button(onClick = onNavigateToConnection) {
                            Text("Connect")
                        }
                    }
                }
                return@Scaffold
            }

            // Toolbar row
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = 8.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Button(
                    onClick = { vm.browseRemote() },
                    enabled = !isLoading && !isTransferring,
                    modifier = Modifier.weight(1f),
                ) {
                    Icon(Icons.Default.Refresh, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text(stringResource(R.string.file_transfer_browse))
                }
                if (isTransferring) {
                    OutlinedButton(
                        onClick = { vm.cancelTransfer() },
                        modifier = Modifier.weight(1f),
                    ) {
                        Text(stringResource(R.string.file_transfer_cancel))
                    }
                }
            }

            // Progress bar
            if (isTransferring) {
                LinearProgressIndicator(
                    progress = { transferProgress },
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(vertical = 4.dp),
                    strokeCap = androidx.compose.ui.graphics.StrokeCap.Round,
                )
            }

            // Status
            if (statusText.isNotBlank()) {
                Text(
                    text = statusText,
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(bottom = 8.dp),
                )
            }

            // File list
            when {
                isLoading -> {
                    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                        CircularProgressIndicator()
                    }
                }
                remoteEntries.isEmpty() && !isLoading -> {
                    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                        Text(
                            text = if (remotePath.isEmpty()) "Tap Browse to load remote files."
                                   else stringResource(R.string.file_transfer_empty),
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
                                onDownload = { vm.download(entry) },
                            )
                            HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.4f))
                        }
                    }
                }
            }
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
        modifier = Modifier
            .fillMaxWidth()
            .clickable(enabled = entry.isDirectory) { onTap() }
            .padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Icon(
            imageVector = if (entry.isDirectory) Icons.Default.Folder else Icons.Default.InsertDriveFile,
            contentDescription = null,
            tint = if (entry.isDirectory) MaterialTheme.colorScheme.primary
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
                    contentDescription = "Download",
                    tint = MaterialTheme.colorScheme.primary,
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

private fun formatBytes(bytes: Long): String = when {
    bytes >= 1_073_741_824L -> "%.1f GB".format(bytes / 1_073_741_824.0)
    bytes >= 1_048_576L     -> "%.1f MB".format(bytes / 1_048_576.0)
    bytes >= 1_024L         -> "%.1f KB".format(bytes / 1_024.0)
    else                    -> "$bytes B"
}
