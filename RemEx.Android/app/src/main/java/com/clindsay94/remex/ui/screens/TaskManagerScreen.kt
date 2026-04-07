package com.clindsay94.remex.ui.screens

import androidx.compose.animation.*
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AssistChip
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.RemexClientManager

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TaskManagerScreen(
    onNavigateToConnection: () -> Unit = {},
    viewModel: TaskManagerViewModel = viewModel()
) {
    val processes by viewModel.processes.collectAsState()
    val searchQuery by viewModel.searchQuery.collectAsState()
    val sortField by viewModel.sortField.collectAsState()
    val sortDescending by viewModel.sortDescending.collectAsState()
    val shapePreset by viewModel.taskManagerCardShapePreset.collectAsState()
    val cornerRadius by viewModel.cardCornerRadius.collectAsState()
    val isConnected by RemexClientManager.isConnected.collectAsState()

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Task Manager", fontWeight = FontWeight.Bold) },
                actions = {
                    IconButton(onClick = { viewModel.refreshProcesses() }) {
                        Icon(Icons.Default.Refresh, contentDescription = "Refresh")
                    }
                }
            )
        }
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
        ) {
            NotConnectedBanner(
                isConnected = isConnected,
                onNavigateToConnection = onNavigateToConnection
            )

            OutlinedTextField(
                value = searchQuery,
                onValueChange = viewModel::updateSearchQuery,
                label = { Text("Search process name or PID") },
                singleLine = true,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 8.dp),
                shape = com.clindsay94.remex.ui.theme.cardShape(shapePreset, cornerRadius)
            )

            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                SortChip("Name", sortField == ProcessSortField.NAME, sortDescending) {
                    viewModel.updateSortField(ProcessSortField.NAME)
                }
                SortChip("CPU", sortField == ProcessSortField.CPU, sortDescending) {
                    viewModel.updateSortField(ProcessSortField.CPU)
                }
                SortChip("RAM", sortField == ProcessSortField.RAM, sortDescending) {
                    viewModel.updateSortField(ProcessSortField.RAM)
                }
                SortChip("PID", sortField == ProcessSortField.PID, sortDescending) {
                    viewModel.updateSortField(ProcessSortField.PID)
                }
            }

            if (!isConnected && processes.isEmpty()) {
                DisconnectedFullScreen(
                    screenName = "Task Manager",
                    onNavigateToConnection = onNavigateToConnection
                )
            } else if (processes.isEmpty()) {
                Box(
                    modifier = Modifier.fillMaxSize(),
                    contentAlignment = Alignment.Center
                ) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        CircularProgressIndicator()
                        Spacer(modifier = Modifier.height(16.dp))
                        Text("Fetching processes...", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            } else {
                AnimatedVisibility(
                    visible = true,
                    enter = fadeIn() + expandVertically()
                ) {
                    LazyColumn(modifier = Modifier.fillMaxSize()) {
                        item {
                            ProcessHeader()
                        }
                        items(processes, key = { it.id }) { process ->
                            ProcessItem(process = process, onKill = { viewModel.killProcess(process.id) })
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun SortChip(label: String, selected: Boolean, descending: Boolean, onClick: () -> Unit) {
    AssistChip(
        onClick = onClick,
        label = {
            if (selected) {
                Text("$label ${if (descending) "↓" else "↑"}")
            } else {
                Text(label)
            }
        }
    )
}

@Composable
private fun ProcessHeader() {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surfaceVariant)
            .padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text("Name", modifier = Modifier.weight(1f), style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.Bold)
        Text("CPU", modifier = Modifier.width(60.dp), style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.Bold)
        Text("RAM", modifier = Modifier.width(80.dp), style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.Bold)
        Spacer(modifier = Modifier.width(48.dp))
    }
}

@Composable
private fun ProcessItem(process: ProcessInfo, onKill: () -> Unit) {
    var showConfirm by remember { mutableStateOf(false) }

    if (showConfirm) {
        AlertDialog(
            onDismissRequest = { showConfirm = false },
            title = { Text("Kill Process?") },
            text = { Text("Are you sure you want to terminate ${process.name} (PID: ${process.id})?") },
            confirmButton = {
                TextButton(onClick = { onKill(); showConfirm = false }) {
                    Text("Kill", color = MaterialTheme.colorScheme.error)
                }
            },
            dismissButton = {
                TextButton(onClick = { showConfirm = false }) {
                    Text("Cancel")
                }
            }
        )
    }

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = process.name,
                style = MaterialTheme.typography.bodyMedium,
                fontWeight = FontWeight.Bold,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            Text(
                text = "PID: ${process.id}",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
        Text(text = "${process.cpu.toInt()}%", modifier = Modifier.width(60.dp), style = MaterialTheme.typography.bodyMedium)
        Text(text = "${process.ram.toInt()}MB", modifier = Modifier.width(80.dp), style = MaterialTheme.typography.bodyMedium)

        IconButton(onClick = { showConfirm = true }, modifier = Modifier.size(32.dp)) {
            Icon(
                Icons.Default.Close,
                contentDescription = "Kill",
                tint = MaterialTheme.colorScheme.error,
                modifier = Modifier.size(20.dp)
            )
        }
    }
    HorizontalDivider(
        modifier = Modifier.padding(horizontal = 16.dp),
        thickness = 0.5.dp,
        color = MaterialTheme.colorScheme.outlineVariant
    )
}
