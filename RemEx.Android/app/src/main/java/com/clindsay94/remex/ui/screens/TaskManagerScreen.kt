package com.clindsay94.remex.ui.screens

import androidx.compose.animation.*
import android.view.HapticFeedbackConstants
import androidx.compose.ui.platform.LocalView
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
import androidx.compose.material3.*
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.material3.pulltorefresh.rememberPullToRefreshState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager

data class TaskManagerUiState(
    val processes: List<ProcessInfo> = emptyList(),
    val searchQuery: String = "",
    val sortField: ProcessSortField = ProcessSortField.NAME,
    val sortDescending: Boolean = false,
    val shapePreset: Float = 0f,
    val cornerRadius: Int = 8,
    val isConnected: Boolean = false,
    val isRefreshing: Boolean = false
)

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
    val isRefreshing by viewModel.isRefreshing.collectAsState()

    val uiState = TaskManagerUiState(
        processes = processes,
        searchQuery = searchQuery,
        sortField = sortField,
        sortDescending = sortDescending,
        shapePreset = shapePreset,
        cornerRadius = cornerRadius,
        isConnected = isConnected,
        isRefreshing = isRefreshing
    )

    TaskManagerScreenContent(
        uiState = uiState,
        onRefreshProcesses = { viewModel.refreshProcesses() },
        onUpdateSearchQuery = { viewModel.updateSearchQuery(it) },
        onUpdateSortField = { viewModel.updateSortField(it) },
        onKillProcess = { viewModel.killProcess(it) },
        onNavigateToConnection = onNavigateToConnection
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TaskManagerScreenContent(
    uiState: TaskManagerUiState,
    onRefreshProcesses: () -> Unit,
    onUpdateSearchQuery: (String) -> Unit,
    onUpdateSortField: (ProcessSortField) -> Unit,
    onKillProcess: (Int) -> Unit,
    onNavigateToConnection: () -> Unit,
    modifier: Modifier = Modifier
) {
    val view = LocalView.current

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.screen_task_manager_title)) },
                actions = {
                    IconButton(onClick = {
                        view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                        onRefreshProcesses()
                    }) {
                        Icon(Icons.Default.Refresh, contentDescription = stringResource(R.string.cd_refresh))
                    }
                }
            )
        }
    ) { innerPadding ->
        PullToRefreshBox(
            isRefreshing = uiState.isRefreshing,
            onRefresh = onRefreshProcesses,
            modifier = modifier.fillMaxSize().padding(innerPadding)
        ) {
            Column(modifier = Modifier.fillMaxSize()) {
                NotConnectedBanner(
                    isConnected = uiState.isConnected,
                    onNavigateToConnection = onNavigateToConnection
                )

                OutlinedTextField(
                    value = uiState.searchQuery,
                    onValueChange = onUpdateSearchQuery,
                    label = { Text(stringResource(R.string.task_manager_search_hint)) },
                    singleLine = true,
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 16.dp, vertical = 8.dp),
                    shape = com.clindsay94.remex.ui.theme.cardShape(uiState.shapePreset, uiState.cornerRadius)
                )

                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 16.dp),
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    SortChip(stringResource(R.string.sort_name), uiState.sortField == ProcessSortField.NAME, uiState.sortDescending) {
                        view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                        onUpdateSortField(ProcessSortField.NAME)
                    }
                    SortChip(stringResource(R.string.sort_cpu), uiState.sortField == ProcessSortField.CPU, uiState.sortDescending) {
                        view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                        onUpdateSortField(ProcessSortField.CPU)
                    }
                    SortChip(stringResource(R.string.sort_ram), uiState.sortField == ProcessSortField.RAM, uiState.sortDescending) {
                        view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                        onUpdateSortField(ProcessSortField.RAM)
                    }
                    SortChip(stringResource(R.string.sort_pid), uiState.sortField == ProcessSortField.PID, uiState.sortDescending) {
                        view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                        onUpdateSortField(ProcessSortField.PID)
                    }
                }

                if (!uiState.isConnected && uiState.processes.isEmpty()) {
                    DisconnectedFullScreen(
                        screenName = stringResource(R.string.screen_task_manager_title),
                        onNavigateToConnection = onNavigateToConnection,
                        modifier = Modifier.weight(1f)
                    )
                } else if (uiState.processes.isEmpty()) {
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
                        val maxRam = remember(uiState.processes) { uiState.processes.maxOfOrNull { it.ram } ?: 1.0 }
                        LazyColumn(modifier = Modifier.fillMaxSize()) {
                            item {
                                ProcessHeader()
                            }
                            items(uiState.processes, key = { it.id }) { process ->
                                ProcessItem(process = process, maxRam = maxRam, onKill = { onKillProcess(process.id) })
                            }
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
            .background(MaterialTheme.colorScheme.surfaceContainerHigh)
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
    val view = LocalView.current
    var showConfirm by remember { mutableStateOf(false) }

    if (showConfirm) {
        AlertDialog(
            onDismissRequest = { showConfirm = false },
            title = { Text(stringResource(R.string.task_manager_kill_title)) },
            text = { Text(stringResource(R.string.task_manager_kill_message, process.name, process.id)) },
            confirmButton = {
                TextButton(onClick = {
                    view.performHapticFeedback(HapticFeedbackConstants.REJECT)
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
            view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
            showConfirm = true
        }) {
            Icon(
                Icons.Default.Close,
                contentDescription = stringResource(R.string.cd_kill_process),
                tint = MaterialTheme.colorScheme.error
            )
        }
    }
    HorizontalDivider(
        modifier = Modifier.padding(horizontal = 16.dp),
        thickness = 0.5.dp,
        color = MaterialTheme.colorScheme.outlineVariant
    )
}
