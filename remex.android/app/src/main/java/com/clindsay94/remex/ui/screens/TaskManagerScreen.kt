package com.clindsay94.remex.ui.screens

import android.view.HapticFeedbackConstants
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.material3.pulltorefresh.PullToRefreshDefaults
import androidx.compose.material3.pulltorefresh.rememberPullToRefreshState
import androidx.compose.runtime.*
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme
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
        viewModel: TaskManagerViewModel = viewModel(),
) {
    val processes by viewModel.processes.collectAsStateWithLifecycle()
    val isConnected by RemexClientManager.isConnected.collectAsStateWithLifecycle()
    val isRefreshing by viewModel.isRefreshing.collectAsStateWithLifecycle()
    val searchQuery by viewModel.searchQuery.collectAsStateWithLifecycle()
    val sortField by viewModel.sortField.collectAsStateWithLifecycle()
    val sortDescending by viewModel.sortDescending.collectAsStateWithLifecycle()
    val shapePreset by viewModel.taskManagerCardShapePreset.collectAsStateWithLifecycle()
    val cornerRadius by viewModel.cardCornerRadius.collectAsStateWithLifecycle()
    val killError by viewModel.killError.collectAsStateWithLifecycle()
    val loadError by viewModel.loadError.collectAsStateWithLifecycle()

    LaunchedEffect(viewModel, isVisible, isConnected) {
        viewModel.setAutoRefreshEnabled(isVisible && isConnected)
    }

    DisposableEffect(viewModel) {
        onDispose { viewModel.setAutoRefreshEnabled(enabled = false) }
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
            loadError = loadError,
            onRefreshProcesses = { viewModel.refreshProcesses() },
            onUpdateSearchQuery = { viewModel.updateSearchQuery(it) },
            onUpdateSortField = { viewModel.updateSortField(it) },
            onKillProcess = { viewModel.killProcess(it) },
            onClearKillError = { viewModel.clearKillError() },
            onClearLoadError = { viewModel.clearLoadError() },
            onNavigateToConnection = onNavigateToConnection
    )
}

private enum class TaskManagerListState { DISCONNECTED, LOADING, LIST }

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
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
        onRefreshProcesses: () -> Unit,
        onUpdateSearchQuery: (String) -> Unit,
        onUpdateSortField: (ProcessSortField) -> Unit,
        // Takes the whole ProcessInfo, not just the id: the host verifies the name before
        // ending anything, so the PID alone is no longer enough to act on (RemEx-druh).
        onKillProcess: (ProcessInfo) -> Unit,
        onNavigateToConnection: () -> Unit,
        modifier: Modifier = Modifier,
        killError: String? = null,
        loadError: String? = null,
        onClearKillError: () -> Unit = {},
        onClearLoadError: () -> Unit = {},
) {
    val view = LocalView.current
    val motionScheme = MaterialTheme.motionScheme

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
        val pullState = rememberPullToRefreshState()
        PullToRefreshBox(
                isRefreshing = isRefreshing,
                onRefresh = {
                    view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                    onRefreshProcesses()
                },
                modifier = Modifier.fillMaxSize().padding(innerPadding),
                state = pullState,
                indicator = {
                    PullToRefreshDefaults.LoadingIndicator(
                            state = pullState,
                            isRefreshing = isRefreshing,
                            modifier = Modifier.align(Alignment.TopCenter)
                    )
                }
        ) {
            Column(modifier = Modifier.fillMaxSize()) {
                NotConnectedBanner(
                        isConnected = isConnected,
                        onNavigateToConnection = onNavigateToConnection
                )

                LoadErrorBanner(
                        message = loadError,
                        onRetry = {
                            onClearLoadError()
                            onRefreshProcesses()
                        },
                        onDismiss = onClearLoadError
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

                val listState =
                        when {
                            !isConnected && processes.isEmpty() -> TaskManagerListState.DISCONNECTED
                            processes.isEmpty() && isRefreshing -> TaskManagerListState.LOADING
                            else -> TaskManagerListState.LIST
                        }
                AnimatedContent(
                        targetState = listState,
                        transitionSpec = {
                            val effectsSpec = motionScheme.defaultEffectsSpec<Float>()
                            fadeIn(effectsSpec) togetherWith fadeOut(effectsSpec)
                        },
                        modifier = Modifier.weight(1f),
                        label = "taskManagerListState"
                ) { state ->
                    when (state) {
                        TaskManagerListState.DISCONNECTED -> {
                            DisconnectedFullScreen(
                                    screenName = stringResource(R.string.screen_task_manager_title),
                                    onNavigateToConnection = onNavigateToConnection,
                                    modifier = Modifier.fillMaxSize()
                            )
                        }
                        TaskManagerListState.LOADING -> {
                            Box(
                                    modifier = Modifier.fillMaxSize(),
                                    contentAlignment = Alignment.Center
                            ) {
                                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                                    RemexLoadingIndicator()
                                    Spacer(modifier = Modifier.height(16.dp))
                                    Text(
                                            stringResource(R.string.task_manager_fetching),
                                            style = MaterialTheme.typography.bodyMedium,
                                            color = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                }
                            }
                        }
                        TaskManagerListState.LIST -> {
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
                                            onKill = { onKillProcess(process) },
                                            modifier =
                                                    Modifier.animateItem(
                                                            placementSpec =
                                                                    MaterialTheme.motionScheme
                                                                            .fastSpatialSpec()
                                                    )
                                    )
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

@Preview(showBackground = true)
@Composable
private fun TaskManagerScreenPreview() {
    RemExTheme {
        TaskManagerScreenContent(
            processes = listOf(
                ProcessInfo(1, "system", 10.5, 256.0),
                ProcessInfo(1234, "remex-host.exe", 2.1, 128.0),
                ProcessInfo(5678, "chrome.exe", 15.0, 1024.0)
            ),
            searchQuery = "",
            sortField = ProcessSortField.CPU,
            sortDescending = true,
            shapePreset = 1f,
            cornerRadius = 12,
            isConnected = true,
            isRefreshing = false,
            onRefreshProcesses = {},
            onUpdateSearchQuery = {},
            onUpdateSortField = {},
            onKillProcess = {},
            onNavigateToConnection = {}
        )
    }
}

@Preview(showBackground = true)
@Composable
private fun ProcessCardPreview() {
    RemExTheme {
        Box(modifier = Modifier.padding(16.dp)) {
            ProcessCard(
                process = ProcessInfo(1234, "remex-host.exe", 5.0, 512.0),
                maxRam = 1024.0,
                maxCpu = 100.0,
                shapePreset = 0f,
                cornerRadius = 12,
                onKill = {}
            )
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
                    AnimatedVisibility(
                            visible = query.isNotEmpty(),
                            enter = scaleIn(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
                            exit = scaleOut(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeOut(MaterialTheme.motionScheme.fastEffectsSpec())
                    ) {
                        IconButton(onClick = { onUpdateQuery("") }) {
                            Icon(
                                    Icons.Default.Close,
                                    contentDescription =
                                            stringResource(R.string.cd_clear_search)
                            )
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
@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
private fun FilterSortSection(
        currentSortField: ProcessSortField,
        sortDescending: Boolean,
        onUpdateSortField: (ProcessSortField) -> Unit
) {
    // Labels resolved here (composable scope) because ButtonGroup's content lambda is non-composable.
    val fields =
            listOf(
                    ProcessSortField.CPU to stringResource(R.string.sort_cpu),
                    ProcessSortField.RAM to stringResource(R.string.sort_ram),
                    ProcessSortField.NAME to stringResource(R.string.sort_name),
                    ProcessSortField.PID to stringResource(R.string.sort_pid)
            )
    val sortByText = stringResource(R.string.sort_by)

    // The row is MEASURED rather than switched on a font-scale threshold. A threshold would have to
    // be a single number covering every locale at every scale, and it would be wrong at one end or
    // the other; measuring answers the only question that matters — do these four labels, at this
    // font size, in this language, actually fit beside the label and the direction button?
    val measurer = rememberTextMeasurer()
    val density = LocalDensity.current
    val chipStyle = MaterialTheme.typography.labelLarge
    val labelStyle = MaterialTheme.typography.labelMedium
    val widestChipLabelPx =
            remember(fields, chipStyle, density, measurer) {
                // measurer is keyed on too: rememberTextMeasurer is itself remembered on
                // (fontFamilyResolver, density, layoutDirection), so a resolver or direction change
                // that left density equal would otherwise leave this memo holding a width computed
                // by a measurer that has since been discarded.
                fields.maxOf { (_, label) -> measurer.measure(label, chipStyle).size.width }
            }
    val sortLabelPx =
            remember(sortByText, labelStyle, density, measurer) {
                minOf(
                        measurer.measure(sortByText, labelStyle).size.width,
                        with(density) { SortLabelMaxWidth.roundToPx() }
                )
            }

    BoxWithConstraints(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp)
    ) {
        val fitsInline =
                with(density) {
                    sortControlsFitInline(
                            availableWidthPx = constraints.maxWidth,
                            sortLabelWidthPx = sortLabelPx,
                            widestChipLabelPx = widestChipLabelPx,
                            chipCount = fields.size,
                            chipHorizontalPaddingPx = ChipHorizontalPadding.roundToPx(),
                            chipSpacingPx = ChipSpacing.roundToPx(),
                            rowSpacingPx = SortRowSpacing.roundToPx(),
                            directionButtonWidthPx = DirectionButtonWidth.roundToPx()
                    )
                }

        if (fitsInline) {
            Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(SortRowSpacing)
            ) {
                // CAPPED, not weighted. A Row measures non-weighted children first at their full
                // intrinsic width, so an unbounded label took whatever it wanted and the chips
                // absorbed the entire shortfall — clipping mid-glyph, since their default overflow
                // is Clip. Invisible while this string was hardcoded English at ~48dp; localizing it
                // made the label up to 2.5x wider (RemEx-0pxq).
                //
                // widthIn rather than weight(fill = false): giving BOTH children a weight makes
                // totalWeight 2, and RowColumnMeasurePolicy computes each share up front with no
                // redistribution pass — so a fill = false label shrinks to intrinsic while the chip
                // row, whose weight defaults to fill = true, stays PINNED at its 50% share. The
                // label's unused half is not handed back, it is simply lost, which cost English
                // chips ~84dp and left dead space at the row's end. With only one weighted child the
                // chip row gets the whole true remainder, so English is unchanged and only the long
                // locales are capped.
                SortByLabel(sortByText, Modifier.widthIn(max = SortLabelMaxWidth))
                SortFieldChips(fields, currentSortField, onUpdateSortField, Modifier.weight(1f))
                SortDirectionButton(sortDescending, currentSortField, onUpdateSortField)
            }
        } else {
            // Stacked: the chips get the FULL width instead of the ~55% left over beside the label
            // and the button, which is the only thing that actually buys them room. The label keeps
            // the direction button company on the first line — it is the one control that must stay
            // reachable, and it costs nothing there.
            Column(
                    modifier = Modifier.fillMaxWidth(),
                    verticalArrangement = Arrangement.spacedBy(StackedRowSpacing)
            ) {
                Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically
                ) {
                    // weight, not widthIn: with a whole line to itself the label no longer has to be
                    // capped, so the long translations stop being truncated at exactly the font
                    // scale where they are hardest to read.
                    SortByLabel(sortByText, Modifier.weight(1f))
                    SortDirectionButton(sortDescending, currentSortField, onUpdateSortField)
                }
                SortFieldChips(
                        fields,
                        currentSortField,
                        onUpdateSortField,
                        Modifier.fillMaxWidth()
                )
            }
        }
    }
}

private val SortLabelMaxWidth = 120.dp
private val ChipHorizontalPadding = 6.dp
private val ChipSpacing = 4.dp
private val SortRowSpacing = 8.dp
/** Gap between the label line and the chip line when stacked. */
private val StackedRowSpacing = 4.dp
/** M3 minimum interactive size — what an [IconButton] reserves regardless of its 20dp icon. */
private val DirectionButtonWidth = 48.dp

/**
 * Do the sort label, the four sort chips and the direction button fit on one line?
 *
 * Pure arithmetic, so the decision this layout turns on can be tested on the JVM rather than only
 * seen on a device at a font scale nobody remembers to try (RemEx-k0vy). Every input is already in
 * pixels; the caller converts.
 *
 * Sizing every chip to the WIDEST label is deliberate — the chips share the row equally
 * (`weight(1f)` each), so the widest one decides whether any of them clips. Averaging would report
 * a fit that the actual measure policy does not deliver.
 *
 * The model is EXACT, not an estimate, and carries no slack — verified against material3
 * 1.5.0-alpha24. `ToggleButton` provides `typography.labelLarge` to its content (ToggleButton.kt),
 * which is the style measured here, and it constrains `minHeight` only, so there is no hidden
 * min-width term. `IconButton` applies `minimumInteractiveComponentSize()` outermost over a 40dp
 * container, and `LocalMinimumInteractiveComponentSize` defaults to 48dp, so the direction button
 * really does occupy 48dp. The comparison therefore trips at the exact pixel where the first glyph
 * would clip. Do not "reclaim" margin here — there is none to reclaim.
 *
 * Stacking does NOT guarantee a fit. A full-width chip row on a 280dp screen at 2.0x still needs
 * more than it has, and those chips ellipsize. That is the floor of this approach; escaping it needs
 * the dropdown option the bead also lists, not a bigger number here.
 */
internal fun sortControlsFitInline(
        availableWidthPx: Int,
        sortLabelWidthPx: Int,
        widestChipLabelPx: Int,
        chipCount: Int,
        chipHorizontalPaddingPx: Int,
        chipSpacingPx: Int,
        rowSpacingPx: Int,
        directionButtonWidthPx: Int
): Boolean {
    if (chipCount <= 0) return true
    val chipsNeedPx =
            chipCount * (widestChipLabelPx + 2 * chipHorizontalPaddingPx) +
                    (chipCount - 1) * chipSpacingPx
    val remainingPx =
            availableWidthPx - sortLabelWidthPx - directionButtonWidthPx - 2 * rowSpacingPx
    return chipsNeedPx <= remainingPx
}

@Composable
private fun SortByLabel(text: String, modifier: Modifier = Modifier) {
    Text(
            text = text,
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = modifier,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
    )
}

@Composable
@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
private fun SortFieldChips(
        fields: List<Pair<ProcessSortField, String>>,
        currentSortField: ProcessSortField,
        onUpdateSortField: (ProcessSortField) -> Unit,
        modifier: Modifier = Modifier
) {
    val view = LocalView.current
    // M3 Expressive ToggleButtons in a plain Row. NOTE: we deliberately do NOT use the Expressive
    // ButtonGroup — its measure policy crashes in material3 1.5.0-alpha20 (ButtonGroupMeasurePolicy
    // → Constraints.copy with negative width, ButtonGroup.kt:816). A standalone ToggleButton still
    // gives the round↔squircle shape morph on selection.
    Row(modifier = modifier, horizontalArrangement = Arrangement.spacedBy(ChipSpacing)) {
        fields.forEach { (field, label) ->
            ToggleButton(
                    checked = currentSortField == field,
                    onCheckedChange = {
                        view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                        onUpdateSortField(field)
                    },
                    modifier = Modifier.weight(1f),
                    contentPadding =
                            androidx.compose.foundation.layout.PaddingValues(
                                    horizontal = ChipHorizontalPadding
                            )
            ) {
                Text(label, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
    }
}

/** Sort direction toggle. Stays a separate control from the chips for a clear affordance. */
@Composable
private fun SortDirectionButton(
        sortDescending: Boolean,
        currentSortField: ProcessSortField,
        onUpdateSortField: (ProcessSortField) -> Unit
) {
    val view = LocalView.current
    IconButton(
            onClick = {
                view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                onUpdateSortField(currentSortField) // same field → toggles descending
            }
    ) {
        val sortArrowRotation by
                animateFloatAsState(
                        targetValue = if (sortDescending) 0f else 180f,
                        animationSpec = MaterialTheme.motionScheme.fastSpatialSpec(),
                        label = "sortArrowRotation"
                )
        Icon(
                imageVector = Icons.Default.ArrowDownward,
                contentDescription =
                        if (sortDescending) stringResource(R.string.sort_descending)
                        else stringResource(R.string.sort_ascending),
                modifier = Modifier.size(20.dp).rotate(sortArrowRotation)
        )
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
                        style = MaterialTheme.typography.titleMediumEmphasized,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                )
                Text(
                        text = stringResource(R.string.task_manager_pid_label, process.id),
                        style = MaterialTheme.typography.labelSmallEmphasized,
                        color = MaterialTheme.colorScheme.primary.copy(alpha = 0.8f)
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
    val animatedProgress by
            animateFloatAsState(
                    targetValue = progress,
                    animationSpec = MaterialTheme.motionScheme.fastSpatialSpec(),
                    label = "usageIndicatorProgress"
            )
    Column {
        Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.Bottom
        ) {
            Text(
                    text = label,
                    style = MaterialTheme.typography.labelSmallEmphasized,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Text(
                    text = value,
                    style = MaterialTheme.typography.labelSmallEmphasized,
                    color = color
            )
        }
        Spacer(modifier = Modifier.height(2.dp))
        LinearProgressIndicator(
                progress = { animatedProgress },
                modifier = Modifier.fillMaxWidth().height(6.dp).clip(CircleShape),
                color = color,
                trackColor = color.copy(alpha = 0.1f),
                strokeCap = StrokeCap.Round
        )
    }
}

/**
 * Persistent error banner shown when the host has stopped responding to process_list_request
 * after several consecutive timeouts. Distinct from a transient snackbar — this stays put until
 * the user retries or dismisses it, and replaces the previous "spinner refreshes forever" UI.
 */
@Composable
private fun LoadErrorBanner(
        message: String?,
        onRetry: () -> Unit,
        onDismiss: () -> Unit,
) {
    // Remembers the last non-blank message so the shrink-out exit animation still has text to
    // show after the caller clears `message` to null/blank.
    var lastMessage by remember { mutableStateOf(message ?: "") }
    LaunchedEffect(message) {
        if (!message.isNullOrBlank()) lastMessage = message
    }
    AnimatedVisibility(
            visible = !message.isNullOrBlank(),
            enter = expandVertically(MaterialTheme.motionScheme.fastSpatialSpec()),
            exit = shrinkVertically(MaterialTheme.motionScheme.fastSpatialSpec())
    ) {
        Card(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
                colors =
                        CardDefaults.cardColors(
                                containerColor = MaterialTheme.colorScheme.errorContainer
                        )
        ) {
            Row(
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                Icon(
                        Icons.Default.Warning,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.onErrorContainer,
                        modifier = Modifier.size(18.dp)
                )
                Text(
                        text = lastMessage,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onErrorContainer,
                        modifier = Modifier.weight(1f)
                )
                TextButton(onClick = onRetry) { Text(stringResource(R.string.task_manager_retry)) }
                TextButton(onClick = onDismiss) { Text(stringResource(R.string.task_manager_dismiss)) }
            }
        }
    }
}
