package com.clindsay94.remex.ui.routines

import android.view.HapticFeedbackConstants
import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.isImeVisible
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowDropDown
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.DragHandle
import androidx.compose.material.icons.filled.Error
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.PhoneAndroid
import androidx.compose.material.icons.filled.Science
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FloatingToolbarDefaults
import androidx.compose.material3.HorizontalFloatingToolbar
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.ToggleButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.SheetValue
import androidx.compose.material3.rememberBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.positionInParent
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalResources
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.customActions
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.zIndex
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clindsay94.remex.R
import com.clindsay94.remex.routines.RoutineReasonText
import com.clindsay94.remex.routines.RoutineSaveResult
import com.clindsay94.remex.routines.RoutineSyncStates
import com.clindsay94.remex.routines.model.RoutineLimits
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import com.clindsay94.remex.routines.model.RoutineSessionStates
import com.clindsay94.remex.routines.model.RoutineNotifyTargets
import com.clindsay94.remex.routines.model.RoutinePowerVerbs
import com.clindsay94.remex.routines.model.RoutineReasonArgs
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineSensorDirections
import com.clindsay94.remex.routines.model.RoutineStep
import com.clindsay94.remex.routines.model.RoutineStepStatuses
import com.clindsay94.remex.routines.model.RoutineStepTypes
import com.clindsay94.remex.routines.model.RoutineTrigger
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import com.clindsay94.remex.ui.components.RemexTooltip
import com.clindsay94.remex.ui.screens.RemexLoadingIndicator
import com.clindsay94.remex.ui.theme.LocalReducedMotion
import kotlin.math.roundToInt
import kotlinx.coroutines.launch

/** The step sheet: choosing a kind (grid) or editing one step's parameters (spec A5). */
private data class StepSheet(val editIndex: Int?, val working: RoutineStep?)

/**
 * The routine editor (routines spec A4, 1.6-1.8; R-UX-08-11, 16, 17, 55, 58). The draft lives in
 * [RoutinesViewModel]; nothing is stored until Save.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class, ExperimentalLayoutApi::class)
@Composable
internal fun RoutineEditorPane(
    viewModel: RoutinesViewModel,
    source: RoutineDetail.Editor,
    onClose: () -> Unit,
    onOpenHistory: (String) -> Unit,
    onNavigateToConnection: () -> Unit,
) {
    val routines by viewModel.routines.collectAsStateWithLifecycle()
    val status by viewModel.status.collectAsStateWithLifecycle()
    LaunchedEffect(source, routines, status.health) { viewModel.openEditor(source) }

    val draftOrNull by viewModel.draft.collectAsStateWithLifecycle()
    val original by viewModel.draftOriginal.collectAsStateWithLifecycle()
    val pcs by viewModel.pcs.collectAsStateWithLifecycle()
    val launcher by viewModel.launcher.collectAsStateWithLifecycle()
    val selectedMac by viewModel.selectedMac.collectAsStateWithLifecycle()
    val selectedPc by viewModel.selectedPc.collectAsStateWithLifecycle()
    val mediaKeys by viewModel.mediaKeys.collectAsStateWithLifecycle()
    val activeRuns by viewModel.activeRuns.collectAsStateWithLifecycle()
    val pcActiveRuns by viewModel.pcActiveRuns.collectAsStateWithLifecycle()
    val hostSync by viewModel.hostSync.collectAsStateWithLifecycle()
    val pausedAll by viewModel.pausedAll.collectAsStateWithLifecycle()
    val routinesSupport by viewModel.routinesSupport.collectAsStateWithLifecycle()
    val isConnected by viewModel.isConnected.collectAsStateWithLifecycle()
    val sensors by viewModel.sensors.collectAsStateWithLifecycle()
    // A new draft opened before the PCs loaded picks up the default PC and its MAC once they do.
    LaunchedEffect(draftOrNull?.isNew, pcs, selectedPc, selectedMac) { viewModel.adoptDefaultPc() }
    // A health template opened before the PC's sensors arrived picks its sensor once they do.
    LaunchedEffect(draftOrNull?.templateId, draftOrNull?.hostIdentity, sensors) { viewModel.adoptTemplateSensor() }
    val context = LocalContext.current
    val resources = LocalResources.current
    val view = LocalView.current
    val scope = rememberCoroutineScope()

    val draft = draftOrNull
    if (draft == null) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text(stringResource(R.string.routines_loading), style = MaterialTheme.typography.bodyLarge)
        }
        return
    }
    // Recomputed whenever the connected PC's launcher list or the selected MAC changes.
    val env = remember(draft.hostIdentity, launcher, mediaKeys, sensors, selectedMac, selectedPc) { viewModel.environmentFor(draft.hostIdentity) }
    val problems = RoutineEditorRules.problems(draft, env)
    val errors = RoutineEditorRules.errors(problems)
    val dirty = original?.let { !draft.sameContentAs(it) } ?: false
    val readOnly = status.readOnly
    val pcName = draft.hostIdentity?.let { id -> pcs.firstOrNull { it.identity == id }?.name }
    val activeRun = draft.base?.id?.let { activeRuns[it] ?: pcActiveRuns[it] }
    val pcView = draft.hostIdentity?.let { RoutineSyncStates.pc(it, hostSync[it]) }
    // The stored routine's sync state; an unsaved edit is by definition not on the PC yet.
    val syncView = draft.base?.takeIf { draft.runsOnPc && !dirty }?.let { RoutineSyncStates.routine(it, draft.hostIdentity?.let { h -> hostSync[h] }) }
    // Only a definite "no" from THIS connection to the target PC disables the PC triggers (§7.6).
    val pcTooOld = routinesSupport.first != null && routinesSupport.first == draft.hostIdentity && routinesSupport.second == false

    var showProblems by rememberSaveable(source) { mutableStateOf(source.templateId != null) }
    var confirmDiscard by remember { mutableStateOf(false) }
    var confirmDelete by remember { mutableStateOf(false) }
    var testDialog by remember { mutableStateOf(false) }
    var triggerSheet by remember { mutableStateOf(false) }
    // A trigger change waiting for "Remove it?" (R-UX-11): the new type and the steps it removes.
    var pendingTriggerChange by remember { mutableStateOf<Pair<String, List<Int>>?>(null) }
    var stepSheet by remember { mutableStateOf<StepSheet?>(null) }
    var menuOpen by remember { mutableStateOf(false) }
    val scroll = rememberScrollState()
    val targetY = remember { mutableStateMapOf<ProblemTarget, Int>() }

    val autoName =
        RoutineEditorRules.autoName(
            triggerChip = draft.trigger?.type?.let { stringResource(RoutineTriggerText.chip(it)) },
            firstStepChip = draft.steps.firstOrNull()?.let { stepChipLabel(it.step) },
            pair = { a, b -> resources.getString(R.string.routines_auto_name, a, b) },
            fallback = stringResource(R.string.routines_new_routine),
        )

    BackHandler(enabled = dirty) { confirmDiscard = true }

    fun requestClose() {
        if (dirty) confirmDiscard = true else onClose()
    }

    /** Save (spec 1.8): with errors, scroll to the first, outline all, and say how many. */
    fun attemptSave(afterSave: ((String) -> Unit)? = null) {
        showProblems = true
        if (errors.isNotEmpty()) {
            view.performHapticFeedback(HapticFeedbackConstants.REJECT)
            val first = errors.first().target
            // Step cards report their offset inside the step list, which sits under the THEN label.
            val y =
                if (first is ProblemTarget.Step) {
                    (targetY[ProblemTarget.Steps] ?: 0) + (targetY[first] ?: 0)
                } else {
                    targetY[first] ?: targetY[ProblemTarget.Steps]
                }
            if (y != null) scope.launch { scroll.animateScrollTo((y - 48).coerceAtLeast(0)) }
            viewModel.post(resources.getQuantityString(R.plurals.routines_fix_count, errors.size, errors.size))
            return
        }
        scope.launch {
            when (val result = viewModel.saveDraft(autoName)) {
                is RoutineSaveResult.Saved -> {
                    view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                    val id = result.routine.id.orEmpty()
                    if (afterSave != null) {
                        afterSave(id)
                    } else if (draft.runsOnPc) {
                        // Spec 1.3 step 6: where a PC routine goes next, in plain words.
                        val connected = viewModel.connectedPc.value == draft.hostIdentity
                        val text =
                            resources.getString(
                                if (connected) R.string.routines_saved_sent_pc else R.string.routines_saved_pc_later,
                                pcLabelText(resources, pcName),
                            )
                        viewModel.postMessage(RoutinesMessage(text, if (draft.isNew) RoutinesMessageAction.TestNow(id) else null))
                    } else if (draft.isNew) {
                        viewModel.postMessage(RoutinesMessage(resources.getString(R.string.routines_saved_new), RoutinesMessageAction.TestNow(id)))
                    } else {
                        viewModel.post(resources.getString(R.string.routines_saved))
                    }
                }
                else -> {
                    view.performHapticFeedback(HapticFeedbackConstants.REJECT)
                    viewModel.post(viewModel.saveFailureText(result))
                }
            }
        }
    }

    fun startTest() {
        if (errors.isNotEmpty()) {
            attemptSave()
            return
        }
        val hasDestructive = draft.steps.any { it.step.isDestructive }
        if (dirty || draft.isNew || hasDestructive) testDialog = true else draft.base?.id?.let { viewModel.run(it, testRun = true) }
    }

    Box(Modifier.fillMaxSize()) {
        Scaffold(
            topBar = {
                TopAppBar(
                    title = {
                        Text(
                            stringResource(if (draft.isNew) R.string.routines_new_routine else R.string.routines_edit_routine),
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    },
                    navigationIcon = {
                        val close = stringResource(R.string.routines_close_editor)
                        RemexTooltip(close) { IconButton(onClick = ::requestClose) { Icon(Icons.Default.Close, contentDescription = close) } }
                    },
                    actions = {
                        val id = draft.base?.id
                        if (id != null && !readOnly) {
                            val more = stringResource(R.string.cd_more_options)
                            RemexTooltip(more) { IconButton(onClick = { menuOpen = true }) { Icon(Icons.Default.MoreVert, contentDescription = more) } }
                            DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                                DropdownMenuItem(
                                    text = { Text(stringResource(R.string.routines_duplicate)) },
                                    onClick = {
                                        menuOpen = false
                                        viewModel.duplicate(id)
                                    },
                                )
                                DropdownMenuItem(
                                    text = { Text(stringResource(R.string.routines_delete), color = MaterialTheme.colorScheme.error) },
                                    onClick = {
                                        menuOpen = false
                                        confirmDelete = true
                                    },
                                )
                            }
                        }
                    },
                )
            },
        ) { padding ->
            Column(
                Modifier.fillMaxSize()
                    .padding(padding)
                    .imePadding()
                    .verticalScroll(scroll)
                    .padding(horizontal = 16.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Column(Modifier.widthIn(max = 640.dp).fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    OutlinedTextField(
                        value = draft.name,
                        onValueChange = { value -> viewModel.updateDraft { it.copy(name = value.take(RoutineLimits.MAX_NAME_LENGTH)) } },
                        label = { Text(stringResource(R.string.routines_name_label)) },
                        placeholder = { Text(autoName, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                        supportingText = { Text(stringResource(R.string.routines_name_counter, draft.name.length, RoutineLimits.MAX_NAME_LENGTH)) },
                        singleLine = true,
                        enabled = !readOnly,
                        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                        modifier = Modifier.fillMaxWidth(),
                    )

                    // "Runs on" and "Controls" (spec 0.4 principle 3, A4).
                    FlowRow(
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                        verticalArrangement = Arrangement.spacedBy(8.dp),
                        modifier = Modifier.onGloballyPositioned { targetY[ProblemTarget.Pc] = it.positionInParent().y.roundToInt() },
                    ) {
                        RunsOnChip(runsOnPc = draft.runsOnPc, pcName = pcName)
                        PcPickerChip(
                            pcs = pcs,
                            selected = draft.hostIdentity,
                            enabled = !readOnly,
                            onPick = { identity ->
                                viewModel.updateDraft { d ->
                                    val mac = viewModel.macFor(identity)
                                    d.copy(hostIdentity = identity).let { if (mac != null) it.withWakeMac(mac) else it }
                                }
                            },
                        )
                    }
                    problems.filter { it.target == ProblemTarget.Pc }.forEach { ProblemLine(it, pcName, showProblems) }
                    if (draft.runsOnPc) {
                        syncView?.let { OutcomeLine(routineSyncLook(it), routineSyncText(it, pcName)) }
                        pcView?.let { PcSyncBanners(pc = it, phonePaused = pausedAll, pcName = pcName) }
                    }

                    // The enable switch (spec 1.6): an existing, unedited routine saves at once and
                    // shares state with the list card (R-UX-19); otherwise it is part of the draft.
                    val onLabel = stringResource(R.string.routines_state_on)
                    val offLabel = stringResource(R.string.routines_state_off)
                    val enabledLabel = stringResource(R.string.routines_enabled_label)
                    Row(
                        Modifier.fillMaxWidth()
                            .heightIn(min = 56.dp)
                            .toggleable(
                                value = draft.enabled,
                                enabled = !readOnly,
                                role = Role.Switch,
                                onValueChange = { checked ->
                                    val id = draft.base?.id
                                    if (id != null && !dirty) viewModel.setEnabled(id, checked) else viewModel.updateDraft { it.copy(enabled = checked) }
                                },
                            )
                            .semantics { stateDescription = if (draft.enabled) onLabel else offLabel },
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Column(Modifier.weight(1f)) {
                            Text(enabledLabel, style = MaterialTheme.typography.bodyLarge)
                            Text(
                                stringResource(R.string.routines_enabled_supporting),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        Spacer(Modifier.width(16.dp))
                        Switch(
                            checked = draft.enabled,
                            onCheckedChange = null,
                            enabled = !readOnly,
                            thumbContent = if (draft.enabled) {
                                { Icon(Icons.Default.Check, contentDescription = null, modifier = Modifier.size(16.dp)) }
                            } else {
                                null
                            },
                        )
                    }

                    // WHEN
                    SectionLabel(stringResource(R.string.routines_when))
                    TriggerCard(
                        trigger = draft.trigger,
                        problems = problems.filter { it.target == ProblemTarget.Trigger },
                        showProblems = showProblems,
                        pcName = pcName,
                        enabled = !readOnly,
                        onClick = { triggerSheet = true },
                        modifier = Modifier.onGloballyPositioned { targetY[ProblemTarget.Trigger] = it.positionInParent().y.roundToInt() },
                    )
                    TriggerParameters(
                        trigger = draft.trigger,
                        enabled = !readOnly,
                        sensors = sensors.second?.takeIf { sensors.first != null && sensors.first == draft.hostIdentity },
                        connected = sensors.first != null && sensors.first == draft.hostIdentity,
                        draftKey = viewModel.draftSession,
                        pcName = pcName,
                        onChange = { trigger -> viewModel.updateDraft { it.copy(trigger = trigger) } },
                    )
                    Connector(dashed = false)

                    // THEN
                    val stepsAnchor = Modifier.onGloballyPositioned { targetY[ProblemTarget.Steps] = it.positionInParent().y.roundToInt() }
                    SectionLabel(stringResource(R.string.routines_then), stepsAnchor)
                    problems.filter { it.target == ProblemTarget.Steps }.forEach { ProblemLine(it, pcName, showProblems) }
                    StepList(
                        draft = draft,
                        problems = problems,
                        showProblems = showProblems,
                        pcName = pcName,
                        activeRun = activeRun,
                        enabled = !readOnly,
                        onEdit = { index -> stepSheet = StepSheet(editIndex = index, working = draft.steps[index].step) },
                        onMove = { from, to -> viewModel.updateDraft { it.moveStep(from, to) } },
                        onDuplicate = { index -> viewModel.updateDraft { it.duplicateStep(index) } },
                        onDelete = { index ->
                            val removed = draft.steps.getOrNull(index) ?: return@StepList
                            viewModel.updateDraft { it.removeStep(index) }
                            // Bound to THIS draft: after switching routines or closing, Undo does nothing.
                            val session = viewModel.draftSession
                            viewModel.postMessage(
                                RoutinesMessage(
                                    resources.getString(R.string.routines_step_deleted),
                                    RoutinesMessageAction.Undo {
                                        viewModel.updateDraftIn(session) { d ->
                                            val list = d.steps.toMutableList()
                                            list.add(index.coerceAtMost(list.size), removed.copy(key = d.nextKey()))
                                            d.copy(steps = list)
                                        }
                                    },
                                ),
                            )
                        },
                        onPositioned = { index, y -> targetY[ProblemTarget.Step(index)] = y },
                    )
                    Connector(dashed = true)
                    FilledTonalButton(
                        onClick = { stepSheet = StepSheet(editIndex = null, working = null) },
                        enabled = draft.canAddStep && !readOnly,
                        modifier = Modifier.heightIn(min = 48.dp),
                    ) { Text(stringResource(R.string.routines_add_step)) }
                    if (!draft.canAddStep) {
                        Text(
                            stringResource(R.string.routine_reason_too_many_steps),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                    Spacer(Modifier.height(120.dp))
                }
            }
        }

        // Test, History and Save (spec A4, 2.5): a floating toolbar with a vibrant FAB.
        val imeVisible = WindowInsets.isImeVisible
        HorizontalFloatingToolbar(
            expanded = !imeVisible,
            floatingActionButton = {
                val save = stringResource(R.string.routines_save)
                FloatingToolbarDefaults.VibrantFloatingActionButton(onClick = { if (!readOnly) attemptSave() }) {
                    Icon(Icons.Default.Check, contentDescription = save)
                }
            },
            colors = FloatingToolbarDefaults.vibrantFloatingToolbarColors(),
            modifier = Modifier.align(Alignment.BottomCenter).navigationBarsPadding().imePadding().padding(bottom = 16.dp),
        ) {
            TextButton(onClick = ::startTest, enabled = !readOnly && activeRun == null, modifier = Modifier.heightIn(min = 48.dp)) {
                Icon(Icons.Default.Science, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(6.dp))
                Text(stringResource(R.string.routines_test))
            }
            TextButton(
                onClick = { draft.base?.id?.let(onOpenHistory) },
                enabled = draft.base?.id != null,
                modifier = Modifier.heightIn(min = 48.dp),
            ) {
                Icon(Icons.Default.History, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(6.dp))
                Text(stringResource(R.string.routines_history))
            }
        }
    }

    if (triggerSheet) {
        TriggerPickerSheet(
            current = draft.trigger?.type,
            unavailableReason = { type ->
                when {
                    !RoutineTriggerTypes.isHostRun(type) -> null
                    pcTooOld -> resources.getString(R.string.routines_trigger_pc_too_old, pcLabelText(resources, pcName))
                    RoutineSyncStates.triggerAvailable(type, pcView) == false && type == RoutineTriggerTypes.PC_SENSOR ->
                        resources.getString(R.string.routines_trigger_sensor_unavailable, pcLabelText(resources, pcName))
                    RoutineSyncStates.triggerAvailable(type, pcView) == false ->
                        RoutineReasonText.message(
                            context,
                            if (type == RoutineTriggerTypes.PC_IDLE) RoutineReasonCodes.IDLE_SOURCE_UNAVAILABLE else RoutineReasonCodes.SESSION_SOURCE_UNAVAILABLE,
                            RoutineReasonArgs(pc = pcLabelText(resources, pcName)),
                        )
                    else -> null
                }
            },
            onDismiss = { triggerSheet = false },
            onPick = { type ->
                triggerSheet = false
                val invalid = RoutineEditorRules.stepsInvalidatedBy(type, draft.steps.map { it.step })
                if (invalid.isEmpty()) {
                    // Keep the parameters when re-picking the same trigger.
                    if (draft.trigger?.type != type) viewModel.updateDraft { it.copy(trigger = RoutineTriggerFamilies.newTrigger(type)) }
                } else {
                    pendingTriggerChange = type to invalid
                }
            },
        )
    }
    pendingTriggerChange?.let { (type, invalid) ->
        AlertDialog(
            onDismissRequest = { pendingTriggerChange = null },
            title = { Text(stringResource(R.string.routines_trigger_change_title)) },
            text = { Text(pluralStringResource(R.plurals.routines_trigger_change_body, invalid.size, invalid.size)) },
            confirmButton = {
                Button(onClick = {
                    pendingTriggerChange = null
                    viewModel.updateDraft { d ->
                        d.copy(trigger = RoutineTriggerFamilies.newTrigger(type), steps = d.steps.filterIndexed { i, _ -> i !in invalid })
                    }
                }) { Text(stringResource(R.string.routines_trigger_change_confirm)) }
            },
            dismissButton = { TextButton(onClick = { pendingTriggerChange = null }) { Text(stringResource(R.string.button_cancel)) } },
        )
    }

    stepSheet?.let { sheet ->
        StepSheetHost(
            sheet = sheet,
            draft = draft,
            pcName = pcName,
            mac = viewModel.macFor(draft.hostIdentity),
            apps = launcher.second?.takeIf { launcher.first == draft.hostIdentity },
            isConnected = isConnected,
            onRefreshApps = viewModel::refreshLauncher,
            onNavigateToConnection = onNavigateToConnection,
            onDismiss = { stepSheet = null },
            onChange = { stepSheet = it },
            onCommit = { index, step ->
                stepSheet = null
                viewModel.updateDraft { if (index == null) it.addStep(step) else it.updateStep(index, step) }
            },
        )
    }

    if (confirmDiscard) {
        DiscardChangesDialog(
            onDiscard = {
                confirmDiscard = false
                onClose()
            },
            onKeep = { confirmDiscard = false },
        )
    }
    if (confirmDelete) {
        DeleteConfirmDialog(
            name = draft.base?.name.orEmpty(),
            onConfirm = {
                confirmDelete = false
                draft.base?.id?.let(viewModel::delete)
                onClose()
            },
            onDismiss = { confirmDelete = false },
        )
    }
    if (testDialog) {
        TestDialog(
            draft = draft,
            pcName = pcName,
            needsSave = dirty || draft.isNew,
            onConfirm = {
                testDialog = false
                if (dirty || draft.isNew) {
                    attemptSave { id -> viewModel.run(id, testRun = true) }
                } else {
                    draft.base?.id?.let { viewModel.run(it, testRun = true) }
                }
            },
            onDismiss = { testDialog = false },
        )
    }
}

@Composable
private fun SectionLabel(text: String, modifier: Modifier = Modifier) {
    Text(
        text,
        style = MaterialTheme.typography.labelLarge,
        color = MaterialTheme.colorScheme.primary,
        modifier = modifier.fillMaxWidth().semantics { heading() },
    )
}

/** The line between WHEN and THEN, and the dashed "next goes here" after the last step. */
@Composable
private fun Connector(dashed: Boolean) {
    val color = MaterialTheme.colorScheme.outlineVariant
    Box(Modifier.fillMaxWidth().padding(start = 28.dp)) {
        if (dashed) {
            Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                repeat(3) { Box(Modifier.width(2.dp).height(4.dp).background(color)) }
            }
        } else {
            Box(Modifier.width(2.dp).height(16.dp).background(color))
        }
    }
}

@Composable
internal fun RunsOnChip(runsOnPc: Boolean, pcName: String?) {
    Surface(color = MaterialTheme.colorScheme.secondaryContainer, contentColor = MaterialTheme.colorScheme.onSecondaryContainer, shape = MaterialTheme.shapes.small) {
        Row(Modifier.heightIn(min = 32.dp).padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(if (runsOnPc) Icons.Default.Computer else Icons.Default.PhoneAndroid, contentDescription = null, modifier = Modifier.size(18.dp))
            Spacer(Modifier.width(8.dp))
            Text(
                if (runsOnPc) stringResource(R.string.routines_runs_on_pc, pcLabel(pcName)) else stringResource(R.string.routines_runs_on_phone),
                style = MaterialTheme.typography.labelLarge,
            )
        }
    }
}

/** "Controls <PC>": a picker when more than one PC is paired (spec 1.6 "Multiple PCs"). */
@Composable
private fun PcPickerChip(pcs: List<RoutinePc>, selected: String?, enabled: Boolean, onPick: (String) -> Unit) {
    var open by remember { mutableStateOf(false) }
    val name = pcs.firstOrNull { it.identity == selected }?.name
    val label =
        if (selected == null) stringResource(R.string.routines_no_pc_chip) else stringResource(R.string.routines_controls_pc, pcLabel(name))
    Box {
        AssistChip(
            onClick = { if (pcs.size > 1) open = true },
            enabled = enabled,
            label = { Text(label) },
            leadingIcon = { Icon(Icons.Default.Computer, contentDescription = null, modifier = Modifier.size(18.dp)) },
            trailingIcon = if (pcs.size > 1) {
                { Icon(Icons.Default.ArrowDropDown, contentDescription = null) }
            } else {
                null
            },
            modifier = Modifier.heightIn(min = 48.dp),
        )
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            pcs.forEachIndexed { i, pc ->
                DropdownMenuItem(
                    text = { Text(pc.name ?: stringResource(R.string.routines_pc_unnamed, i + 1)) },
                    onClick = {
                        open = false
                        onPick(pc.identity)
                    },
                )
            }
        }
    }
}

@Composable
private fun TriggerCard(
    trigger: RoutineTrigger?,
    problems: List<EditorProblem>,
    showProblems: Boolean,
    pcName: String?,
    enabled: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val hasError = showProblems && problems.any { it.kind == ProblemKind.ERROR }
    Card(
        onClick = onClick,
        enabled = enabled,
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer, contentColor = MaterialTheme.colorScheme.onPrimaryContainer),
        border = if (hasError) BorderStroke(2.dp, MaterialTheme.colorScheme.error) else null,
        modifier = modifier.fillMaxWidth(),
    ) {
        Row(Modifier.padding(16.dp).heightIn(min = 48.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(triggerIcon(trigger?.type), contentDescription = null)
            Spacer(Modifier.width(16.dp))
            Column(Modifier.weight(1f)) {
                Text(
                    if (trigger?.type == null) stringResource(R.string.routines_choose_trigger) else stringResource(RoutineTriggerText.title(trigger.type)),
                    style = MaterialTheme.typography.titleMedium,
                )
                if (trigger?.type != null) {
                    Text(stringResource(RoutineTriggerText.supporting(trigger.type)), style = MaterialTheme.typography.bodySmall)
                }
            }
            Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, contentDescription = null)
        }
    }
    problems.forEach { ProblemLine(it, pcName, showProblems) }
}

/** A PC's display name outside composition (the "your PC" fallback). */
private fun pcLabelText(resources: android.content.res.Resources, name: String?): String =
    name?.takeIf { it.isNotBlank() } ?: resources.getString(R.string.routine_pc_fallback_name)

/**
 * The WHEN card's parameters for the PC triggers (spec A4 progressive disclosure): how long the PC
 * must be idle, whether playing media keeps it awake, which lock edge starts the routine, and for a
 * sensor, which one, above or below what limit, and for how long (§6.3).
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun TriggerParameters(
    trigger: RoutineTrigger?,
    enabled: Boolean,
    sensors: List<RoutineSensorOption>?,
    connected: Boolean,
    draftKey: Long,
    pcName: String?,
    onChange: (RoutineTrigger) -> Unit,
) {
    when (trigger?.type) {
        RoutineTriggerTypes.PC_SENSOR -> SensorParameters(trigger, enabled, sensors, connected, draftKey, pcName, onChange)
        RoutineTriggerTypes.PC_IDLE -> {
            val minutes = trigger.idleMinutes ?: RoutineTriggerText.DEFAULT_IDLE_MINUTES
            Text(stringResource(R.string.routines_trigger_idle_for), style = MaterialTheme.typography.labelLarge)
            if (enabled) {
                // The shared duration chips work in seconds; idle minutes are whole minutes.
                DurationChoices(RoutineTriggerText.idleChoices.map { it * 60 }, minutes * 60) { seconds ->
                    onChange(trigger.copy(idleMinutes = (seconds / 60).coerceAtLeast(1)))
                }
            }
            val media = trigger.ignoreWhileMediaPlaying ?: false
            val onLabel = stringResource(R.string.routines_state_on)
            val offLabel = stringResource(R.string.routines_state_off)
            Row(
                Modifier.fillMaxWidth()
                    .heightIn(min = 56.dp)
                    .toggleable(value = media, enabled = enabled, role = Role.Switch, onValueChange = { onChange(trigger.copy(ignoreWhileMediaPlaying = it)) })
                    .semantics { stateDescription = if (media) onLabel else offLabel },
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(stringResource(R.string.routines_trigger_idle_media), style = MaterialTheme.typography.bodyLarge, modifier = Modifier.weight(1f))
                Spacer(Modifier.width(16.dp))
                Switch(checked = media, onCheckedChange = null, enabled = enabled)
            }
        }
        RoutineTriggerTypes.PC_SESSION -> {
            Text(stringResource(R.string.routines_trigger_session_when), style = MaterialTheme.typography.labelLarge)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                listOf(RoutineSessionStates.LOCKED, RoutineSessionStates.UNLOCKED).forEach { state ->
                    FilterChip(
                        selected = trigger.sessionState == state,
                        onClick = { onChange(trigger.copy(sessionState = state)) },
                        enabled = enabled,
                        label = { Text(stringResource(RoutineTriggerText.sessionState(state))) },
                        modifier = Modifier.heightIn(min = 48.dp),
                    )
                }
            }
        }
        else -> Unit
    }
}

/**
 * `pc.sensor` (routines S5, §6.3): the sensor, picked from the target PC's own catalog by name and
 * group (its id is what the PC watches), above or below, the limit in the sensor's unit, and how long
 * it must hold (5 s to 10 min) under "More options" (spec 1.3 step 5).
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun SensorParameters(
    trigger: RoutineTrigger,
    enabled: Boolean,
    sensors: List<RoutineSensorOption>?,
    connected: Boolean,
    draftKey: Long,
    pcName: String?,
    onChange: (RoutineTrigger) -> Unit,
) {
    var picking by remember { mutableStateOf(false) }
    val chosen = sensors?.firstOrNull { it.id == trigger.sensorId }
    val label = chosen?.name ?: trigger.sensorLabel

    Text(stringResource(R.string.routines_trigger_sensor_which), style = MaterialTheme.typography.labelLarge)
    Box {
        OutlinedButton(
            onClick = { picking = true },
            enabled = enabled && !sensors.isNullOrEmpty(),
            modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp),
        ) {
            Text(
                label ?: stringResource(R.string.routines_trigger_sensor_choose),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            Icon(Icons.Default.ArrowDropDown, contentDescription = null)
        }
        DropdownMenu(expanded = picking, onDismissRequest = { picking = false }, modifier = Modifier.heightIn(max = 360.dp)) {
            sensors.orEmpty().forEach { option ->
                DropdownMenuItem(
                    text = {
                        Column {
                            Text(option.name, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            val detail = listOf(option.group, option.unit).filter { it.isNotBlank() }
                            if (detail.isNotEmpty()) {
                                // Two data values (the PC's group and unit), not a sentence.
                                Text(
                                    detail.joinToString(" · "),
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                        }
                    },
                    onClick = {
                        picking = false
                        onChange(RoutineSensorCatalog.choose(trigger, option))
                    },
                )
            }
        }
    }
    if (sensors.isNullOrEmpty()) {
        // Connected with a catalog that is empty is a PC with no sensors, not a PC to connect to.
        Text(
            if (connected && sensors != null) {
                stringResource(R.string.routines_trigger_sensor_none, pcLabel(pcName))
            } else {
                stringResource(R.string.routines_trigger_sensor_connect, pcLabel(pcName))
            },
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }

    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        listOf(RoutineSensorDirections.ABOVE, RoutineSensorDirections.BELOW).forEach { direction ->
            FilterChip(
                selected = trigger.direction == direction,
                onClick = { onChange(trigger.copy(direction = direction)) },
                enabled = enabled,
                label = { Text(stringResource(RoutineTriggerText.sensorDirection(direction))) },
                modifier = Modifier.heightIn(min = 48.dp),
            )
        }
    }

    // Text state of its own: "8" on the way to "85" or "-" on the way to "-5" must survive typing,
    // so the trigger only changes when the text is a number (an empty field clears the limit).
    var limitText by remember(draftKey, trigger.sensorId) { mutableStateOf(RoutineSensorCatalog.formatLimit(trigger.threshold)) }
    val unit = chosen?.unit.orEmpty()
    OutlinedTextField(
        value = limitText,
        onValueChange = { text ->
            limitText = text
            onChange(trigger.copy(threshold = RoutineSensorCatalog.parseLimit(text)))
        },
        enabled = enabled,
        singleLine = true,
        label = { Text(stringResource(R.string.routines_trigger_sensor_limit)) },
        suffix = if (unit.isNotBlank()) {
            { Text(unit) }
        } else {
            null
        },
        isError = RoutineSensorCatalog.parseLimit(limitText) == null,
        keyboardOptions = KeyboardOptions(keyboardType = androidx.compose.ui.text.input.KeyboardType.Decimal, imeAction = ImeAction.Done),
        modifier = Modifier.fillMaxWidth(),
    )

    var more by rememberSaveable { mutableStateOf(false) }
    TextButton(onClick = { more = !more }, modifier = Modifier.heightIn(min = 48.dp)) {
        Text(stringResource(R.string.routines_more_options))
    }
    if (more) {
        Text(stringResource(R.string.routines_trigger_sensor_for), style = MaterialTheme.typography.labelLarge)
        if (enabled) {
            DurationChoices(RoutineSensorCatalog.sustainChoices, trigger.sustainSeconds ?: RoutineLimits.DEFAULT_SUSTAIN_SECONDS) { seconds ->
                onChange(trigger.copy(sustainSeconds = seconds))
            }
        }
    }
}

/** One editor message (spec 1.8). Errors show once Save was tried (templates flag them at once). */
@Composable
private fun ProblemLine(problem: EditorProblem, pcName: String?, showProblems: Boolean) {
    if (problem.kind == ProblemKind.ERROR && !showProblems) return
    val scheme = MaterialTheme.colorScheme
    val (icon, tint) =
        when (problem.kind) {
            ProblemKind.ERROR -> Icons.Default.Error to scheme.error
            ProblemKind.WARNING -> Icons.Default.Warning to scheme.tertiary
            ProblemKind.INFO -> Icons.Default.Info to scheme.onSurfaceVariant
        }
    Row(verticalAlignment = Alignment.Top, horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.fillMaxWidth()) {
        Icon(icon, contentDescription = null, tint = tint, modifier = Modifier.size(18.dp))
        Text(problemText(problem, pcName), style = MaterialTheme.typography.bodySmall, color = if (problem.kind == ProblemKind.ERROR) scheme.error else scheme.onSurfaceVariant)
    }
}

@Composable
internal fun problemText(problem: EditorProblem, pcName: String?): String {
    val context = LocalContext.current
    val res =
        when (problem.code) {
            EditorProblemCode.NO_PC -> R.string.routines_problem_no_pc
            EditorProblemCode.NO_TRIGGER -> R.string.routines_problem_no_trigger
            EditorProblemCode.NO_STEPS -> R.string.routines_problem_no_steps
            EditorProblemCode.WAKE_NO_MAC -> R.string.routines_problem_wake_no_mac
            EditorProblemCode.CHOOSE_APP -> R.string.routines_problem_choose_app
            EditorProblemCode.APP_MISSING -> R.string.routines_problem_app_missing
            EditorProblemCode.NOTIFY_NO_TITLE -> R.string.routines_problem_notify_title
            EditorProblemCode.PHONE_ONLY -> R.string.routine_reason_step_not_allowed_on_pc
            EditorProblemCode.TOO_MANY_OF_KIND -> R.string.routines_problem_too_many_of_kind
            EditorProblemCode.DESTRUCTIVE_NOT_LAST -> R.string.routine_reason_destructive_not_last
            EditorProblemCode.TOO_MANY_DESTRUCTIVE -> R.string.routine_reason_too_many_destructive
            EditorProblemCode.AFTER_POWER_OFF -> R.string.routines_problem_after_power_off
            EditorProblemCode.COUNTDOWN -> R.string.routines_problem_countdown
            EditorProblemCode.BUDGET_EXCEEDED -> R.string.routine_reason_budget_exceeded
            EditorProblemCode.MEDIA_KEYS -> R.string.routine_reason_media_unavailable
            EditorProblemCode.CHOOSE_SENSOR -> R.string.routines_problem_choose_sensor
            EditorProblemCode.SENSOR_MISSING -> R.string.routines_problem_sensor_missing
            EditorProblemCode.SENSOR_LIMIT -> R.string.routines_problem_sensor_limit
            EditorProblemCode.SENSOR_SUSTAIN -> R.string.routines_problem_sensor_sustain
        }
    return RoutineReasonText.render(context, stringResource(res), RoutineReasonArgs(pc = pcName, app = problem.app))
}

/**
 * The THEN steps: numbered cards, drag to reorder (M5), overflow Move up / Move down / Duplicate /
 * Delete, and TalkBack actions Move up / down / to top / to bottom that announce the new position
 * (R-UX-17). Kept in a Column rather than a lazy list: twelve steps at most, inside the editor's
 * own scroll.
 */
@Composable
private fun StepList(
    draft: RoutineDraft,
    problems: List<EditorProblem>,
    showProblems: Boolean,
    pcName: String?,
    activeRun: RoutineRun?,
    enabled: Boolean,
    onEdit: (Int) -> Unit,
    onMove: (Int, Int) -> Unit,
    onDuplicate: (Int) -> Unit,
    onDelete: (Int) -> Unit,
    onPositioned: (Int, Int) -> Unit,
) {
    val view = LocalView.current
    val reduced = LocalReducedMotion.current
    val latest by rememberUpdatedState(draft)
    var draggingKey by remember { mutableStateOf<Long?>(null) }
    var dragOffset by remember { mutableFloatStateOf(0f) }
    val heights = remember { mutableStateMapOf<Long, Int>() }
    val total = draft.steps.size

    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        draft.steps.forEachIndexed { index, draftStep ->
            key(draftStep.key) {
                val dragging = draggingKey == draftStep.key
                val lift by androidx.compose.animation.core.animateFloatAsState(
                    if (dragging && !reduced) 1.03f else 1f,
                    MaterialTheme.motionScheme.fastSpatialSpec(),
                    label = "stepLift",
                )
                val handleModifier =
                    Modifier.pointerInput(draftStep.key, enabled) {
                        if (!enabled) return@pointerInput
                        detectDragGestures(
                            onDragStart = {
                                draggingKey = draftStep.key
                                dragOffset = 0f
                                view.performHapticFeedback(HapticFeedbackConstants.GESTURE_START)
                            },
                            onDrag = { change, amount ->
                                change.consume()
                                dragOffset += amount.y
                                val steps = latest.steps
                                val at = steps.indexOfFirst { it.key == draftStep.key }
                                if (at < 0) return@detectDragGestures
                                val spacing = 8.dp.toPx()
                                if (dragOffset > 0 && at < steps.lastIndex) {
                                    val next = heights[steps[at + 1].key] ?: return@detectDragGestures
                                    if (dragOffset > next / 2f) {
                                        onMove(at, at + 1)
                                        dragOffset -= next + spacing
                                        view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                                    }
                                } else if (dragOffset < 0 && at > 0) {
                                    val prev = heights[steps[at - 1].key] ?: return@detectDragGestures
                                    if (-dragOffset > prev / 2f) {
                                        onMove(at, at - 1)
                                        dragOffset += prev + spacing
                                        view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                                    }
                                }
                            },
                            onDragEnd = {
                                draggingKey = null
                                dragOffset = 0f
                                view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                            },
                            onDragCancel = {
                                draggingKey = null
                                dragOffset = 0f
                            },
                        )
                    }
                StepCard(
                    index = index,
                    total = total,
                    step = draftStep.step,
                    problems = problems.filter { it.target == ProblemTarget.Step(index) },
                    showProblems = showProblems,
                    pcName = pcName,
                    runStatus = activeRun?.steps?.firstOrNull { it.index == index }?.status,
                    enabled = enabled,
                    handleModifier = handleModifier,
                    onClick = { onEdit(index) },
                    onMove = onMove,
                    onDuplicate = { onDuplicate(index) },
                    onDelete = { onDelete(index) },
                    canDuplicate = draft.canAddStep,
                    modifier =
                        Modifier.zIndex(if (dragging) 1f else 0f)
                            .graphicsLayer {
                                translationY = if (dragging) dragOffset else 0f
                                scaleX = lift
                                scaleY = lift
                                shadowElevation = if (dragging) 6.dp.toPx() else 0f
                            }
                            .onGloballyPositioned {
                                heights[draftStep.key] = it.size.height
                                onPositioned(index, it.positionInParent().y.roundToInt())
                            },
                )
            }
        }
    }
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun StepCard(
    index: Int,
    total: Int,
    step: RoutineStep,
    problems: List<EditorProblem>,
    showProblems: Boolean,
    pcName: String?,
    runStatus: String?,
    enabled: Boolean,
    handleModifier: Modifier,
    onClick: () -> Unit,
    onMove: (Int, Int) -> Unit,
    onDuplicate: () -> Unit,
    onDelete: () -> Unit,
    canDuplicate: Boolean,
    modifier: Modifier = Modifier,
) {
    val scheme = MaterialTheme.colorScheme
    val discards = RoutineStepText.discardsWork(step)
    val hasError = showProblems && problems.any { it.kind == ProblemKind.ERROR }
    var menuOpen by remember { mutableStateOf(false) }
    val position = stringResource(R.string.routine_progress_step_count, index + 1, total)
    val moveUp = stringResource(R.string.routines_move_up)
    val moveDown = stringResource(R.string.routines_move_down)
    val moveTop = stringResource(R.string.routines_move_top)
    val moveBottom = stringResource(R.string.routines_move_bottom)
    val title =
        when (step.type) {
            RoutineStepTypes.POWER -> stringResource(RoutineStepText.powerVerb(step.verb))
            RoutineStepTypes.MEDIA -> stringResource(RoutineStepText.mediaAction(step.mediaAction))
            else -> stringResource(RoutineStepText.title(step.type))
        }
    val summary = stepSummary(step, pcName)?.takeIf { it != title }

    Card(
        onClick = onClick,
        enabled = enabled,
        colors =
            CardDefaults.cardColors(
                containerColor = if (discards) scheme.errorContainer else scheme.surfaceContainerLow,
                contentColor = if (discards) scheme.onErrorContainer else scheme.onSurface,
            ),
        border = if (hasError) BorderStroke(2.dp, scheme.error) else null,
        modifier =
            modifier.fillMaxWidth().semantics {
                stateDescription = position
                customActions =
                    buildList {
                        if (index > 0) {
                            add(CustomAccessibilityAction(moveUp) { onMove(index, index - 1); true })
                            add(CustomAccessibilityAction(moveTop) { onMove(index, 0); true })
                        }
                        if (index < total - 1) {
                            add(CustomAccessibilityAction(moveDown) { onMove(index, index + 1); true })
                            add(CustomAccessibilityAction(moveBottom) { onMove(index, total - 1); true })
                        }
                    }
            },
    ) {
        Column(Modifier.padding(start = 12.dp, top = 8.dp, bottom = 8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                StepBadge(number = index + 1, status = runStatus)
                Spacer(Modifier.width(12.dp))
                Icon(stepIcon(step), contentDescription = null, modifier = Modifier.size(20.dp))
                Spacer(Modifier.width(8.dp))
                Column(Modifier.weight(1f)) {
                    Text(title, style = MaterialTheme.typography.titleSmall, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    if (summary != null) Text(summary, style = MaterialTheme.typography.bodySmall, maxLines = 2, overflow = TextOverflow.Ellipsis)
                }
                Box {
                    val more = stringResource(R.string.routines_step_menu, index + 1)
                    IconButton(onClick = { menuOpen = true }, enabled = enabled) { Icon(Icons.Default.MoreVert, contentDescription = more) }
                    DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                        if (canDuplicate) DropdownMenuItem(text = { Text(stringResource(R.string.routines_duplicate_step)) }, onClick = { menuOpen = false; onDuplicate() })
                        if (index > 0) DropdownMenuItem(text = { Text(moveUp) }, onClick = { menuOpen = false; onMove(index, index - 1) })
                        if (index < total - 1) DropdownMenuItem(text = { Text(moveDown) }, onClick = { menuOpen = false; onMove(index, index + 1) })
                        DropdownMenuItem(
                            text = { Text(stringResource(R.string.routines_delete), color = scheme.error) },
                            onClick = { menuOpen = false; onDelete() },
                        )
                    }
                }
                val dragLabel = stringResource(R.string.routines_drag_to_reorder)
                RemexTooltip(dragLabel) {
                    Box(
                        handleModifier.size(48.dp).semantics { contentDescription = dragLabel },
                        contentAlignment = Alignment.Center,
                    ) { Icon(Icons.Default.DragHandle, contentDescription = null) }
                }
            }
            Column(Modifier.padding(end = 12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                problems.forEach { ProblemLine(it, pcName, showProblems) }
            }
        }
    }
}

/** The step number; while a run is live it becomes the step's progress (M7), gated on reduced motion. */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun StepBadge(number: Int, status: String?) {
    val reduced = LocalReducedMotion.current
    val scheme = MaterialTheme.colorScheme
    Box(
        Modifier.size(28.dp).clip(CircleShape).background(scheme.secondaryContainer),
        contentAlignment = Alignment.Center,
    ) {
        when {
            status == RoutineStepStatuses.RUNNING && !reduced -> RemexLoadingIndicator(modifier = Modifier.size(28.dp))
            status == RoutineStepStatuses.SUCCEEDED || status == RoutineStepStatuses.SIMULATED ->
                Icon(Icons.Default.Check, contentDescription = null, tint = scheme.onSecondaryContainer, modifier = Modifier.size(18.dp))
            status == RoutineStepStatuses.FAILED -> Icon(Icons.Default.Error, contentDescription = null, tint = scheme.error, modifier = Modifier.size(18.dp))
            else -> Text(number.toString(), style = MaterialTheme.typography.labelLarge, color = scheme.onSecondaryContainer)
        }
    }
}

// ── Sheets ───────────────────────────────────────────────────────────────────

/** "What starts this routine?" (spec A7). Lists only [RoutineTriggerFamilies.offered]. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TriggerPickerSheet(current: String?, unavailableReason: (String) -> String?, onDismiss: () -> Unit, onPick: (String) -> Unit) {
    val state = rememberExpandedSheetState()
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = state, sheetMaxWidth = 640.dp) {
        Column(Modifier.padding(bottom = 24.dp)) {
            Text(
                stringResource(R.string.routines_trigger_picker_title),
                style = MaterialTheme.typography.titleLarge,
                modifier = Modifier.padding(horizontal = 24.dp, vertical = 8.dp).semantics { heading() },
            )
            val phone = RoutineTriggerFamilies.offered.filterNot { RoutineTriggerTypes.isHostRun(it) }
            val pc = RoutineTriggerFamilies.offered.filter { RoutineTriggerTypes.isHostRun(it) }
            if (phone.isNotEmpty()) {
                PickerGroup(stringResource(R.string.routines_trigger_group_phone))
                phone.forEach { type -> TriggerOption(type, current == type, unavailableReason(type)) { onPick(type) } }
            }
            if (pc.isNotEmpty()) {
                PickerGroup(stringResource(R.string.routines_trigger_group_pc))
                pc.forEach { type -> TriggerOption(type, current == type, unavailableReason(type)) { onPick(type) } }
            }
        }
    }
}

/** Hidden or fully expanded, never half: the More sheet's state (AppNavigation). */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun rememberExpandedSheetState() =
    rememberBottomSheetState(initialValue = SheetValue.Hidden, enabledValues = setOf(SheetValue.Hidden, SheetValue.Expanded))

@Composable
private fun PickerGroup(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.labelLarge,
        color = MaterialTheme.colorScheme.primary,
        modifier = Modifier.padding(horizontal = 24.dp, vertical = 8.dp).semantics { heading() },
    )
}

@Composable
private fun TriggerOption(type: String, selected: Boolean, unavailable: String?, onClick: () -> Unit) {
    // A trigger the target PC cannot offer stays listed, disabled, with the reason (§7.5 gating).
    val enabled = unavailable == null || selected
    Row(
        Modifier.fillMaxWidth()
            .selectable(selected = selected, enabled = enabled, onClick = onClick, role = Role.RadioButton)
            .heightIn(min = 56.dp)
            .padding(horizontal = 24.dp, vertical = 8.dp)
            .graphicsLayer { alpha = if (enabled) 1f else 0.6f },
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(triggerIcon(type), contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.width(16.dp))
        Column(Modifier.weight(1f)) {
            Text(stringResource(RoutineTriggerText.title(type)), style = MaterialTheme.typography.bodyLarge)
            Text(
                unavailable ?: stringResource(RoutineTriggerText.supporting(type)),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        if (selected) Icon(Icons.Default.Check, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
    }
}

/** Add a step / edit a step (spec A5): the kind grid moves forward to that kind's parameters. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun StepSheetHost(
    sheet: StepSheet,
    draft: RoutineDraft,
    pcName: String?,
    mac: String?,
    apps: List<RoutineAppChoice>?,
    isConnected: Boolean,
    onRefreshApps: () -> Unit,
    onNavigateToConnection: () -> Unit,
    onDismiss: () -> Unit,
    onChange: (StepSheet) -> Unit,
    onCommit: (Int?, RoutineStep) -> Unit,
) {
    val state = rememberExpandedSheetState()
    val spatial = MaterialTheme.motionScheme.defaultSpatialSpec<androidx.compose.ui.unit.IntOffset>()
    val effects = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = state, sheetMaxWidth = 640.dp) {
        AnimatedContent(
            targetState = sheet.working == null,
            transitionSpec = {
                val forward = !targetState
                (slideInHorizontally(spatial) { w -> if (forward) w / 4 else -w / 4 } + fadeIn(effects)) togetherWith
                    (slideOutHorizontally(spatial) { w -> if (forward) -w / 4 else w / 4 } + fadeOut(effects))
            },
            label = "stepSheet",
        ) { choosing ->
            if (choosing) {
                StepKindGrid(
                    draft = draft,
                    onPick = { type -> onChange(sheet.copy(working = RoutineStepText.newStep(type, mac))) },
                )
            } else {
                val working = sheet.working ?: return@AnimatedContent
                StepParameters(
                    step = working,
                    isNew = sheet.editIndex == null,
                    pcName = pcName,
                    apps = apps,
                    isConnected = isConnected,
                    onRefreshApps = onRefreshApps,
                    onNavigateToConnection = onNavigateToConnection,
                    onChange = { onChange(sheet.copy(working = it)) },
                    onBack = if (sheet.editIndex == null) ({ onChange(sheet.copy(working = null)) }) else null,
                    onCommit = { onCommit(sheet.editIndex, it) },
                )
            }
        }
    }
}

@Composable
private fun StepKindGrid(draft: RoutineDraft, onPick: (String) -> Unit) {
    val types = draft.steps.map { it.step.type }
    val options = RoutineStepText.addOrder.map { it to RoutineEditorRules.availability(it, draft.trigger?.type, types) }
    // Unavailable tiles last, disabled, with their reason (spec A5).
    val ordered = options.filter { it.second == StepAvailability.AVAILABLE } + options.filter { it.second != StepAvailability.AVAILABLE }
    Column(Modifier.padding(horizontal = 16.dp).padding(bottom = 24.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(
            stringResource(R.string.routines_add_step_title),
            style = MaterialTheme.typography.titleLarge,
            modifier = Modifier.padding(horizontal = 8.dp, vertical = 8.dp).semantics { heading() },
        )
        ordered.chunked(2).forEach { row ->
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                row.forEach { (type, availability) ->
                    StepKindTile(type, availability, Modifier.weight(1f)) { onPick(type) }
                }
                if (row.size == 1) Spacer(Modifier.weight(1f))
            }
        }
    }
}

@Composable
private fun StepKindTile(type: String, availability: StepAvailability, modifier: Modifier, onClick: () -> Unit) {
    val available = availability == StepAvailability.AVAILABLE
    val step = RoutineStep(type = type)
    Surface(
        onClick = onClick,
        enabled = available,
        color = MaterialTheme.colorScheme.surfaceContainerHigh,
        shape = MaterialTheme.shapes.large,
        modifier = modifier.heightIn(min = 96.dp),
    ) {
        Column(
            Modifier.padding(12.dp).graphicsLayer { alpha = if (available) 1f else 0.38f },
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            Icon(stepIcon(step), contentDescription = null, tint = MaterialTheme.colorScheme.primary)
            Text(stringResource(RoutineStepText.title(type)), style = MaterialTheme.typography.titleSmall)
            Text(
                when (availability) {
                    StepAvailability.AVAILABLE -> stringResource(RoutineStepText.supporting(type))
                    StepAvailability.PHONE_ONLY -> stringResource(R.string.routine_reason_step_not_allowed_on_pc)
                    StepAvailability.LIMIT_REACHED -> stringResource(R.string.routines_problem_too_many_of_kind)
                },
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

/** One step's parameters: the one or two fields that matter (spec 1.3 step 5). */
@OptIn(ExperimentalLayoutApi::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun StepParameters(
    step: RoutineStep,
    isNew: Boolean,
    pcName: String?,
    apps: List<RoutineAppChoice>?,
    isConnected: Boolean,
    onRefreshApps: () -> Unit,
    onNavigateToConnection: () -> Unit,
    onChange: (RoutineStep) -> Unit,
    onBack: (() -> Unit)?,
    onCommit: (RoutineStep) -> Unit,
) {
    val context = LocalContext.current
    val scheme = MaterialTheme.colorScheme
    Column(
        Modifier.padding(horizontal = 24.dp).padding(bottom = 24.dp).verticalScroll(rememberScrollState()),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(stepIcon(step), contentDescription = null, tint = scheme.primary)
            Spacer(Modifier.width(12.dp))
            Text(stringResource(RoutineStepText.title(step.type)), style = MaterialTheme.typography.titleLarge, modifier = Modifier.semantics { heading() })
        }
        Text(stringResource(RoutineStepText.supporting(step.type)), style = MaterialTheme.typography.bodyMedium, color = scheme.onSurfaceVariant)

        when (step.type) {
            RoutineStepTypes.WAKE -> {
                if (step.mac != null) {
                    Text(stringResource(R.string.routines_wake_mac, step.mac), style = MaterialTheme.typography.bodyMedium)
                } else {
                    Text(
                        RoutineReasonText.render(context, stringResource(R.string.routines_problem_wake_no_mac), RoutineReasonArgs(pc = pcName)),
                        style = MaterialTheme.typography.bodyMedium,
                        color = scheme.error,
                    )
                    OutlinedButton(onClick = onNavigateToConnection) { Text(stringResource(R.string.routines_fix_connection)) }
                }
            }
            RoutineStepTypes.WAIT_ONLINE -> {
                Text(stringResource(R.string.routines_wait_up_to), style = MaterialTheme.typography.labelLarge)
                DurationChoices(RoutineStepText.waitOnlineChoices, step.timeoutSeconds ?: RoutineLimits.DEFAULT_WAIT_ONLINE_SECONDS) {
                    onChange(step.copy(timeoutSeconds = it))
                }
            }
            RoutineStepTypes.DELAY -> {
                Text(stringResource(R.string.routines_wait_for), style = MaterialTheme.typography.labelLarge)
                DurationChoices(RoutineStepText.delayChoices, step.seconds ?: 30) { onChange(step.copy(seconds = it)) }
            }
            RoutineStepTypes.POWER -> {
                Column {
                    RoutineStepText.powerVerbs.forEach { verb ->
                        val discards = RoutineStepText.discardsWork(RoutineStep(type = RoutineStepTypes.POWER, verb = verb))
                        Row(
                            Modifier.fillMaxWidth()
                                .heightIn(min = 48.dp)
                                .selectable(selected = step.verb == verb, onClick = { onChange(step.copy(verb = verb, delaySeconds = null)) }, role = Role.RadioButton),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            RadioButton(selected = step.verb == verb, onClick = null)
                            Spacer(Modifier.width(12.dp))
                            Icon(powerIcon(verb), contentDescription = null, tint = if (discards) scheme.error else scheme.onSurfaceVariant)
                            Spacer(Modifier.width(12.dp))
                            Text(stringResource(RoutineStepText.powerVerb(verb)), color = if (discards) scheme.error else scheme.onSurface)
                        }
                    }
                }
                if (RoutinePowerVerbs.isDestructive(step.verb)) {
                    Text(
                        RoutineReasonText.render(context, stringResource(R.string.routines_problem_countdown), RoutineReasonArgs(pc = pcName)),
                        style = MaterialTheme.typography.bodySmall,
                        color = scheme.onSurfaceVariant,
                    )
                }
            }
            RoutineStepTypes.LAUNCH_APP -> {
                if (apps.isNullOrEmpty()) {
                    Text(
                        RoutineReasonText.render(context, stringResource(R.string.routines_apps_unknown), RoutineReasonArgs(pc = pcName)),
                        style = MaterialTheme.typography.bodyMedium,
                    )
                    if (isConnected) {
                        OutlinedButton(onClick = onRefreshApps) { Text(stringResource(R.string.routines_apps_refresh)) }
                    } else {
                        OutlinedButton(onClick = onNavigateToConnection) { Text(stringResource(R.string.routines_fix_connection)) }
                    }
                    if (step.appLabel != null) Text(stringResource(R.string.routines_apps_current, step.appLabel), style = MaterialTheme.typography.bodySmall)
                } else {
                    Column {
                        apps.forEach { app ->
                            val selected = step.appId.equals(app.id, ignoreCase = true)
                            Row(
                                Modifier.fillMaxWidth()
                                    .heightIn(min = 48.dp)
                                    .selectable(
                                        selected = selected,
                                        onClick = { onChange(step.copy(appId = app.id, appLabel = app.name.take(RoutineLimits.MAX_LABEL_LENGTH))) },
                                        role = Role.RadioButton,
                                    ),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                RadioButton(selected = selected, onClick = null)
                                Spacer(Modifier.width(12.dp))
                                Text(app.name, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            }
                        }
                    }
                }
            }
            RoutineStepTypes.MEDIA -> {
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    // ToggleButtons, not ButtonGroup (its measure policy crashes; PersonalizationScreen.kt).
                    RoutineStepText.mediaActions.forEach { action ->
                        ToggleButton(
                            checked = (step.mediaAction ?: "") == action,
                            onCheckedChange = { onChange(step.copy(mediaAction = action)) },
                            modifier = Modifier.heightIn(min = 48.dp),
                        ) { Text(stringResource(RoutineStepText.mediaAction(action))) }
                    }
                }
            }
            RoutineStepTypes.NOTIFY -> {
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    ToggleButton(
                        checked = step.target != RoutineNotifyTargets.PC,
                        onCheckedChange = { onChange(step.copy(target = RoutineNotifyTargets.PHONE)) },
                        modifier = Modifier.heightIn(min = 48.dp),
                    ) { Text(stringResource(R.string.routines_notify_on_phone)) }
                    ToggleButton(
                        checked = step.target == RoutineNotifyTargets.PC,
                        onCheckedChange = { onChange(step.copy(target = RoutineNotifyTargets.PC)) },
                        modifier = Modifier.heightIn(min = 48.dp),
                    ) { Text(stringResource(R.string.routines_notify_on_pc)) }
                }
                OutlinedTextField(
                    value = step.title.orEmpty(),
                    onValueChange = { onChange(step.copy(title = it.take(RoutineLimits.MAX_NOTIFY_TITLE_LENGTH))) },
                    label = { Text(stringResource(R.string.routines_notify_title)) },
                    supportingText = { Text(stringResource(R.string.routines_name_counter, step.title.orEmpty().length, RoutineLimits.MAX_NOTIFY_TITLE_LENGTH)) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = step.body.orEmpty(),
                    onValueChange = { value -> onChange(step.copy(body = value.take(RoutineLimits.MAX_NOTIFY_BODY_LENGTH).ifEmpty { null })) },
                    label = { Text(stringResource(R.string.routines_notify_body)) },
                    supportingText = { Text(stringResource(R.string.routines_name_counter, step.body.orEmpty().length, RoutineLimits.MAX_NOTIFY_BODY_LENGTH)) },
                    minLines = 2,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
        }

        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.End)) {
            if (onBack != null) TextButton(onClick = onBack) { Text(stringResource(R.string.cd_back)) }
            Button(onClick = { onCommit(step) }) { Text(stringResource(if (isNew) R.string.routines_add else R.string.button_done)) }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun DurationChoices(choices: List<Int>, selected: Int, onPick: (Int) -> Unit) {
    val context = LocalContext.current
    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        val all = if (selected in choices) choices else (choices + selected).sorted()
        all.forEach { seconds ->
            FilterChip(
                selected = seconds == selected,
                onClick = { onPick(seconds) },
                label = { Text(RoutineReasonText.formatDuration(context, seconds.toString())) },
                modifier = Modifier.heightIn(min = 48.dp),
            )
        }
    }
}

/** "Save and test" / "Test <name>?" (spec 1.6, D7, R-UX-16). */
@Composable
private fun TestDialog(draft: RoutineDraft, pcName: String?, needsSave: Boolean, onConfirm: () -> Unit, onDismiss: () -> Unit) {
    val context = LocalContext.current
    val destructive = draft.steps.firstOrNull { it.step.isDestructive }?.step
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(if (needsSave) R.string.routines_save_and_test_title else R.string.routines_test_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (needsSave) Text(stringResource(R.string.routines_save_and_test_body))
                Text(stringResource(R.string.routines_test_body))
                if (destructive != null) {
                    Text(RoutineReasonText.render(context, stringResource(R.string.routines_test_simulated_body), RoutineReasonArgs(pc = pcName, action = destructive.verb)))
                }
            }
        },
        confirmButton = {
            Button(onClick = onConfirm) {
                Text(stringResource(if (needsSave) R.string.routines_save_and_test else R.string.routines_test))
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.button_cancel)) } },
    )
}
