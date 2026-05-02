package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.animation.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
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
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.rememberRemexCollapsingScrollBehavior
import com.clindsay94.remex.ui.theme.calculateAdaptivePadding
import com.clindsay94.remex.ui.theme.cardShape

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TaskManagerScreen(
        onNavigateToConnection: () -> Unit = {},
        isVisible: Boolean = true,
        viewModel: TaskManagerViewModel = viewModel()
) {
    val processes by viewModel.processes.collectAsState()
    val isConnected by RemexClientManager.isConnected.collectAsState()
    val isRefreshing by viewModel.isRefreshing.collectAsState()
    val searchQuery by viewModel.searchQuery.collectAsState()
    val sortField by viewModel.sortField.collectAsState()
    val sortDescending by viewModel.sortDescending.collectAsState()
    val shapePreset by viewModel.taskManagerCardShapePreset.collectAsState()
    val cornerRadius by viewModel.cardCornerRadius.collectAsState()
    val killError by viewModel.killError.collectAsState()

        LaunchedEffect(viewModel, isVisible, isConnected) {
                viewModel.setAutoRefreshEnabled(isVisible && isConnected)
        }

        DisposableEffect(viewModel) {
                onDispose { viewModel.setAutoRefreshEnabled(false) }
        }

    TaskManagerScreenContent(
            processes = processes,
            searchQuery = searchQuery,
            sortField = sortField,
            sortDescending = sortDescending,
            shapePreset = shapePreset,
            cornerRadius = cornerRadius,
            isConnected = isConnected,
            isRefreshing = isRefreshing,
            killError = killError,
            onRefreshProcesses = { viewModel.refreshProcesses() },
            onUpdateSearchQuery = { viewModel.updateSearchQuery(it) },
            onUpdateSortField = { viewModel.updateSortField(it) },
            onKillProcess = { viewModel.killProcess(it) },
            onClearKillError = { viewModel.clearKillError() },
            onNavigateToConnection = onNavigateToConnection
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TaskManagerScreenContent(
        processes: List<ProcessInfo>,
        searchQuery: String,
        sortField: ProcessSortField,
        sortDescending: Boolean,
        shapePreset: Float,
        cornerRadius: Int,
        isConnected: Boolean,
        isRefreshing: Boolean,
        killError: String? = null,
        onRefreshProcesses: () -> Unit,
        onUpdateSearchQuery: (String) -> Unit,
        onUpdateSortField: (ProcessSortField) -> Unit,
        onKillProcess: (Int) -> Unit,
        onClearKillError: () -> Unit = {},
        onNavigateToConnection: () -> Unit,
        modifier: Modifier = Modifier
) {
    val view = LocalView.current

    val snackbarHostState = remember { SnackbarHostState() }
    LaunchedEffect(killError) {
        if (killError != null) {
            snackbarHostState.showSnackbar(killError)
            onClearKillError()
        }
    }

    // M3: exit-until-collapsed topbar with nestedScroll for immersive list experience
    val scrollBehavior = rememberRemexCollapsingScrollBehavior()
    Scaffold(
            modifier = modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
            topBar = {
                RemexFlexibleTopBar(
                        title = stringResource(R.string.screen_task_manager_title),
                        scrollBehavior = scrollBehavior,
                        actions = {
                            IconButton(
                                    onClick = {
                                        view.performHapticFeedback(
                                                HapticFeedbackConstants.KEYBOARD_TAP
                                        )
                                        onRefreshProcesses()
                                    }
                            ) {
                                Icon(
                                        Icons.Default.Refresh,
                                        contentDescription = stringResource(R.string.cd_refresh)
                                )
                            }
                        }
                )
            },
            snackbarHost = { SnackbarHost(snackbarHostState) }
    ) { innerPadding ->
        PullToRefreshBox(
                isRefreshing = isRefreshing,
                onRefresh = onRefreshProcesses,
                modifier = Modifier.fillMaxSize().padding(innerPadding)
        ) {
            Column(modifier = Modifier.fillMaxSize()) {
                NotConnectedBanner(
                        isConnected = isConnected,
                        onNavigateToConnection = onNavigateToConnection
                )

                SearchBar(
                        query = searchQuery,
                        onUpdateQuery = onUpdateSearchQuery,
                        shape = cardShape(shapePreset, cornerRadius)
                )

                FilterSortSection(
                        currentSortField = sortField,
                        sortDescending = sortDescending,
                        onUpdateSortField = onUpdateSortField
                )

                if (!isConnected && processes.isEmpty()) {
                    DisconnectedFullScreen(
                            screenName = stringResource(R.string.screen_task_manager_title),
                            onNavigateToConnection = onNavigateToConnection,
                            modifier = Modifier.weight(1f)
                    )
                } else if (processes.isEmpty() && isRefreshing) {
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
                    val maxRam = remember(processes) { processes.maxOfOrNull { it.ram } ?: 1.0 }
                    val maxCpu = remember(processes) { processes.maxOfOrNull { it.cpu } ?: 1.0 }
                    LazyColumn(
                            modifier = Modifier.fillMaxSize(),
                            contentPadding = PaddingValues(bottom = 80.dp)
                    ) {
                        items(processes, key = { it.id }) { process ->
                            ProcessCard(
                                    process = process,
                                    maxRam = maxRam,
                                    maxCpu = maxCpu,
                                    shapePreset = shapePreset,
                                    cornerRadius = cornerRadius,
                                    onKill = { onKillProcess(process.id) },
                                    modifier = Modifier.animateItem()
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
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
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
                colors =
                        TextFieldDefaults.colors(
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
@OptIn(ExperimentalMaterial3Api::class)
private fun FilterSortSection(
        currentSortField: ProcessSortField,
        sortDescending: Boolean,
        onUpdateSortField: (ProcessSortField) -> Unit
) {
    val view = LocalView.current
    val fields =
            listOf(
                    ProcessSortField.CPU to R.string.sort_cpu,
                    ProcessSortField.RAM to R.string.sort_ram,
                    ProcessSortField.NAME to R.string.sort_name,
                    ProcessSortField.PID to R.string.sort_pid
            )
    Row(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        Text(
                text = stringResource(R.string.sort_by),
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        // M3: SegmentedButton for sort field selection (4 items — fits well)
        SingleChoiceSegmentedButtonRow(modifier = Modifier.weight(1f)) {
            fields.forEachIndexed { index, (field, labelRes) ->
                SegmentedButton(
                        selected = currentSortField == field,
                        onClick = {
                            view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                            onUpdateSortField(field)
                        },
                        shape = SegmentedButtonDefaults.itemShape(index, fields.size),
                        label = { Text(stringResource(labelRes)) }
                )
            }
        }
        // Sort direction IconButton stays separate for clear affordance
        IconButton(
                onClick = {
                    view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                    onUpdateSortField(currentSortField) // same field → toggles descending
                }
        ) {
            Icon(
                    imageVector =
                            if (sortDescending) Icons.Default.ArrowDownward
                            else Icons.Default.ArrowUpward,
                    contentDescription = if (sortDescending) "Descending" else "Ascending",
                    modifier = Modifier.size(20.dp)
            )
        }
    }
}

@Composable
private fun ProcessCard(
        process: ProcessInfo,
        maxRam: Double,
        maxCpu: Double,
        shapePreset: Float,
        cornerRadius: Int,
        onKill: () -> Unit,
        modifier: Modifier = Modifier
) {
    val view = LocalView.current
    var showConfirm by remember { mutableStateOf(false) }
    val adaptivePadding = calculateAdaptivePadding(shapePreset)

    if (showConfirm) {
        AlertDialog(
                onDismissRequest = { showConfirm = false },
                title = { Text(stringResource(R.string.task_manager_kill_title)) },
                text = {
                    Text(
                            stringResource(
                                    R.string.task_manager_kill_message,
                                    process.name,
                                    process.id
                            )
                    )
                },
                confirmButton = {
                    Button(
                            onClick = {
                                view.performHapticFeedback(HapticFeedbackConstants.REJECT)
                                onKill()
                                showConfirm = false
                            },
                            colors =
                                    ButtonDefaults.buttonColors(
                                            containerColor = MaterialTheme.colorScheme.error
                                    )
                    ) { Text(stringResource(R.string.button_kill)) }
                },
                dismissButton = {
                    TextButton(onClick = { showConfirm = false }) {
                        Text(stringResource(R.string.button_cancel))
                    }
                }
        )
    }

    Card(
            modifier = modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp),
            shape = cardShape(shapePreset, cornerRadius),
            colors =
                    CardDefaults.cardColors(
                            containerColor = MaterialTheme.colorScheme.surfaceContainer
                    ),
            onClick = {
                view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                showConfirm = true
            }
    ) {
        Row(
                modifier = Modifier.fillMaxWidth().padding(adaptivePadding),
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

            Column(
                    modifier = Modifier.width(100.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                UsageIndicator(
                        label = "CPU",
                        value = "${process.cpu.toInt()}%",
                        progress = (process.cpu / maxCpu).toFloat().coerceIn(0f, 1f),
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
                modifier = Modifier.fillMaxWidth().height(6.dp).clip(CircleShape),
                color = color,
                trackColor = color.copy(alpha = 0.1f),
                strokeCap = StrokeCap.Round
        )
    }
}
