package com.clindsay94.remex.ui.routines

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clindsay94.remex.R
import com.clindsay94.remex.routines.RoutineReasonText
import com.clindsay94.remex.routines.model.RoutineReasonArgs
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunOrigins
import com.clindsay94.remex.routines.model.RoutineRunSourceDetail
import com.clindsay94.remex.routines.model.RoutineRunOutcomes
import com.clindsay94.remex.routines.model.RoutineRunStep
import com.clindsay94.remex.routines.model.RoutineStep
import com.clindsay94.remex.routines.model.RoutineStepStatuses
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.RemexTooltip
import com.clindsay94.remex.ui.screens.RemexLoadingIndicator
import com.clindsay94.remex.ui.theme.LocalReducedMotion
import java.time.LocalDate
import java.time.ZoneId

/** Run history, newest first, grouped by day (routines spec A8, R-UX-27). */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalFoundationApi::class)
@Composable
internal fun RoutineHistoryPane(
    viewModel: RoutinesViewModel,
    routineId: String?,
    showBack: Boolean,
    onBack: () -> Unit,
    onOpenRun: (String) -> Unit,
) {
    val history by viewModel.history.collectAsStateWithLifecycle()
    val routines by viewModel.routines.collectAsStateWithLifecycle()
    val pcs by viewModel.pcs.collectAsStateWithLifecycle()
    val runs = if (routineId == null) history else history.filter { it.routineId == routineId }
    val routineName = routineId?.let { id -> routines.firstOrNull { it.routine.id == id }?.routine?.name ?: runs.firstOrNull()?.routineName }
    val zone = remember { ZoneId.systemDefault() }
    val groups = remember(runs) { RoutineRunViews.groupByDay(runs, zone) }
    val today = LocalDate.now(zone)
    val locale = appLocale()
    val hostSync by viewModel.hostSync.collectAsStateWithLifecycle()
    val connectedPc by viewModel.connectedPc.collectAsStateWithLifecycle()
    // PC history is a cache while that PC is not connected: say how old it is (R-UX-29).
    val staleHosts =
        runs.filter { it.origin == RoutineRunOrigins.PC }.mapNotNull { it.hostIdentity }.distinct()
            .filter { it != connectedPc }
            .mapNotNull { host -> hostSync[host]?.historyAsOfUnixMs?.let { host to it } }

    Scaffold(
        topBar = {
            RemexFlexibleTopBar(
                title = stringResource(R.string.routines_history),
                subtitle = routineName,
                navigationIcon = { if (showBack) BackButton(onBack) },
            )
        },
    ) { padding ->
        if (runs.isEmpty()) {
            Box(Modifier.fillMaxSize().padding(padding).padding(24.dp), contentAlignment = Alignment.Center) {
                Text(stringResource(R.string.routines_history_empty), style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            return@Scaffold
        }
        LazyColumn(
            contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = padding.calculateTopPadding(), bottom = padding.calculateBottomPadding() + 24.dp),
            verticalArrangement = Arrangement.spacedBy(4.dp),
            modifier = Modifier.fillMaxSize(),
        ) {
            items(staleHosts, key = { "as-of-" + it.first }) { (host, asOf) ->
                Text(
                    stringResource(
                        R.string.routines_history_as_of,
                        pcLabel(pcs.firstOrNull { it.identity == host }?.name),
                        RoutineTimeText.time(asOf, locale = locale),
                    ),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(vertical = 4.dp),
                )
            }
            groups.forEach { (day, dayRuns) ->
                stickyHeader(key = "day-$day") {
                    GroupHeader(
                        when (day) {
                            today -> stringResource(R.string.routines_day_today)
                            today.minusDays(1) -> stringResource(R.string.routines_day_yesterday)
                            else -> RoutineTimeText.date(day, locale)
                        },
                    )
                }
                items(dayRuns, key = { "run-" + it.runId }) { run ->
                    RunRow(
                        run = run,
                        pcName = run.hostIdentity?.let { h -> pcs.firstOrNull { it.identity == h }?.name },
                        showName = routineId == null,
                        onClick = { run.runId?.let(onOpenRun) },
                        modifier = Modifier.animateItem(placementSpec = MaterialTheme.motionScheme.fastSpatialSpec()),
                    )
                }
            }
        }
    }
}

@Composable
internal fun BackButton(onBack: () -> Unit) {
    val back = stringResource(R.string.cd_back)
    RemexTooltip(back) { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = back) } }
}

/** Codes whose fix is in Connection settings (spec A8 "contextual fix"). */
private val connectionFixCodes =
    setOf(
        RoutineReasonCodes.PC_UNREACHABLE,
        RoutineReasonCodes.WAIT_TIMEOUT,
        RoutineReasonCodes.WAKE_NO_MAC,
        RoutineReasonCodes.PC_NOT_SELECTED,
        RoutineReasonCodes.PC_NOT_PAIRED,
        RoutineReasonCodes.TRANSPORT_LOST,
    )

/** One run, step by step (spec A8, R-UX-28). Live while the run is in progress (M7). */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
internal fun RoutineRunPane(
    viewModel: RoutinesViewModel,
    runId: String,
    showBack: Boolean,
    onBack: () -> Unit,
    onNavigateToConnection: () -> Unit,
    onEdit: (String) -> Unit,
) {
    val history by viewModel.history.collectAsStateWithLifecycle()
    val routines by viewModel.routines.collectAsStateWithLifecycle()
    val pcs by viewModel.pcs.collectAsStateWithLifecycle()
    val run = history.firstOrNull { it.runId == runId }
    val locale = appLocale()
    val context = LocalContext.current
    var confirmRun by remember { mutableStateOf(false) }

    Scaffold(
        topBar = {
            RemexFlexibleTopBar(
                title =
                    run?.let { stringResource(R.string.routines_run_title, RoutineTimeText.clock(it.triggeredAtUnixMs, locale = locale)) }
                        ?: stringResource(R.string.routines_history),
                subtitle =
                    run?.let {
                        if (it.testRun) stringResource(R.string.routines_run_subtitle_test, it.routineName.orEmpty()) else it.routineName
                    },
                navigationIcon = { if (showBack) BackButton(onBack) },
            )
        },
    ) { padding ->
        if (run == null) {
            Box(Modifier.fillMaxSize().padding(padding).padding(24.dp), contentAlignment = Alignment.Center) {
                Text(stringResource(R.string.routines_run_missing), style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            return@Scaffold
        }
        val item = routines.firstOrNull { it.routine.id == run.routineId }
        val routine = item?.routine
        val pcName = run.hostIdentity?.let { h -> pcs.firstOrNull { it.identity == h }?.name }
        val edited = RoutineRunViews.editedSince(run, routine)
        val definitions: List<RoutineStep?> = if (!edited) routine?.steps.orEmpty() else emptyList()
        val running = run.outcome == RoutineRunOutcomes.RUNNING
        val stepCodes = run.steps.orEmpty().mapNotNull { it.reasonCode }.toSet()

        Column(
            Modifier.fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp, vertical = 8.dp)
                .widthIn(max = 640.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            OutcomeLine(runOutcomeLook(run.outcome, run.testRun), runOutcomeText(run, pcName))
            run.endedAtUnixMs?.let { end ->
                val seconds = ((end - run.triggeredAtUnixMs).coerceAtLeast(0L) / 1000L).toString()
                Text(
                    stringResource(R.string.routines_run_took, RoutineReasonText.formatDuration(context, seconds)),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            // A run-level reason that no step carries (a skip, a refused start) gets its full message.
            val runCode = run.reasonCode
            if (runCode != null && runCode != RoutineReasonCodes.OK && runCode !in stepCodes) {
                Text(
                    RoutineReasonText.message(context, runCode, (run.reasonArgs ?: RoutineReasonArgs()).withPc(pcName)),
                    style = MaterialTheme.typography.bodyMedium,
                )
            }

            // Where it ran, and the run's attributes (a dry run, a simulated test step, a countdown
            // nobody saw): each changes what the outcome line means, so each is said (spec 1.10).
            if (run.origin == RoutineRunOrigins.PC) {
                Text(stringResource(R.string.routines_run_origin_pc, pcLabel(pcName)), style = MaterialTheme.typography.bodyMedium)
            }
            val attributes = run.attributes.orEmpty().filter { it in RoutineReasonText.coveredCodes && it != runCode }.distinct()
            if (attributes.isNotEmpty()) {
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    attributes.forEach { code ->
                        Surface(color = MaterialTheme.colorScheme.secondaryContainer, shape = MaterialTheme.shapes.small) {
                            Text(
                                RoutineReasonText.history(context, code, (run.reasonArgs ?: RoutineReasonArgs()).withPc(pcName)),
                                style = MaterialTheme.typography.labelMedium,
                                color = MaterialTheme.colorScheme.onSecondaryContainer,
                                modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp),
                            )
                        }
                    }
                }
            }

            // Timeline: the trigger node, then each step.
            TimelineNode(
                look = OutcomeLook(triggerIcon(routine?.trigger?.type ?: RoutineTriggerTypes.MANUAL), MaterialTheme.colorScheme.primary),
                title = stringResource(RoutineTriggerText.title(routine?.trigger?.type ?: RoutineTriggerTypes.MANUAL)),
                trailing = RoutineTimeText.clock(run.triggeredAtUnixMs, locale = locale),
                hasNext = run.steps.orEmpty().isNotEmpty(),
                spinning = false,
                message = run.sourceDetail?.let { sourceDetailText(it) },
            )
            val steps = run.steps.orEmpty()
            steps.forEachIndexed { i, step ->
                RunStepNode(
                    step = step,
                    definition = definitions.getOrNull(step.index),
                    pcName = pcName,
                    hasNext = i < steps.lastIndex,
                    onFix = if (step.reasonCode in connectionFixCodes) onNavigateToConnection else null,
                )
            }

            if (edited) {
                Text(stringResource(R.string.routines_run_edited_since), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                if (running) {
                    OutlinedButton(onClick = { run.routineId?.let(viewModel::cancel) }) { Text(stringResource(R.string.routines_stop)) }
                } else if (
                    item != null && item.verdict.isValid &&
                    (item.routine.trigger?.type == RoutineTriggerTypes.MANUAL || RoutineTriggerTypes.isHostRun(item.routine.trigger?.type))
                ) {
                    Button(onClick = {
                        // A real run of a routine that powers the PC off confirms first, as on the list.
                        if (!run.testRun && item.routine.steps.orEmpty().any { it?.isDestructive == true }) {
                            confirmRun = true
                        } else {
                            viewModel.run(item.routine.id.orEmpty(), testRun = run.testRun)
                        }
                    }) {
                        Text(stringResource(if (run.testRun) R.string.routines_test_again else R.string.routines_run_again))
                    }
                }
                if (item != null) {
                    OutlinedButton(onClick = { onEdit(item.routine.id.orEmpty()) }) { Text(stringResource(R.string.routines_edit)) }
                }
            }
            Spacer(Modifier.height(24.dp))
        }
        if (confirmRun && item != null) {
            RunConfirmDialog(
                item = item,
                pcName = pcName,
                onConfirm = {
                    confirmRun = false
                    viewModel.run(item.routine.id.orEmpty(), testRun = false)
                },
                onDismiss = { confirmRun = false },
            )
        }
    }
}

@Composable
private fun RunStepNode(step: RoutineRunStep, definition: RoutineStep?, pcName: String?, hasNext: Boolean, onFix: (() -> Unit)?) {
    val context = LocalContext.current
    val label = stepChipLabel(definition ?: RoutineStep(type = step.kind))
    val status = step.status
    val trailing =
        when {
            status == RoutineStepStatuses.PENDING || status == null -> stringResource(R.string.routines_step_not_run)
            step.startedAtUnixMs != null && step.endedAtUnixMs != null -> {
                val seconds = ((step.endedAtUnixMs - step.startedAtUnixMs).coerceAtLeast(0L) / 1000L).toString()
                RoutineReasonText.formatDuration(context, seconds)
            }
            else -> null
        }
    val args = (step.reasonArgs ?: RoutineReasonArgs()).withPc(pcName)
    val message =
        step.reasonCode?.takeIf { it != RoutineReasonCodes.OK && status != RoutineStepStatuses.SUCCEEDED }?.let { code ->
            RoutineReasonText.message(context, code, args)
        }
    val statusText =
        when (status) {
            RoutineStepStatuses.SUCCEEDED -> stringResource(R.string.routine_history_ok)
            RoutineStepStatuses.RUNNING -> stringResource(R.string.routines_outcome_running)
            RoutineStepStatuses.PENDING, null -> null
            else -> RoutineReasonText.history(context, step.reasonCode, args)
        }
    TimelineNode(
        look = stepOutcomeLook(status),
        title = label,
        trailing = trailing,
        hasNext = hasNext,
        spinning = status == RoutineStepStatuses.RUNNING,
        statusText = statusText,
        message = message,
        onFix = onFix,
    )
}

/**
 * A node of the run timeline: an icon (a loading indicator while that step runs, M7; a still icon
 * under reduced motion), a title, a trailing time and an optional message and fix.
 */
/**
 * What the trigger saw (`sourceDetail`, §6.8): "After 10 minutes idle", "GPU Core reached 92 °C",
 * "Session: Locked", "Home: Flat". Null when the run carries nothing to say.
 */
@Composable
private fun sourceDetailText(detail: RoutineRunSourceDetail): String? {
    val context = LocalContext.current
    val parts =
        buildList {
            detail.idleMinutes?.let {
                add(stringResource(R.string.routines_run_source_idle, RoutineReasonText.formatDuration(context, (it * 60).toString())))
            }
            val sensor = detail.sensorName?.takeIf { it.isNotBlank() }
            val value = detail.value?.let { v -> listOf(RoutineSensorCatalog.formatLimit(v), detail.unit.orEmpty()).filter { it.isNotBlank() }.joinToString(" ") }
            if (sensor != null && value != null) add(stringResource(R.string.routines_run_source_sensor, sensor, value))
            detail.sessionState?.let { add(stringResource(R.string.routines_run_source_session, stringResource(RoutineTriggerText.sessionState(it)))) }
            detail.homeLabel?.takeIf { it.isNotBlank() }?.let { add(stringResource(R.string.routines_run_source_home, it)) }
        }
    return parts.takeIf { it.isNotEmpty() }?.joinToString("\n")
}

@Composable
private fun TimelineNode(
    look: OutcomeLook,
    title: String,
    trailing: String?,
    hasNext: Boolean,
    spinning: Boolean,
    statusText: String? = null,
    message: String? = null,
    onFix: (() -> Unit)? = null,
) {
    val reduced = LocalReducedMotion.current
    Row(Modifier.fillMaxWidth()) {
        Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.width(32.dp)) {
            Box(Modifier.size(28.dp), contentAlignment = Alignment.Center) {
                if (spinning && !reduced) {
                    RemexLoadingIndicator(modifier = Modifier.size(28.dp))
                } else {
                    Icon(look.icon, contentDescription = null, tint = look.tint, modifier = Modifier.size(22.dp))
                }
            }
            if (hasNext) {
                Box(Modifier.width(2.dp).heightIn(min = 24.dp).background(MaterialTheme.colorScheme.outlineVariant))
            }
        }
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f).padding(bottom = 12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(title, style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f).semantics { heading() })
                if (trailing != null) Text(trailing, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            if (statusText != null) {
                Text(
                    statusText,
                    style = MaterialTheme.typography.bodySmall,
                    color = look.tint,
                    modifier = Modifier.semantics { contentDescription = statusText },
                )
            }
            if (message != null && message != statusText) {
                Text(message, style = MaterialTheme.typography.bodyMedium)
            }
            if (onFix != null) {
                OutlinedButton(onClick = onFix) { Text(stringResource(R.string.routines_fix_connection)) }
            }
        }
    }
}
