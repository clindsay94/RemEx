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
import androidx.compose.material3.LinearProgressIndicator
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
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.ui.components.RemexScreenHeader

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TaskManagerScreen(
    onNavigateToConnection: () -> Unit = {},
    viewModel: TaskManagerViewModel = viewModel()
) {
    val haptic = LocalHapticFeedback.current
    val processes by viewModel.processes.collectAsState()
    val searchQuery by viewModel.searchQuery.collectAsState()
    val sortField by viewModel.sortField.collectAsState()
    val sortDescending by viewModel.sortDescending.collectAsState()
    val shapePreset by viewModel.taskManagerCardShapePreset.collectAsState()
    val cornerRadius by viewModel.cardCornerRadius.collectAsState()
    val isConnected by RemexClientManager.isConnected.collectAsState()

    Column(modifier = Modifier.fillMaxSize()) {
        RemexScreenHeader(
            title = stringResource(R.string.screen_task_manager_title),
            actions = {
                IconButton(onClick = {
                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                    viewModel.refreshProcesses()
                }) {
                    Icon(Icons.Default.Refresh, contentDescription = stringResource(R.string.cd_refresh))
                }
            }
        )
        Column(
            modifier = Modifier.fillMaxSize()
        ) {
            NotConnectedBanner(
                isConnected = isConnected,
                onNavigateToConnection = onNavigateToConnection
            )

            OutlinedTextField(
                value = searchQuery,
                onValueChange = viewModel::updateSearchQuery,
                label = { Text(stringResource(R.string.task_manager_search_hint)) },
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
                SortChip(stringResource(R.string.sort_name), sortField == ProcessSortField.NAME, sortDescending) {
                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                    viewModel.updateSortField(ProcessSortField.NAME)
                }
                SortChip(stringResource(R.string.sort_cpu), sortField == ProcessSortField.CPU, sortDescending) {
                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                    viewModel.updateSortField(ProcessSortField.CPU)
                }
                SortChip(stringResource(R.string.sort_ram), sortField == ProcessSortField.RAM, sortDescending) {
                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                    viewModel.updateSortField(ProcessSortField.RAM)
                }
                SortChip(stringResource(R.string.sort_pid), sortField == ProcessSortField.PID, sortDescending) {
                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                    viewModel.updateSortField(ProcessSortField.PID)
                }
            }

            if (!isConnected && processes.isEmpty()) {
                DisconnectedFullScreen(
                    screenName = stringResource(R.string.screen_task_manager_title),
                    onNavigateToConnection = onNavigateToConnection,
                    modifier = Modifier.weight(1f)
                )
            } else if (processes.isEmpty()) {
                Box(
                    modifier = Modifier.weight(1f),
                    contentAlignment = Alignment.Center
                ) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        CircularProgressIndicator()
                        Spacer(modifier = Modifier.height(16.dp))
                        Text(stringResource(R.string.task_manager_fetching), color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            } else {
                AnimatedVisibility(
                    visible = true,
                    enter = fadeIn() + expandVertically()
                ) {
                    val maxRam = remember(processes) { processes.maxOfOrNull { it.ram } ?: 1.0 }
                    LazyColumn(modifier = Modifier.fillMaxSize()) {
                        item {
                            ProcessHeader()
                        }
                        items(processes, key = { it.id }) { process ->
                            ProcessItem(process = process, maxRam = maxRam, onKill = { viewModel.killProcess(process.id) })
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
        Text(stringResource(R.string.task_manager_header_name), modifier = Modifier.weight(1f), style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.Bold)
        Text(stringResource(R.string.task_manager_header_cpu), modifier = Modifier.width(60.dp), style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.Bold)
        Text(stringResource(R.string.task_manager_header_ram), modifier = Modifier.width(80.dp), style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.Bold)
        Spacer(modifier = Modifier.width(48.dp))
    }
}

@Composable
private fun ProcessItem(process: ProcessInfo, maxRam: Double, onKill: () -> Unit) {
    val haptic = LocalHapticFeedback.current
    var showConfirm by remember { mutableStateOf(false) }

    if (showConfirm) {
        AlertDialog(
            onDismissRequest = { showConfirm = false },
            title = { Text(stringResource(R.string.task_manager_kill_title)) },
            text = { Text(stringResource(R.string.task_manager_kill_message, process.name, process.id)) },
            confirmButton = {
                TextButton(onClick = {
                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                    onKill()
                    showConfirm = false
                }) {
                    Text(stringResource(R.string.button_kill), color = MaterialTheme.colorScheme.error)
                }
            },
            dismissButton = {
                TextButton(onClick = { showConfirm = false }) {
                    Text(stringResource(R.string.button_cancel))
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
                text = stringResource(R.string.task_manager_pid_label, process.id),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
        Column(modifier = Modifier.width(60.dp)) {
            Text(text = "${process.cpu.toInt()}%", style = MaterialTheme.typography.bodyMedium)
            LinearProgressIndicator(
                progress = { (process.cpu / 100.0).toFloat().coerceIn(0f, 1f) },
                modifier = Modifier.fillMaxWidth().height(2.dp),
                color = MaterialTheme.colorScheme.primary,
                trackColor = MaterialTheme.colorScheme.surfaceVariant
            )
        }
        Column(modifier = Modifier.width(80.dp)) {
            Text(text = "${process.ram.toInt()}MB", style = MaterialTheme.typography.bodyMedium)
            LinearProgressIndicator(
                progress = { (process.ram / maxRam).toFloat().coerceIn(0f, 1f) },
                modifier = Modifier.fillMaxWidth().height(2.dp),
                color = MaterialTheme.colorScheme.secondary,
                trackColor = MaterialTheme.colorScheme.surfaceVariant
            )
        }

        IconButton(onClick = {
            haptic.performHapticFeedback(HapticFeedbackType.LongPress)
            showConfirm = true
        }, modifier = Modifier.size(32.dp)) {
            Icon(
                Icons.Default.Close,
                contentDescription = stringResource(R.string.cd_kill_process),
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
