@file:OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)

package com.clindsay94.remex.ui.screens

import android.content.ClipData
import android.content.ClipDescription
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.PersistableBundle
import androidx.annotation.StringRes
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.horizontalScroll
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
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Notes
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.PlayCircle
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Schedule
import androidx.compose.material.icons.automirrored.filled.ScreenShare
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Security
import androidx.compose.material.icons.filled.Share
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextField
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.LifecycleStartEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.ui.components.RemexFilterChip
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.RemexHapticEvent
import com.clindsay94.remex.ui.components.RemexHaptics
import com.clindsay94.remex.ui.components.RemexSegmentedSwitch
import com.clindsay94.remex.ui.components.RemexTooltip
import com.clindsay94.remex.ui.components.rememberRemexCollapsingScrollBehavior
import com.clindsay94.remex.ui.components.rememberRemexHaptics
import com.clindsay94.remex.ui.components.AdminPanelSettingsGlyph
import com.clindsay94.remex.ui.components.KeyboardDoubleArrowDownGlyph
import com.clindsay94.remex.ui.components.LanGlyph
import com.clindsay94.remex.ui.components.MemoryGlyph
import com.clindsay94.remex.ui.components.VerifiedUserGlyph
import com.clindsay94.remex.ui.components.WarningAmberGlyph
import com.clindsay94.remex.ui.theme.rememberRemexIconButtonShapes
import kotlinx.coroutines.launch
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/** The two tabs of the PC logs and diagnostics page. Order is the segmented button order. */
enum class PcDiagnosticsTab(@get:StringRes val labelRes: Int) {
    Logs(R.string.pc_diag_tab_logs),
    Diagnostics(R.string.pc_diag_tab_diagnostics),
}

/** The level chips, lowest first. Each one is the lowest level the PC sends. */
private val levelChips =
        listOf(
                PcLogLevel.Debug to R.string.pc_diag_chip_debug,
                PcLogLevel.Information to R.string.pc_diag_chip_info,
                PcLogLevel.Warning to R.string.pc_diag_chip_warnings,
                PcLogLevel.Error to R.string.pc_diag_chip_errors,
        )

/**
 * More > PC logs & diagnostics (RemEx-pp4cm.13): a read-only look at what the PC has logged and how
 * its own checks came out. Polls only while this page is on screen, the Logs tab is selected and Live
 * is on; see [PcLogsController].
 */
@Composable
fun PcDiagnosticsScreen(
        onNavigateToConnection: () -> Unit = {},
        viewModel: PcDiagnosticsViewModel = viewModel(),
) {
    val controller = viewModel.controller
    val logs by controller.logs.collectAsStateWithLifecycle()
    val summary by controller.summary.collectAsStateWithLifecycle()
    val live by controller.live.collectAsStateWithLifecycle()
    val ready by controller.ready.collectAsStateWithLifecycle()
    val isConnected by RemexClientManager.isConnected.collectAsStateWithLifecycle()
    val needsPairing by RemexClientManager.needsPairing.collectAsStateWithLifecycle()

    var tab by remember { mutableStateOf(PcDiagnosticsTab.Logs) }
    var query by remember { mutableStateOf("") }
    val haptics = rememberRemexHaptics()
    val snackbarHostState = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val copiedText = stringResource(R.string.pc_diag_copied)
    val copyFailedText = stringResource(R.string.pc_diag_copy_failed)

    // Visible means the Logs tab is on screen AND the app is in the foreground. A LifecycleStartEffect
    // is an observer, so onStopOrDispose fires on ON_STOP even though Compose's frame clock is paused
    // then, which is exactly the case a keyed LaunchedEffect misses (see TaskManagerScreen).
    LifecycleStartEffect(controller, tab) {
        controller.setVisible(tab == PcDiagnosticsTab.Logs)
        onStopOrDispose { controller.setVisible(false) }
    }

    // First load for the tab the person is on, when the Live poll is not going to do it.
    LaunchedEffect(tab, ready) {
        if (!ready) return@LaunchedEffect
        when (tab) {
            PcDiagnosticsTab.Logs -> if (!logs.loadedOnce && !live) controller.refreshLogs()
            PcDiagnosticsTab.Diagnostics -> if (!summary.loadedOnce) controller.refreshSummary()
        }
    }

    val tabFadeIn = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
    val tabFadeOut = MaterialTheme.motionScheme.fastEffectsSpec<Float>()
    val visibleLines = remember(logs.lines, query) { PcDiagnostics.filter(logs.lines, query) }
    val scrollBehavior = rememberRemexCollapsingScrollBehavior()

    Scaffold(
            modifier = Modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
            snackbarHost = { SnackbarHost(snackbarHostState) },
            topBar = {
                Column {
                    RemexFlexibleTopBar(
                            title = stringResource(R.string.screen_pc_diagnostics_title),
                            subtitle = stringResource(R.string.pc_diag_subtitle),
                            scrollBehavior = scrollBehavior,
                            actions = {
                                RemexTooltip(stringResource(R.string.cd_refresh)) {
                                    IconButton(
                                            enabled = ready,
                                            onClick = {
                                                haptics.perform(RemexHapticEvent.Refresh)
                                                when (tab) {
                                                    PcDiagnosticsTab.Logs -> controller.refreshLogs()
                                                    PcDiagnosticsTab.Diagnostics -> controller.refreshSummary()
                                                }
                                            },
                                            shapes = rememberRemexIconButtonShapes()
                                    ) {
                                        Icon(
                                                Icons.Default.Refresh,
                                                contentDescription = stringResource(R.string.cd_refresh)
                                        )
                                    }
                                }
                            }
                    )
                    RemexSegmentedSwitch(
                            options = PcDiagnosticsTab.entries,
                            selected = tab,
                            labelRes = { it.labelRes },
                            onSelect = { tab = it },
                    )
                }
            }
    ) { innerPadding ->
        Box(Modifier.fillMaxSize().padding(innerPadding)) {
            when {
                !isConnected ->
                        DisconnectedFullScreen(
                                screenName = stringResource(R.string.screen_pc_diagnostics_title),
                                onNavigateToConnection = onNavigateToConnection,
                        )
                needsPairing ->
                        NeedsPairingContent(
                                onPair = {
                                    ConnectionOpenRequests.requestAddPc()
                                    onNavigateToConnection()
                                }
                        )
                else ->
                        AnimatedContent(
                                targetState = tab,
                                transitionSpec = {
                                    fadeIn(tabFadeIn) togetherWith fadeOut(tabFadeOut)
                                },
                                label = "pcDiagnosticsTab"
                        ) { shown ->
                            when (shown) {
                                PcDiagnosticsTab.Logs ->
                                        LogsTab(
                                                state = logs,
                                                lines = visibleLines,
                                                query = query,
                                                live = live,
                                                onQueryChange = { query = it },
                                                onLevel = {
                                                    haptics.perform(RemexHapticEvent.Select)
                                                    controller.setMinLevel(it)
                                                },
                                                onLive = {
                                                    haptics.perform(RemexHaptics.toggle(it))
                                                    controller.setLive(it)
                                                },
                                                onRetry = { controller.refreshLogs() },
                                                onCopy = {
                                                    val ok = copyLines(context, visibleLines)
                                                    haptics.perform(
                                                            if (ok) RemexHapticEvent.Confirm
                                                            else RemexHapticEvent.Reject
                                                    )
                                                    scope.launch {
                                                        snackbarHostState.showSnackbar(
                                                                if (ok) copiedText else copyFailedText
                                                        )
                                                    }
                                                },
                                                onShare = {
                                                    haptics.perform(RemexHapticEvent.Press)
                                                    shareLines(context, visibleLines)
                                                },
                                        )
                                PcDiagnosticsTab.Diagnostics ->
                                        DiagnosticsTab(
                                                state = summary,
                                                onRefresh = {
                                                    haptics.perform(RemexHapticEvent.Refresh)
                                                    controller.refreshSummary()
                                                },
                                        )
                            }
                        }
            }
        }
    }
}

// ── Logs ───────────────────────────────────────────────────────────────────────────────────────

@Composable
private fun LogsTab(
        state: PcLogsState,
        lines: List<PcLogLine>,
        query: String,
        live: Boolean,
        onQueryChange: (String) -> Unit,
        onLevel: (PcLogLevel) -> Unit,
        onLive: (Boolean) -> Unit,
        onRetry: () -> Unit,
        onCopy: () -> Unit,
        onShare: () -> Unit,
) {
    val listState = rememberLazyListState()
    val scope = rememberCoroutineScope()

    // Follow the newest line until the person scrolls away from the end; Jump to latest turns it back on.
    // Only a scroll in progress changes it, so the list growing under a still finger does not.
    var follow by remember { mutableStateOf(true) }
    LaunchedEffect(listState) {
        snapshotFlow { listState.isScrollInProgress to !listState.canScrollForward }.collect {
                (scrolling, atEnd) ->
            if (scrolling) follow = atEnd
        }
    }
    val newest = lines.lastOrNull()?.seq
    LaunchedEffect(newest, follow) {
        if (follow && lines.isNotEmpty()) listState.scrollToItem(lines.lastIndex)
    }

    Column(Modifier.fillMaxSize()) {
        LogSearchField(query, onQueryChange)
        Row(
                Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 16.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically
        ) {
            levelChips.forEach { (level, labelRes) ->
                RemexFilterChip(
                        selected = state.minLevel == level,
                        onClick = { onLevel(level) },
                        label = { Text(stringResource(labelRes)) },
                )
            }
        }
        Row(
                Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp, top = 4.dp),
                verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                    stringResource(R.string.pc_diag_live),
                    style = MaterialTheme.typography.labelLarge,
                    modifier = Modifier.padding(end = 8.dp)
            )
            Switch(checked = live, onCheckedChange = onLive)
            Spacer(Modifier.weight(1f))
            RemexTooltip(stringResource(R.string.pc_diag_copy)) {
                IconButton(
                        enabled = lines.isNotEmpty(),
                        onClick = onCopy,
                        shapes = rememberRemexIconButtonShapes()
                ) {
                    Icon(Icons.Default.ContentCopy, contentDescription = stringResource(R.string.pc_diag_copy))
                }
            }
            RemexTooltip(stringResource(R.string.pc_diag_share)) {
                IconButton(
                        enabled = lines.isNotEmpty(),
                        onClick = onShare,
                        shapes = rememberRemexIconButtonShapes()
                ) {
                    Icon(Icons.Default.Share, contentDescription = stringResource(R.string.pc_diag_share))
                }
            }
        }

        Box(Modifier.fillMaxSize()) {
            when {
                state.error != null && state.lines.isEmpty() ->
                        MessagePane(
                                icon = Icons.Default.ErrorOutline,
                                title = stringResource(errorTitleRes(state.error)),
                                body = stringResource(errorBodyRes(state.error)),
                                actionLabel = stringResource(R.string.pc_diag_retry),
                                onAction = onRetry,
                        )
                !state.loadedOnce ->
                        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                                RemexLoadingIndicator()
                                Spacer(Modifier.height(12.dp))
                                Text(
                                        stringResource(R.string.pc_diag_loading_logs),
                                        style = MaterialTheme.typography.bodyMedium,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                        }
                lines.isEmpty() ->
                        if (query.isNotBlank()) {
                            MessagePane(
                                    icon = Icons.Default.Search,
                                    title = stringResource(R.string.pc_diag_no_match_title),
                                    body = stringResource(R.string.pc_diag_no_match_body, query.trim()),
                            )
                        } else {
                            MessagePane(
                                    icon = Icons.AutoMirrored.Filled.Notes,
                                    title = stringResource(R.string.pc_diag_empty_title),
                                    body = stringResource(R.string.pc_diag_empty_body),
                            )
                        }
                else ->
                        Column(Modifier.fillMaxSize()) {
                            if (state.error != null) {
                                InlineError(
                                        text = stringResource(errorBodyRes(state.error)),
                                        actionLabel = stringResource(R.string.pc_diag_retry),
                                        onAction = onRetry,
                                )
                            }
                            if (state.truncated) {
                                Text(
                                        stringResource(R.string.pc_diag_newest_only),
                                        style = MaterialTheme.typography.labelSmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                        modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp)
                                )
                            }
                            LazyColumn(
                                    state = listState,
                                    modifier = Modifier.fillMaxSize(),
                                    contentPadding =
                                            androidx.compose.foundation.layout.PaddingValues(
                                                    horizontal = 12.dp,
                                                    vertical = 8.dp
                                            ),
                                    verticalArrangement = Arrangement.spacedBy(6.dp)
                            ) {
                                items(lines, key = { it.seq }) { line -> LogRow(line) }
                            }
                        }
            }

            JumpToLatestButton(
                    visible = !follow && lines.isNotEmpty(),
                    onClick = {
                        follow = true
                        scope.launch {
                            if (lines.isNotEmpty()) listState.animateScrollToItem(lines.lastIndex)
                        }
                    },
                    modifier = Modifier.align(Alignment.BottomCenter).padding(bottom = 16.dp)
            )
        }
    }
}

/** "Jump to latest": appears once the person has scrolled away from the newest line. */
@Composable
private fun JumpToLatestButton(visible: Boolean, onClick: () -> Unit, modifier: Modifier = Modifier) {
    val haptics = rememberRemexHaptics()
    AnimatedVisibility(
            visible = visible,
            enter = scaleIn(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
            exit = scaleOut(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
            modifier = modifier
    ) {
        ExtendedFloatingActionButton(
                onClick = {
                    haptics.perform(RemexHapticEvent.Press)
                    onClick()
                },
                icon = { Icon(KeyboardDoubleArrowDownGlyph, contentDescription = null) },
                text = { Text(stringResource(R.string.pc_diag_jump_latest)) },
        )
    }
}

@Composable
private fun LogSearchField(query: String, onQueryChange: (String) -> Unit) {
    Surface(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
            color = MaterialTheme.colorScheme.surfaceContainerLow,
            shape = MaterialTheme.shapes.extraLarge
    ) {
        TextField(
                value = query,
                onValueChange = onQueryChange,
                placeholder = { Text(stringResource(R.string.pc_diag_search_hint)) },
                leadingIcon = { Icon(Icons.Default.Search, contentDescription = null) },
                trailingIcon = {
                    AnimatedVisibility(
                            visible = query.isNotEmpty(),
                            enter = scaleIn(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeIn(MaterialTheme.motionScheme.fastEffectsSpec()),
                            exit = scaleOut(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeOut(MaterialTheme.motionScheme.fastEffectsSpec())
                    ) {
                        IconButton(onClick = { onQueryChange("") }, shapes = rememberRemexIconButtonShapes()) {
                            Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cd_clear_search))
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

private val clockFormat: DateTimeFormatter =
        DateTimeFormatter.ofPattern("HH:mm:ss").withZone(ZoneId.systemDefault())

/** One log line: a coloured header (time, level tag, category) over the message, all monospace. */
@Composable
private fun LogRow(line: PcLogLine) {
    val color =
            when (line.level) {
                PcLogLevel.Trace, PcLogLevel.Debug -> MaterialTheme.colorScheme.onSurfaceVariant
                PcLogLevel.Information -> MaterialTheme.colorScheme.onSurface
                PcLogLevel.Warning -> MaterialTheme.colorScheme.tertiary
                PcLogLevel.Error, PcLogLevel.Critical -> MaterialTheme.colorScheme.error
            }
    val time = remember(line.timeUtcMillis) { clockFormat.format(Instant.ofEpochMilli(line.timeUtcMillis)) }
    Column(Modifier.fillMaxWidth()) {
        Text(
                text = "$time  ${line.level.tag}  ${line.category}",
                style = MaterialTheme.typography.labelSmall.copy(fontFamily = FontFamily.Monospace),
                color = color,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
        )
        Text(
                text = line.message,
                style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                color = color
        )
    }
}

// ── Diagnostics ────────────────────────────────────────────────────────────────────────────────

@Composable
private fun DiagnosticsTab(state: PcSummaryState, onRefresh: () -> Unit) {
    when {
        state.error != null && state.rows.isEmpty() ->
                MessagePane(
                        icon = Icons.Default.ErrorOutline,
                        title = stringResource(errorTitleRes(state.error)),
                        body = stringResource(errorBodyRes(state.error)),
                        actionLabel = stringResource(R.string.pc_diag_retry),
                        onAction = onRefresh,
                )
        !state.loadedOnce ->
                Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        RemexLoadingIndicator()
                        Spacer(Modifier.height(12.dp))
                        Text(
                                stringResource(R.string.pc_diag_loading_summary),
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
        state.rows.isEmpty() ->
                MessagePane(
                        icon = Icons.Default.Info,
                        title = stringResource(R.string.pc_diag_summary_empty_title),
                        body = stringResource(R.string.pc_diag_summary_empty_body),
                        actionLabel = stringResource(R.string.cd_refresh),
                        onAction = onRefresh,
                )
        else ->
                LazyColumn(
                        Modifier.fillMaxSize(),
                        contentPadding = androidx.compose.foundation.layout.PaddingValues(vertical = 8.dp)
                ) {
                    item {
                        Text(
                                stringResource(R.string.pc_diag_summary_intro),
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp)
                        )
                    }
                    if (state.error != null) {
                        item {
                            InlineError(
                                    text = stringResource(errorBodyRes(state.error)),
                                    actionLabel = stringResource(R.string.pc_diag_retry),
                                    onAction = onRefresh,
                            )
                        }
                    }
                    items(state.rows, key = { it.key }) { row -> SummaryRow(row) }
                    item {
                        FilledTonalButton(
                                onClick = onRefresh,
                                modifier = Modifier.padding(horizontal = 16.dp, vertical = 12.dp)
                        ) {
                            Icon(Icons.Default.Refresh, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.size(8.dp))
                            Text(stringResource(R.string.cd_refresh))
                        }
                    }
                }
    }
}

private data class RowStyle(@get:StringRes val labelRes: Int?, val icon: ImageVector)

private fun styleFor(key: String): RowStyle =
        when (key) {
            "listener" -> RowStyle(R.string.pc_diag_row_listener, LanGlyph)
            "certificate" -> RowStyle(R.string.pc_diag_row_certificate, VerifiedUserGlyph)
            "firewall" -> RowStyle(R.string.pc_diag_row_firewall, Icons.Default.Security)
            "elevation" -> RowStyle(R.string.pc_diag_row_elevation, AdminPanelSettingsGlyph)
            "autostart" -> RowStyle(R.string.pc_diag_row_autostart, Icons.Default.PlayCircle)
            "capture" -> RowStyle(R.string.pc_diag_row_capture, Icons.AutoMirrored.Filled.ScreenShare)
            "encoder" -> RowStyle(R.string.pc_diag_row_encoder, MemoryGlyph)
            "version" -> RowStyle(R.string.pc_diag_row_version, Icons.Default.Info)
            "uptime" -> RowStyle(R.string.pc_diag_row_uptime, Icons.Default.Schedule)
            else -> RowStyle(null, Icons.Default.Info)
        }

@Composable
private fun SummaryRow(row: PcSummaryRow) {
    val style = styleFor(row.key)
    val (stateIcon, stateColor, stateRes) =
            when (row.state) {
                PcRowState.Ok -> Triple(Icons.Default.CheckCircle, MaterialTheme.colorScheme.primary, R.string.pc_diag_state_ok)
                PcRowState.Warn -> Triple(WarningAmberGlyph, MaterialTheme.colorScheme.tertiary, R.string.pc_diag_state_warn)
                PcRowState.Error -> Triple(Icons.Default.ErrorOutline, MaterialTheme.colorScheme.error, R.string.pc_diag_state_error)
            }
    Row(
            Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(16.dp)
    ) {
        Icon(style.icon, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
        Column(Modifier.weight(1f)) {
            Text(
                    style.labelRes?.let { stringResource(it) } ?: row.key,
                    style = MaterialTheme.typography.bodyLarge,
                    color = MaterialTheme.colorScheme.onSurface
            )
            if (row.detail.isNotBlank()) {
                Text(
                        row.detail,
                        style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
        // The word as well as the icon and colour, so state never rests on colour alone.
        Column(horizontalAlignment = Alignment.End) {
            Icon(stateIcon, contentDescription = null, tint = stateColor)
            Text(stringResource(stateRes), style = MaterialTheme.typography.labelSmall, color = stateColor)
        }
    }
}

// ── Shared pieces ──────────────────────────────────────────────────────────────────────────────

@Composable
private fun MessagePane(
        icon: ImageVector,
        title: String,
        body: String,
        actionLabel: String? = null,
        onAction: () -> Unit = {},
) {
    Column(
            Modifier.fillMaxSize().padding(32.dp),
            verticalArrangement = Arrangement.Center,
            horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Icon(
                icon,
                contentDescription = null,
                modifier = Modifier.size(56.dp),
                tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.6f)
        )
        Spacer(Modifier.height(16.dp))
        Text(
                title,
                style = MaterialTheme.typography.titleMediumEmphasized,
                textAlign = TextAlign.Center,
                color = MaterialTheme.colorScheme.onSurface
        )
        Spacer(Modifier.height(8.dp))
        Text(
                body,
                style = MaterialTheme.typography.bodyMedium,
                textAlign = TextAlign.Center,
                color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        if (actionLabel != null) {
            Spacer(Modifier.height(20.dp))
            Button(onClick = onAction) { Text(actionLabel) }
        }
    }
}

@Composable
private fun InlineError(text: String, actionLabel: String, onAction: () -> Unit) {
    Surface(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
            color = MaterialTheme.colorScheme.errorContainer,
            shape = MaterialTheme.shapes.medium
    ) {
        Row(Modifier.padding(start = 12.dp, end = 4.dp, top = 4.dp, bottom = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(
                    text,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onErrorContainer,
                    modifier = Modifier.weight(1f)
            )
            androidx.compose.material3.TextButton(onClick = onAction) { Text(actionLabel) }
        }
    }
}

@StringRes
private fun errorTitleRes(error: PcDiagError): Int =
        when (error) {
            PcDiagError.Refused -> R.string.pc_diag_error_refused_title
            PcDiagError.NoAnswer -> R.string.pc_diag_error_no_answer_title
            PcDiagError.Unavailable -> R.string.pc_diag_error_unavailable_title
        }

@StringRes
private fun errorBodyRes(error: PcDiagError): Int =
        when (error) {
            PcDiagError.Refused -> R.string.pc_diag_error_refused_body
            PcDiagError.NoAnswer -> R.string.pc_diag_error_no_answer_body
            PcDiagError.Unavailable -> R.string.pc_diag_error_unavailable_body
        }

// ── Copy and share ─────────────────────────────────────────────────────────────────────────────

/**
 * Puts the shown lines on the clipboard as text. The lines were redacted by the PC, but a log can
 * still hold something private, so the clip is marked sensitive and Android hides its preview.
 * Guarded because a clip is a binder transaction that can fail; false means nothing was copied.
 */
private fun copyLines(context: Context, lines: List<PcLogLine>): Boolean {
    if (lines.isEmpty()) return false
    val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager ?: return false
    return runCatching {
                clipboard.setPrimaryClip(
                        ClipData.newPlainText(
                                        context.getString(R.string.pc_diag_share_title),
                                        PcDiagnostics.formatForShare(lines)
                                )
                                .apply {
                                    description.extras =
                                            PersistableBundle().apply {
                                                putBoolean(ClipDescription.EXTRA_IS_SENSITIVE, true)
                                            }
                                }
                )
            }
            .isSuccess
}

private fun shareLines(context: Context, lines: List<PcLogLine>) {
    if (lines.isEmpty()) return
    val send =
            Intent(Intent.ACTION_SEND).apply {
                type = "text/plain"
                putExtra(Intent.EXTRA_SUBJECT, context.getString(R.string.pc_diag_share_title))
                putExtra(Intent.EXTRA_TEXT, PcDiagnostics.formatForShare(lines))
            }
    runCatching {
        context.startActivity(
                Intent.createChooser(send, context.getString(R.string.pc_diag_share_title))
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        )
    }
}
