package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.animation.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.ui.theme.calculateAdaptivePadding
import com.clindsay94.remex.ui.theme.cardShape

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
    val processList by viewModel.processes.collectAsState(initial = emptyList())
    val isConnected by viewModel.isConnected.collectAsState(initial = false)
    val isRefreshing by viewModel.isRefreshing.collectAsState(initial = false)
    val searchQuery by viewModel.searchQuery.collectAsState()
    val sortField by viewModel.sortField.collectAsState()
    val sortDescending by viewModel.sortDescending.collectAsState()
    val shapePreset by viewModel.taskManagerCardShapePreset.collectAsState()
    val cornerRadius by viewModel.cardCornerRadius.collectAsState()

    TaskManagerScreenContent(
        uiState = TaskManagerUiState(
            processes = processList,
            searchQuery = searchQuery,
            sortField = sortField,
            sortDescending = sortDescending,
            shapePreset = shapePreset,
            cornerRadius = cornerRadius,
            isConnected = isConnected,
            isRefreshing = isRefreshing
        ),
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
                title = {
                    Column {
                        Text(
                            stringResource(R.string.screen_task_manager_title),
                            style = MaterialTheme.typography.titleLarge,
                            fontWeight = FontWeight.Bold
                        )
                        if (uiState.isConnected) {
                            Text(
                                stringResource(R.string.status_connected),
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.primary
                            )
                        }
                    }
                },
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
                SearchBar(
                    query = uiState.searchQuery,
                    onUpdateQuery = onUpdateSearchQuery,
                    shape = com.clindsay94.remex.ui.theme.cardShape(uiState.shapePreset, uiState.cornerRadius)
                )

                FilterSortSection(
                    currentSortField = uiState.sortField,
                    sortDescending = uiState.sortDescending,
                    onUpdateSortField = onUpdateSortField
                )

                if (!uiState.isConnected && uiState.processes.isEmpty()) {
                    DisconnectedFullScreen(
                        screenName = stringResource(R.string.screen_task_manager_title),
                        onNavigateToConnection = onNavigateToConnection,
                        modifier = Modifier.weight(1f)
                    )
                } else if (uiState.processes.isEmpty() && uiState.isRefreshing) {
                    Box(
                        modifier = Modifier.weight(1f).fillMaxWidth(),
                        contentAlignment = Alignment.Center
                    ) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            CircularProgressIndicator(strokeCap = StrokeCap.Round)
                            Spacer(modifier = Modifier.height(16.dp))
                            Text(
                                stringResource(R.string.task_manager_fetching),
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    }
                } else {
                    val maxRam = remember(uiState.processes) { uiState.processes.maxOfOrNull { it.ram } ?: 1.0 }
                    val maxCpu = remember(uiState.processes) { uiState.processes.maxOfOrNull { it.cpu } ?: 1.0 }

                    LazyColumn(
                        modifier = Modifier.fillMaxSize(),
                        contentPadding = PaddingValues(bottom = 80.dp)
                    ) {
                        items(uiState.processes, key = { it.id }) { process ->
                            ProcessCard(
                                process = process,
                                maxRam = maxRam,
                                maxCpu = maxCpu,
                                shapePreset = uiState.shapePreset,
                                cornerRadius = uiState.cornerRadius,
                                onKill = { onKillProcess(process.id) }
                            )
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun SearchBar(query: String, onUpdateQuery: (String) -> Unit, shape: Shape) {
    Surface(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp, vertical = 8.dp),
        color = MaterialTheme.colorScheme.surfaceContainerLow,
        shape = shape
    ) {
        TextField(
            value = query,
            onValueChange = onUpdateQuery,
            placeholder = { Text(stringResource(R.string.task_manager_search_hint)) },
            leadingIcon = { Icon(Icons.Default.Search, contentDescription = null) },
            trailingIcon = {
                if (query.isNotEmpty()) {
                    IconButton(onClick = { onUpdateQuery("") }) {
                        Icon(Icons.Default.Close, contentDescription = null)
                    }
                }
            },
            singleLine = true,
            colors = TextFieldDefaults.colors(
                focusedContainerColor = Color.Transparent,
                unfocusedContainerColor = Color.Transparent,
                disabledContainerColor = Color.Transparent,
                focusedIndicatorColor = Color.Transparent,
                unfocusedIndicatorColor = Color.Transparent,
            ),
            modifier = Modifier.fillMaxWidth()
        )
    }
}

@Composable
private fun FilterSortSection(
    currentSortField: ProcessSortField,
    sortDescending: Boolean,
    onUpdateSortField: (ProcessSortField) -> Unit
) {
    val view = LocalView.current
    LazyRow(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp, vertical = 4.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        item {
            Text(
                text = stringResource(R.string.sort_by),
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(end = 4.dp)
            )
        }
        val fields = listOf(
            ProcessSortField.CPU to R.string.sort_cpu,
            ProcessSortField.RAM to R.string.sort_ram,
            ProcessSortField.NAME to R.string.sort_name,
            ProcessSortField.PID to R.string.sort_pid
        )
        items(fields) { (field, labelRes) ->
            SortChip(
                label = stringResource(labelRes),
                selected = currentSortField == field,
                descending = sortDescending,
                onClick = {
                    view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                    onUpdateSortField(field)
                }
            )
        }
    }
}

@Composable
private fun SortChip(label: String, selected: Boolean, descending: Boolean, onClick: () -> Unit) {
    FilterChip(
        selected = selected,
        onClick = onClick,
        label = { Text(label) },
        trailingIcon = {
            if (selected) {
                Icon(
                    imageVector = if (descending) Icons.Default.ArrowDownward else Icons.Default.ArrowUpward,
                    contentDescription = null,
                    modifier = Modifier.size(16.dp)
                )
            }
        },
        shape = MaterialTheme.shapes.medium
    )
}

@Composable
private fun ProcessCard(
    process: ProcessInfo,
    maxRam: Double,
    maxCpu: Double,
    shapePreset: Float,
    cornerRadius: Int,
    onKill: () -> Unit
) {
    val view = LocalView.current
    var showConfirm by remember { mutableStateOf(false) }
    val adaptivePadding = calculateAdaptivePadding(shapePreset)

    if (showConfirm) {
        AlertDialog(
            onDismissRequest = { showConfirm = false },
            title = { Text(stringResource(R.string.task_manager_kill_title)) },
            text = { Text(stringResource(R.string.task_manager_kill_message, process.name, process.id)) },
            confirmButton = {
                Button(
                    onClick = {
                        view.performHapticFeedback(HapticFeedbackConstants.REJECT)
                        onKill()
                        showConfirm = false
                    },
                    colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error)
                ) {
                    Text(stringResource(R.string.button_kill))
                }
            },
            dismissButton = {
                TextButton(onClick = { showConfirm = false }) {
                    Text(stringResource(R.string.button_cancel))
                }
            }
        )
    }

    Card(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp, vertical = 6.dp),
        shape = com.clindsay94.remex.ui.theme.cardShape(shapePreset, cornerRadius),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.surfaceContainer
        ),
        onClick = {
            view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
            showConfirm = true
        }
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(adaptivePadding),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = process.name,
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.Bold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Text(
                    text = stringResource(R.string.task_manager_pid_label, process.id),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.primary.copy(alpha = 0.8f),
                    fontWeight = FontWeight.Bold
                )
            }

            Spacer(modifier = Modifier.width(16.dp))

            // Resource Usage Bars
            Column(
                modifier = Modifier.width(100.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                UsageIndicator(
                    label = "CPU",
                    value = "${process.cpu.toInt()}%",
                    progress = (process.cpu / 100.0).toFloat().coerceIn(0f, 1f),
                    color = MaterialTheme.colorScheme.primary
                )
                UsageIndicator(
                    label = "RAM",
                    value = "${process.ram.toInt()}MB",
                    progress = (process.ram / maxRam).toFloat().coerceIn(0f, 1f),
                    color = MaterialTheme.colorScheme.secondary
                )
            }

            Spacer(modifier = Modifier.width(8.dp))

            Icon(
                Icons.Default.ChevronRight,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f),
                modifier = Modifier.size(20.dp)
            )
        }
    }
}

@Composable
private fun UsageIndicator(label: String, value: String, progress: Float, color: Color) {
    Column {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.Bottom
        ) {
            Text(
                text = label,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                fontWeight = FontWeight.Bold
            )
            Text(
                text = value,
                style = MaterialTheme.typography.labelSmall,
                fontWeight = FontWeight.ExtraBold,
                color = color
            )
        }
        Spacer(modifier = Modifier.height(2.dp))
        LinearProgressIndicator(
            progress = { progress },
            modifier = Modifier
                .fillMaxWidth()
                .height(6.dp)
                .clip(CircleShape),
            color = color,
            trackColor = color.copy(alpha = 0.1f),
            strokeCap = StrokeCap.Round
        )
    }
}
