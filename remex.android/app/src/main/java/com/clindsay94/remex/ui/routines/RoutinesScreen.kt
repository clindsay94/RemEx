package com.clindsay94.remex.ui.routines

import android.view.HapticFeedbackConstants
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Lightbulb
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material.icons.filled.ViewModule
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FloatingActionButtonMenu
import androidx.compose.material3.FloatingActionButtonMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.IconToggleButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.ToggleFloatingActionButton
import androidx.compose.material3.adaptive.ExperimentalMaterial3AdaptiveApi
import androidx.compose.material3.adaptive.layout.AnimatedPane
import androidx.compose.material3.adaptive.layout.ListDetailPaneScaffoldRole
import androidx.compose.material3.adaptive.layout.PaneAdaptedValue
import androidx.compose.material3.adaptive.navigation.NavigableListDetailPaneScaffold
import androidx.compose.material3.adaptive.navigation.rememberListDetailPaneScaffoldNavigator
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.layout.boundsInWindow
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.positionInWindow
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalResources
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.customActions
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.R
import com.clindsay94.remex.routines.RoutineItem
import com.clindsay94.remex.routines.RoutineReasonText
import com.clindsay94.remex.routines.RoutineStoreHealth
import com.clindsay94.remex.routines.RoutineSyncStates
import com.clindsay94.remex.routines.RoutineSyncView
import com.clindsay94.remex.routines.model.RoutineRunOrigins
import com.clindsay94.remex.routines.model.RoutineReasonArgs
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunOutcomes
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import com.clindsay94.remex.ui.components.RemexFlexibleTopBar
import com.clindsay94.remex.ui.components.RemexTooltip
import com.clindsay94.remex.ui.components.rememberRemexTopBarScrollBehavior
import com.clindsay94.remex.ui.screens.CoachPanel
import com.clindsay94.remex.ui.screens.RemexLinearWavyProgress
import com.clindsay94.remex.ui.screens.RemexLoadingIndicator
import com.clindsay94.remex.ui.theme.LocalReducedMotion
import kotlin.math.roundToInt
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Routines (RemEx 3.0 S1d, RemEx-pp0rt.6): the list, the template gallery, the editor, history and
 * run detail, as the two panes of one list-detail scaffold (routines spec 2.1, 2.4). A phone shows
 * one pane at a time; a foldable or tablet shows the list beside the detail.
 */
@OptIn(ExperimentalMaterial3AdaptiveApi::class)
@Composable
fun RoutinesScreen(
    onNavigateToConnection: () -> Unit,
    viewModel: RoutinesViewModel = viewModel(),
) {
    val navigator = rememberListDetailPaneScaffoldNavigator<RoutineDetail>()
    val scope = rememberCoroutineScope()
    val snackbar = remember { SnackbarHostState() }
    val context = LocalContext.current
    val resources = LocalResources.current

    // Another card, "Start from blank", a template or a run's Edit while the open draft has unsaved
    // edits: ask first (R-UX-58). The editor's BackHandler only covers system back; every in-app
    // route to another editor comes through open().
    var confirmSwitch by remember { mutableStateOf<RoutineDetail?>(null) }

    fun navigate(detail: RoutineDetail) {
        scope.launch { navigator.navigateTo(ListDetailPaneScaffoldRole.Detail, detail) }
    }
    fun open(detail: RoutineDetail) {
        if (viewModel.needsDiscardBefore(detail)) confirmSwitch = detail else navigate(detail)
    }
    fun back() {
        scope.launch { navigator.navigateBack() }
    }

    // A routine notification's "Open" / "See what happened" (RoutineOpenRequests).
    val pendingOpen by RoutineOpenRequests.pending.collectAsStateWithLifecycle()
    LaunchedEffect(pendingOpen) {
        val request = RoutineOpenRequests.consume() ?: return@LaunchedEffect
        when {
            request.runId != null -> open(RoutineDetail.Run(request.runId))
            request.routineId != null -> open(RoutineDetail.History(request.routineId))
        }
    }

    val detailKey = navigator.currentDestination?.contentKey
    val listHidden = navigator.scaffoldValue[ListDetailPaneScaffoldRole.List] == PaneAdaptedValue.Hidden
    val editorFullScreen = detailKey is RoutineDetail.Editor && listHidden
    SideEffect { RoutineEditorChrome.editorShowing = editorFullScreen }
    DisposableEffect(Unit) { onDispose { RoutineEditorChrome.editorShowing = false } }

    // Latest wins (RoutinesMessageChannel): a new message cancels the one on screen via
    // collectLatest, and it is dismissed explicitly first. Long, never Indefinite, even with an
    // action, and always with a dismiss button, so nothing sits on screen waiting for a tap.
    LaunchedEffect(viewModel) {
        viewModel.messages.collectLatest { message ->
            if (message == null) return@collectLatest
            snackbar.currentSnackbarData?.dismiss()
            val actionLabel =
                when (message.action) {
                    is RoutinesMessageAction.TestNow -> resources.getString(R.string.routines_action_test_now)
                    is RoutinesMessageAction.OpenRun -> resources.getString(R.string.routine_notification_see_what_happened)
                    is RoutinesMessageAction.Undo -> resources.getString(R.string.routines_undo)
                    is RoutinesMessageAction.SwitchAndRun -> resources.getString(R.string.routines_switch_and_run)
                    null -> null
                }
            val result =
                snackbar.showSnackbar(
                    message.text,
                    actionLabel = actionLabel,
                    withDismissAction = true,
                    duration = SnackbarDuration.Long,
                )
            if (result == SnackbarResult.ActionPerformed) {
                when (val action = message.action) {
                    is RoutinesMessageAction.TestNow -> viewModel.run(action.routineId, testRun = true)
                    is RoutinesMessageAction.OpenRun -> open(RoutineDetail.Run(action.runId))
                    is RoutinesMessageAction.Undo -> action.restore()
                    is RoutinesMessageAction.SwitchAndRun -> {
                        viewModel.switchAndRun(action.routineId, action.hostIdentity, action.testRun)
                        onNavigateToConnection()
                    }
                    null -> Unit
                }
            }
            // Last: clearing the message re-emits, which would cancel this block.
            viewModel.messageShown(message)
        }
    }

    confirmSwitch?.let { target ->
        DiscardChangesDialog(
            onDiscard = {
                confirmSwitch = null
                viewModel.closeEditor()
                navigate(target)
            },
            onKeep = { confirmSwitch = null },
        )
    }

    Box(Modifier.fillMaxSize()) {
        NavigableListDetailPaneScaffold(
            navigator = navigator,
            listPane = {
                AnimatedPane {
                    RoutinesListPane(
                        viewModel = viewModel,
                        selected = detailKey,
                        onOpen = ::open,
                    )
                }
            },
            detailPane = {
                AnimatedPane {
                    when (val key = detailKey) {
                        RoutineDetail.Templates ->
                            RoutineTemplatesPane(
                                viewModel = viewModel,
                                showBack = listHidden,
                                onBack = ::back,
                                onPick = { template -> open(RoutineDetail.Editor(templateId = template.id)) },
                            )
                        is RoutineDetail.Editor ->
                            RoutineEditorPane(
                                viewModel = viewModel,
                                source = key,
                                onClose = {
                                    back()
                                    viewModel.closeEditor()
                                },
                                onOpenHistory = { id -> open(RoutineDetail.History(id)) },
                                onNavigateToConnection = onNavigateToConnection,
                            )
                        is RoutineDetail.History ->
                            RoutineHistoryPane(
                                viewModel = viewModel,
                                routineId = key.routineId,
                                showBack = listHidden,
                                onBack = ::back,
                                onOpenRun = { runId -> open(RoutineDetail.Run(runId)) },
                            )
                        is RoutineDetail.Run ->
                            RoutineRunPane(
                                viewModel = viewModel,
                                runId = key.runId,
                                showBack = listHidden,
                                onBack = ::back,
                                onNavigateToConnection = onNavigateToConnection,
                                onEdit = { id -> open(RoutineDetail.Editor(routineId = id)) },
                            )
                        null -> DetailPlaceholder()
                    }
                }
            },
        )
        SnackbarHost(
            hostState = snackbar,
            modifier =
                Modifier.align(Alignment.BottomCenter)
                    .navigationBarsPadding()
                    .padding(bottom = if (detailKey is RoutineDetail.Editor) 96.dp else 16.dp),
        )
    }
}

@Composable
private fun DetailPlaceholder() {
    Box(Modifier.fillMaxSize().padding(24.dp), contentAlignment = Alignment.Center) {
        Text(
            stringResource(R.string.routines_detail_placeholder),
            style = MaterialTheme.typography.bodyLarge,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )
    }
}

// ── List pane ────────────────────────────────────────────────────────────────

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class, ExperimentalFoundationApi::class)
@Composable
private fun RoutinesListPane(
    viewModel: RoutinesViewModel,
    selected: RoutineDetail?,
    onOpen: (RoutineDetail) -> Unit,
) {
    val items by viewModel.routines.collectAsStateWithLifecycle()
    val paused by viewModel.pausedAll.collectAsStateWithLifecycle()
    val status by viewModel.status.collectAsStateWithLifecycle()
    val active by viewModel.activeRuns.collectAsStateWithLifecycle()
    val history by viewModel.history.collectAsStateWithLifecycle()
    val pcs by viewModel.pcs.collectAsStateWithLifecycle()
    val coachSeen by viewModel.coachSeen.collectAsStateWithLifecycle()
    val pcActive by viewModel.pcActiveRuns.collectAsStateWithLifecycle()
    val hostSync by viewModel.hostSync.collectAsStateWithLifecycle()
    val messagesBlocked by viewModel.pcMessagesBlocked.collectAsStateWithLifecycle()
    // Back from the settings with notifications allowed: the notice goes, and the PC's next flush shows them.
    androidx.lifecycle.compose.LifecycleResumeEffect(Unit) {
        viewModel.recheckNotifications()
        onPauseOrDispose {}
    }
    val view = LocalView.current
    val scrollBehavior = rememberRemexTopBarScrollBehavior()
    val readOnly = status.readOnly
    val canEdit = status.health == RoutineStoreHealth.OK && !readOnly

    var menuOpen by remember { mutableStateOf(false) }
    var fabOpen by remember { mutableStateOf(false) }
    var coachStep by rememberSaveable { mutableIntStateOf(0) }
    var coachReplay by rememberSaveable { mutableStateOf(false) }
    var pauseAnchor by remember { mutableStateOf(Rect.Zero) }
    var firstCardAnchor by remember { mutableStateOf(Rect.Zero) }
    var paneOrigin by remember { mutableStateOf(Offset.Zero) }
    var confirmRun by remember { mutableStateOf<RoutineItem?>(null) }
    var confirmDelete by remember { mutableStateOf<RoutineItem?>(null) }

    fun pcName(identity: String?): String? = identity?.let { id -> pcs.firstOrNull { it.identity == id }?.name }

    val showCoach = canEdit && items.isNotEmpty() && (coachReplay || coachSeen == false)

    Box(Modifier.fillMaxSize().onGloballyPositioned { paneOrigin = it.positionInWindow() }) {
        Scaffold(
            modifier = Modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
            topBar = {
                RemexFlexibleTopBar(
                    title = stringResource(R.string.screen_routines_title),
                    scrollBehavior = scrollBehavior,
                    actions = {
                        if (canEdit) {
                            val pauseLabel =
                                stringResource(if (paused) R.string.routines_resume_all else R.string.routines_pause_all)
                            RemexTooltip(pauseLabel) {
                                IconToggleButton(
                                    checked = paused,
                                    onCheckedChange = {
                                        view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
                                        viewModel.setPausedAll(it)
                                    },
                                    modifier = Modifier.onGloballyPositioned { pauseAnchor = it.boundsInWindow() },
                                ) {
                                    Icon(if (paused) Icons.Default.PlayArrow else Icons.Default.Pause, contentDescription = pauseLabel)
                                }
                            }
                        }
                        val moreLabel = stringResource(R.string.cd_more_options)
                        RemexTooltip(moreLabel) {
                            IconButton(onClick = { menuOpen = true }) { Icon(Icons.Default.MoreVert, contentDescription = moreLabel) }
                        }
                        DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                            DropdownMenuItem(
                                text = { Text(stringResource(R.string.routines_menu_all_history)) },
                                leadingIcon = { Icon(Icons.Default.History, contentDescription = null) },
                                onClick = {
                                    menuOpen = false
                                    onOpen(RoutineDetail.History(null))
                                },
                            )
                            if (canEdit && items.isNotEmpty()) {
                                DropdownMenuItem(
                                    text = { Text(stringResource(R.string.routines_menu_show_tips)) },
                                    leadingIcon = { Icon(Icons.Default.Lightbulb, contentDescription = null) },
                                    onClick = {
                                        menuOpen = false
                                        coachStep = 0
                                        coachReplay = true
                                    },
                                )
                            }
                        }
                    },
                )
            },
            floatingActionButton = {
                if (canEdit && items.isNotEmpty()) {
                    FloatingActionButtonMenu(
                        expanded = fabOpen,
                        button = {
                            ToggleFloatingActionButton(
                                checked = fabOpen,
                                onCheckedChange = {
                                    view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                    fabOpen = it
                                },
                            ) {
                                Icon(
                                    if (fabOpen) Icons.Default.Close else Icons.Default.Add,
                                    contentDescription = stringResource(R.string.routines_new_routine),
                                )
                            }
                        },
                    ) {
                        FloatingActionButtonMenuItem(
                            onClick = {
                                fabOpen = false
                                onOpen(RoutineDetail.Templates)
                            },
                            icon = { Icon(Icons.Default.ViewModule, contentDescription = null) },
                            text = { Text(stringResource(R.string.routines_from_template)) },
                        )
                        FloatingActionButtonMenuItem(
                            onClick = {
                                fabOpen = false
                                onOpen(RoutineDetail.Editor())
                            },
                            icon = { Icon(Icons.Default.Edit, contentDescription = null) },
                            text = { Text(stringResource(R.string.routines_start_blank)) },
                        )
                    }
                }
            },
        ) { padding ->
            when {
                status.health == RoutineStoreHealth.LOADING -> LoadingState(Modifier.padding(padding))
                status.health == RoutineStoreHealth.UNREADABLE -> UnreadableState(viewModel, Modifier.padding(padding))
                else ->
                    LazyColumn(
                        modifier = Modifier.fillMaxSize(),
                        contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = padding.calculateTopPadding() + 8.dp, bottom = padding.calculateBottomPadding() + 96.dp),
                        verticalArrangement = Arrangement.spacedBy(12.dp),
                    ) {
                        if (readOnly) {
                            item(key = "readonly") {
                                NoticeCard(
                                    title = stringResource(R.string.routines_read_only_title),
                                    body = stringResource(R.string.routines_read_only_body),
                                )
                            }
                        }
                        status.resetAtUnixMs?.let {
                            item(key = "reset") {
                                val context = LocalContext.current
                                NoticeCard(
                                    title = stringResource(R.string.routines_store_reset_title),
                                    body = RoutineReasonText.message(context, RoutineReasonCodes.STORE_RESET, null),
                                    actionLabel = stringResource(R.string.routines_dismiss),
                                    onAction = viewModel::dismissStoreReset,
                                )
                            }
                        }
                        if (messagesBlocked) {
                            item(key = "messages-blocked") {
                                NoticeCard(
                                    title = null,
                                    body = stringResource(R.string.routines_messages_blocked),
                                    actionLabel = stringResource(R.string.routines_messages_blocked_fix),
                                    onAction = viewModel::openNotificationSettings,
                                )
                            }
                        }
                        item(key = "paused") {
                            AnimatedVisibility(
                                visible = paused,
                                enter = expandVertically(MaterialTheme.motionScheme.defaultSpatialSpec()) + fadeIn(MaterialTheme.motionScheme.defaultEffectsSpec()),
                                exit = shrinkVertically(MaterialTheme.motionScheme.fastSpatialSpec()) + fadeOut(MaterialTheme.motionScheme.fastEffectsSpec()),
                            ) {
                                NoticeCard(
                                    title = null,
                                    body = stringResource(R.string.routines_paused_banner),
                                    actionLabel = if (canEdit) stringResource(R.string.routines_resume) else null,
                                    onAction = { viewModel.setPausedAll(false) },
                                    tonal = true,
                                )
                            }
                        }

                        if (items.isEmpty()) {
                            item(key = "empty") {
                                RoutinesEmptyState(
                                    hasNfc = viewModel.hasNfc,
                                    enabled = canEdit,
                                    onTemplate = { onOpen(RoutineDetail.Editor(templateId = it.id)) },
                                    onBrowse = { onOpen(RoutineDetail.Templates) },
                                    onBlank = { onOpen(RoutineDetail.Editor()) },
                                )
                            }
                        } else {
                            val groups = items.groupBy { RoutineOrder.groupOf(it.routine) }
                            var firstCard = true
                            groups.forEach { (group, groupItems) ->
                                val groupHost = groupItems.first().routine.hostIdentity
                                stickyHeader(key = "header-$group") {
                                    GroupHeader(
                                        if (group == RoutineOrder.PHONE) {
                                            stringResource(R.string.routines_group_phone)
                                        } else {
                                            stringResource(R.string.routines_group_pc, pcLabel(pcName(groupHost)))
                                        },
                                    )
                                }
                                // The PC's own state for this phone's set (§7.4.1 step 5, §8.7).
                                if (group != RoutineOrder.PHONE && groupHost != null) {
                                    item(key = "pc-state-$group") {
                                        PcSyncBanners(
                                            pc = RoutineSyncStates.pc(groupHost, hostSync[groupHost]),
                                            phonePaused = paused,
                                            pcName = pcName(groupHost),
                                        )
                                    }
                                }
                                groupItems.forEachIndexed { index, item ->
                                    val id = item.routine.id.orEmpty()
                                    val isFirst = firstCard
                                    firstCard = false
                                    item(key = "routine-$id") {
                                        val lastRun = history.firstOrNull { it.routineId == id && it.outcome != RoutineRunOutcomes.RUNNING }
                                        RoutineCard(
                                            item = item,
                                            activeRun = active[id],
                                            pcRun = pcActive[id],
                                            syncView = RoutineSyncStates.routine(item.routine, item.routine.hostIdentity?.let { hostSync[it] }),
                                            lastRun = lastRun,
                                            pcName = pcName(item.routine.hostIdentity),
                                            paused = paused,
                                            canEdit = canEdit,
                                            selected = (selected as? RoutineDetail.Editor)?.routineId == id,
                                            canMoveUp = index > 0,
                                            canMoveDown = index < groupItems.lastIndex,
                                            onOpen = { onOpen(RoutineDetail.Editor(routineId = id)) },
                                            onRun = {
                                                if (item.routine.steps.orEmpty().any { it?.isDestructive == true }) confirmRun = item
                                                else viewModel.run(id, testRun = false)
                                            },
                                            onStop = { viewModel.cancel(id) },
                                            onEnabled = { viewModel.setEnabled(id, it) },
                                            onTest = { viewModel.run(id, testRun = true) },
                                            onHistory = { onOpen(RoutineDetail.History(id)) },
                                            onDuplicate = { viewModel.duplicate(id) },
                                            onMove = { delta -> viewModel.move(id, delta) },
                                            onDelete = { confirmDelete = item },
                                            modifier =
                                                Modifier.animateItem(
                                                    fadeInSpec = MaterialTheme.motionScheme.fastEffectsSpec(),
                                                    placementSpec = MaterialTheme.motionScheme.fastSpatialSpec(),
                                                    fadeOutSpec = MaterialTheme.motionScheme.fastEffectsSpec(),
                                                ).then(if (isFirst) Modifier.onGloballyPositioned { firstCardAnchor = it.boundsInWindow() } else Modifier),
                                        )
                                    }
                                }
                            }
                            val recent = history.take(3)
                            if (recent.isNotEmpty()) {
                                item(key = "recent-header") {
                                    Row(
                                        Modifier.fillMaxWidth().padding(top = 8.dp),
                                        verticalAlignment = Alignment.CenterVertically,
                                    ) {
                                        GroupHeader(stringResource(R.string.routines_recent_runs), Modifier.weight(1f))
                                        TextButton(onClick = { onOpen(RoutineDetail.History(null)) }) {
                                            Text(stringResource(R.string.routines_see_all))
                                        }
                                    }
                                }
                                items(recent, key = { "recent-" + it.runId }) { run ->
                                    RunRow(
                                        run = run,
                                        pcName = pcName(run.hostIdentity),
                                        showName = true,
                                        onClick = { run.runId?.let { onOpen(RoutineDetail.Run(it)) } },
                                    )
                                }
                            }
                        }
                    }
            }
        }

        if (showCoach) {
            RoutinesCoachOverlay(
                step = coachStep,
                firstCard = firstCardAnchor.translate(-paneOrigin),
                pauseAction = pauseAnchor.translate(-paneOrigin),
                onAdvance = {
                    if (coachStep >= 1) {
                        coachReplay = false
                        coachStep = 0
                        viewModel.setCoachSeen(true)
                    } else {
                        coachStep++
                    }
                },
                onDismiss = {
                    coachReplay = false
                    coachStep = 0
                    viewModel.setCoachSeen(true)
                },
            )
        }
    }

    confirmRun?.let { item ->
        RunConfirmDialog(
            item = item,
            pcName = pcName(item.routine.hostIdentity),
            onConfirm = {
                confirmRun = null
                viewModel.run(item.routine.id.orEmpty(), testRun = false)
            },
            onDismiss = { confirmRun = null },
        )
    }
    confirmDelete?.let { item ->
        DeleteConfirmDialog(
            name = item.routine.name.orEmpty(),
            onConfirm = {
                confirmDelete = null
                viewModel.delete(item.routine.id.orEmpty())
            },
            onDismiss = { confirmDelete = null },
        )
    }
}

@Composable
internal fun GroupHeader(text: String, modifier: Modifier = Modifier) {
    Surface(color = MaterialTheme.colorScheme.surface, modifier = modifier.fillMaxWidth()) {
        Text(
            text = text,
            style = MaterialTheme.typography.labelLarge,
            color = MaterialTheme.colorScheme.primary,
            modifier = Modifier.padding(vertical = 8.dp).semantics { heading() },
        )
    }
}

@Composable
private fun LoadingState(modifier: Modifier = Modifier) {
    val reduced = LocalReducedMotion.current
    Box(modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        if (reduced) {
            Text(stringResource(R.string.routines_loading), style = MaterialTheme.typography.bodyLarge)
        } else {
            val loading = stringResource(R.string.routines_loading)
            RemexLoadingIndicator(modifier = Modifier.size(48.dp).semantics { contentDescription = loading })
        }
    }
}

/** A tonal notice: read-only store, a store reset, Pause all (spec 1.10, 6.7). */
@Composable
internal fun NoticeCard(
    title: String?,
    body: String,
    modifier: Modifier = Modifier,
    actionLabel: String? = null,
    onAction: () -> Unit = {},
    tonal: Boolean = false,
) {
    val scheme = MaterialTheme.colorScheme
    Card(
        colors =
            CardDefaults.cardColors(
                containerColor = if (tonal) scheme.secondaryContainer else scheme.tertiaryContainer,
                contentColor = if (tonal) scheme.onSecondaryContainer else scheme.onTertiaryContainer,
            ),
        modifier = modifier.fillMaxWidth(),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            if (title != null) Text(title, style = MaterialTheme.typography.titleSmall, modifier = Modifier.semantics { heading() })
            Text(body, style = MaterialTheme.typography.bodyMedium)
            if (actionLabel != null) {
                TextButton(onClick = onAction, modifier = Modifier.align(Alignment.End)) { Text(actionLabel) }
            }
        }
    }
}

/**
 * The store could not be read (spec 6.7): nothing is written over it until the user saves a copy
 * or resets. RoutineRepository keeps the raw text for "Save a copy".
 */
@Composable
private fun UnreadableState(viewModel: RoutinesViewModel, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val resources = LocalResources.current
    val scope = rememberCoroutineScope()
    var confirmReset by remember { mutableStateOf(false) }
    val saver =
        rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/json")) { uri ->
            if (uri == null) return@rememberLauncherForActivityResult
            scope.launch {
                val text = viewModel.unreadableText()
                val written =
                    text != null &&
                        withContext(Dispatchers.IO) {
                            runCatching {
                                context.contentResolver.openOutputStream(uri)?.use { it.write(text.toByteArray(Charsets.UTF_8)) } != null
                            }.getOrDefault(false)
                        }
                viewModel.post(resources.getString(if (written) R.string.routines_unreadable_saved else R.string.routines_unreadable_save_failed))
            }
        }
    Column(
        modifier.fillMaxSize().padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Icon(Icons.Default.ErrorOutline, contentDescription = null, tint = MaterialTheme.colorScheme.error, modifier = Modifier.size(40.dp))
        Text(stringResource(R.string.routines_unreadable_title), style = MaterialTheme.typography.headlineSmall, modifier = Modifier.semantics { heading() })
        Text(stringResource(R.string.routines_unreadable_body), style = MaterialTheme.typography.bodyMedium)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilledTonalButton(onClick = { saver.launch("remex-routines.json") }) { Text(stringResource(R.string.routines_unreadable_save)) }
            OutlinedButton(
                onClick = { confirmReset = true },
                colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error),
            ) { Text(stringResource(R.string.routines_unreadable_reset)) }
        }
    }
    if (confirmReset) {
        AlertDialog(
            onDismissRequest = { confirmReset = false },
            title = { Text(stringResource(R.string.routines_unreadable_reset_title)) },
            text = { Text(stringResource(R.string.routines_unreadable_reset_body)) },
            confirmButton = {
                Button(
                    onClick = {
                        confirmReset = false
                        viewModel.resetUnreadable()
                    },
                    colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error, contentColor = MaterialTheme.colorScheme.onError),
                ) { Text(stringResource(R.string.routines_unreadable_reset)) }
            },
            dismissButton = { TextButton(onClick = { confirmReset = false }) { Text(stringResource(R.string.button_cancel)) } },
        )
    }
}

// ── Routine card ─────────────────────────────────────────────────────────────

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun RoutineCard(
    item: RoutineItem,
    activeRun: RoutineRun?,
    pcRun: RoutineRun?,
    syncView: RoutineSyncView?,
    lastRun: RoutineRun?,
    pcName: String?,
    paused: Boolean,
    canEdit: Boolean,
    selected: Boolean,
    canMoveUp: Boolean,
    canMoveDown: Boolean,
    onOpen: () -> Unit,
    onRun: () -> Unit,
    onStop: () -> Unit,
    onEnabled: (Boolean) -> Unit,
    onTest: () -> Unit,
    onHistory: () -> Unit,
    onDuplicate: () -> Unit,
    onMove: (Int) -> Unit,
    onDelete: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val routine = item.routine
    val steps = routine.steps.orEmpty()
    val manual = routine.trigger?.type == RoutineTriggerTypes.MANUAL
    val runsOnPc = RoutineTriggerTypes.isHostRun(routine.trigger?.type)
    // A phone run of this routine, or the PC's run of it (live run reports, RemEx-pp0rt.12).
    val shownRun = activeRun ?: pcRun
    val running = shownRun != null
    val reduced = LocalReducedMotion.current
    val context = LocalContext.current
    val locale = appLocale()
    var menuOpen by remember { mutableStateOf(false) }
    val alpha by animateFloatAsState(if (paused) 0.6f else 1f, MaterialTheme.motionScheme.defaultEffectsSpec(), label = "pausedAlpha")

    val name = routine.name.orEmpty()
    val onLabel = stringResource(R.string.routines_state_on)
    val offLabel = stringResource(R.string.routines_state_off)
    val resultText =
        when {
            running -> stringResource(R.string.routines_outcome_running)
            lastRun != null -> runOutcomeText(lastRun, pcName)
            else -> stringResource(R.string.routines_never_run)
        }
    val description =
        pluralStringResource(R.plurals.routines_card_description, steps.size, name, triggerChipLabel(routine.trigger?.type), steps.size, resultText)
    val moveUp = stringResource(R.string.routines_move_up)
    val moveDown = stringResource(R.string.routines_move_down)

    Card(
        onClick = onOpen,
        colors =
            CardDefaults.cardColors(
                containerColor = if (selected) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surfaceContainerLow,
            ),
        modifier =
            modifier.fillMaxWidth()
                .graphicsLayer { this.alpha = alpha }
                .semantics {
                    contentDescription = description
                    stateDescription = if (routine.enabled) onLabel else offLabel
                    customActions =
                        buildList {
                            if (canEdit && canMoveUp) add(CustomAccessibilityAction(moveUp) { onMove(-1); true })
                            if (canEdit && canMoveDown) add(CustomAccessibilityAction(moveDown) { onMove(1); true })
                        }
                },
    ) {
        Column(Modifier.padding(start = 16.dp, top = 12.dp, bottom = 12.dp, end = 4.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(triggerIcon(routine.trigger?.type), contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                Spacer(Modifier.size(12.dp))
                Text(
                    name,
                    style = MaterialTheme.typography.titleMedium,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
                if (manual) {
                    if (running) {
                        FilledTonalButton(onClick = onStop, modifier = Modifier.heightIn(min = 48.dp)) {
                            Icon(Icons.Default.Stop, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.size(6.dp))
                            Text(stringResource(R.string.routines_stop))
                        }
                    } else {
                        FilledTonalButton(onClick = onRun, enabled = item.verdict.isValid, modifier = Modifier.heightIn(min = 48.dp)) {
                            Icon(Icons.Default.PlayArrow, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.size(6.dp))
                            Text(stringResource(R.string.routines_run))
                        }
                    }
                } else {
                    androidx.compose.material3.Switch(
                        checked = routine.enabled,
                        onCheckedChange = onEnabled,
                        enabled = canEdit,
                        modifier = Modifier.semantics { contentDescription = name },
                    )
                }
                Box {
                    val moreLabel = stringResource(R.string.routines_routine_menu, name)
                    IconButton(onClick = { menuOpen = true }) { Icon(Icons.Default.MoreVert, contentDescription = moreLabel) }
                    DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                        // A PC routine has a switch where a manual one has Run, so Run now and
                        // Stop live here (spec 1.6 "List overflow Run now").
                        if (runsOnPc && item.verdict.isValid && !running) {
                            DropdownMenuItem(text = { Text(stringResource(R.string.routines_run_now)) }, onClick = { menuOpen = false; onRun() })
                        }
                        if (runsOnPc && running) {
                            DropdownMenuItem(text = { Text(stringResource(R.string.routines_stop)) }, onClick = { menuOpen = false; onStop() })
                        }
                        if (item.verdict.isValid && !running) {
                            DropdownMenuItem(text = { Text(stringResource(R.string.routines_test)) }, onClick = { menuOpen = false; onTest() })
                        }
                        DropdownMenuItem(text = { Text(stringResource(R.string.routines_history)) }, onClick = { menuOpen = false; onHistory() })
                        if (canEdit) {
                            DropdownMenuItem(text = { Text(stringResource(R.string.routines_duplicate)) }, onClick = { menuOpen = false; onDuplicate() })
                            if (canMoveUp) DropdownMenuItem(text = { Text(moveUp) }, onClick = { menuOpen = false; onMove(-1) })
                            if (canMoveDown) DropdownMenuItem(text = { Text(moveDown) }, onClick = { menuOpen = false; onMove(1) })
                            DropdownMenuItem(
                                text = { Text(stringResource(R.string.routines_delete), color = MaterialTheme.colorScheme.error) },
                                onClick = { menuOpen = false; onDelete() },
                            )
                        }
                    }
                }
            }
            RoutineChipChain(
                triggerType = routine.trigger?.type,
                steps = steps,
                highlightIndex = shownRun?.let { RoutineRunViews.currentStep(it) },
                modifier = Modifier.padding(end = 12.dp),
            )
            if (runsOnPc) {
                // "Runs on PC" chip and the routine's sync state (spec 1.2, 2.2; §7.4.1 step 5).
                RoutinePcStatusRow(syncView = syncView, pcName = pcName, modifier = Modifier.padding(end = 12.dp))
            }
            if (shownRun != null) {
                // M8: the wave flattens under reduced motion (spec 3.3); progress is step-granular.
                RemexLinearWavyProgress(
                    progress = RoutineRunViews.progress(shownRun),
                    modifier = Modifier.fillMaxWidth().padding(end = 12.dp),
                    amplitude = if (reduced) { _ -> 0f } else androidx.compose.material3.WavyProgressIndicatorDefaults.indicatorAmplitude,
                )
                val current = RoutineRunViews.currentStep(shownRun)
                Text(
                    stringResource(R.string.routine_progress_step_count, (current ?: 0) + 1, RoutineRunViews.totalSteps(shownRun)),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            } else if (!item.verdict.isValid) {
                OutcomeLine(
                    look = OutcomeLook(Icons.Default.Warning, MaterialTheme.colorScheme.error),
                    text = RoutineReasonText.message(context, item.verdict.reasonCode, RoutineReasonArgs(pc = pcName, detail = item.verdict.detail)),
                )
            } else if (lastRun != null) {
                OutcomeLine(
                    look = runOutcomeLook(lastRun.outcome, lastRun.testRun),
                    text = stringResource(R.string.routines_last_result, resultText, RoutineTimeText.time(lastRun.triggeredAtUnixMs, locale = locale)),
                )
            }
        }
    }
}

/** One run in a list: outcome icon + time + name + outcome text (spec A8, R-UX-27, R-UX-50). */
@Composable
internal fun RunRow(run: RoutineRun, pcName: String?, showName: Boolean, onClick: () -> Unit, modifier: Modifier = Modifier) {
    val locale = appLocale()
    val look = runOutcomeLook(run.outcome, run.testRun)
    val outcome = runOutcomeText(run, pcName)
    Surface(onClick = onClick, color = MaterialTheme.colorScheme.surface, shape = MaterialTheme.shapes.medium, modifier = modifier.fillMaxWidth().heightIn(min = 56.dp)) {
        Row(Modifier.padding(horizontal = 8.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(look.icon, contentDescription = null, tint = look.tint)
            Spacer(Modifier.size(12.dp))
            Column(Modifier.weight(1f)) {
                if (showName) {
                    Text(run.routineName.orEmpty(), style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                Text(
                    if (run.testRun) stringResource(R.string.routines_test_run_outcome, outcome) else outcome,
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            // PC runs merged into the phone's history carry the "on PC" badge (§8.8).
            if (run.origin == RoutineRunOrigins.PC) {
                OnPcBadge(Modifier.padding(horizontal = 8.dp))
            }
            Text(RoutineTimeText.time(run.triggeredAtUnixMs, locale = locale), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
internal fun RunConfirmDialog(item: RoutineItem, pcName: String?, onConfirm: () -> Unit, onDismiss: () -> Unit) {
    val context = LocalContext.current
    val destructive = item.routine.steps.orEmpty().firstOrNull { it?.isDestructive == true }
    val body = RoutineReasonText.render(context, stringResource(R.string.routines_run_confirm_body), RoutineReasonArgs(pc = pcName, action = destructive?.verb))
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.routines_run_confirm_title, item.routine.name.orEmpty())) },
        text = { Text(body) },
        confirmButton = {
            Button(
                onClick = onConfirm,
                colors =
                    if (RoutineStepText.discardsWork(destructive)) {
                        ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error, contentColor = MaterialTheme.colorScheme.onError)
                    } else {
                        ButtonDefaults.buttonColors()
                    },
            ) { Text(stringResource(R.string.routines_run)) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.button_cancel)) } },
    )
}

/** "Discard changes?" (spec 1.8, R-UX-58): Discard is the danger action, Keep editing stays put. */
@Composable
internal fun DiscardChangesDialog(onDiscard: () -> Unit, onKeep: () -> Unit) {
    AlertDialog(
        onDismissRequest = onKeep,
        title = { Text(stringResource(R.string.routines_discard_title)) },
        confirmButton = {
            Button(
                onClick = onDiscard,
                colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error, contentColor = MaterialTheme.colorScheme.onError),
            ) { Text(stringResource(R.string.routines_discard)) }
        },
        dismissButton = { TextButton(onClick = onKeep) { Text(stringResource(R.string.routines_keep_editing)) } },
    )
}

@Composable
internal fun DeleteConfirmDialog(name: String, onConfirm: () -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.routines_delete_title, name)) },
        text = { Text(stringResource(R.string.routines_delete_body)) },
        confirmButton = {
            Button(
                onClick = onConfirm,
                colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error, contentColor = MaterialTheme.colorScheme.onError),
            ) { Text(stringResource(R.string.routines_delete)) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.button_cancel)) } },
    )
}

// ── Empty state ──────────────────────────────────────────────────────────────

@Composable
private fun RoutinesEmptyState(
    hasNfc: Boolean,
    enabled: Boolean,
    onTemplate: (RoutineTemplate) -> Unit,
    onBrowse: () -> Unit,
    onBlank: () -> Unit,
) {
    val featured = remember(hasNfc) { RoutineTemplates.featured(hasNfc) }
    val reduced = LocalReducedMotion.current
    Column(
        Modifier.fillMaxWidth().padding(top = 8.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        RoutineIllustration(Modifier.fillMaxWidth().height(72.dp))
        Text(
            stringResource(R.string.routines_empty_title),
            style = MaterialTheme.typography.headlineSmall,
            textAlign = TextAlign.Center,
            modifier = Modifier.semantics { heading() },
        )
        Text(
            stringResource(R.string.routines_empty_body),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )
        featured.forEachIndexed { i, template ->
            // M1: cards rise 16dp and fade in with a 50 ms stagger; all present at once when reduced.
            val rise = remember { Animatable(if (reduced) 0f else 1f) }
            val spatial = MaterialTheme.motionScheme.defaultSpatialSpec<Float>()
            LaunchedEffect(reduced) {
                if (reduced) {
                    rise.snapTo(0f)
                } else {
                    delay(50L * i)
                    rise.animateTo(0f, spatial)
                }
            }
            val density = LocalDensity.current
            TemplateCard(
                template = template,
                compact = true,
                enabled = enabled,
                onClick = { onTemplate(template) },
                modifier =
                    Modifier.graphicsLayer {
                        translationY = with(density) { 16.dp.toPx() } * rise.value
                        alpha = 1f - rise.value
                    },
            )
        }
        FilledTonalButton(onClick = onBrowse, enabled = enabled) { Text(stringResource(R.string.routines_browse_templates)) }
        TextButton(onClick = onBlank, enabled = enabled) { Text(stringResource(R.string.routines_start_blank)) }
    }
}

/** A trigger node joined to three step nodes (spec A2). The connector draws in once (M1). */
@Composable
private fun RoutineIllustration(modifier: Modifier = Modifier) {
    val reduced = LocalReducedMotion.current
    val primary = MaterialTheme.colorScheme.primary
    val secondary = MaterialTheme.colorScheme.secondaryContainer
    val line = MaterialTheme.colorScheme.outlineVariant
    val draw = remember { Animatable(if (reduced) 1f else 0f) }
    val effects = MaterialTheme.motionScheme.slowEffectsSpec<Float>()
    LaunchedEffect(reduced) { if (reduced) draw.snapTo(1f) else draw.animateTo(1f, effects) }
    Canvas(modifier) {
        val cy = size.height / 2
        val startX = size.width * 0.2f
        val endX = size.width * 0.8f
        val stroke = 3.dp.toPx()
        drawLine(line, Offset(startX, cy), Offset(startX + (endX - startX) * draw.value, cy), strokeWidth = stroke)
        drawCircle(primary, radius = 14.dp.toPx(), center = Offset(startX, cy))
        for (i in 1..3) {
            val x = startX + (endX - startX) * i / 3f
            if (draw.value >= i / 3f - 0.01f) drawCircle(secondary, radius = 9.dp.toPx(), center = Offset(x, cy))
        }
    }
}

// ── Coach marks (spec 5.3) ───────────────────────────────────────────────────

@Composable
private fun RoutinesCoachOverlay(
    step: Int,
    firstCard: Rect,
    pauseAction: Rect,
    onAdvance: () -> Unit,
    onDismiss: () -> Unit,
) {
    Box(Modifier.fillMaxSize()) {
        // A non-clickable Surface swallows touches, so the list cannot be used mid-hint and a stray
        // tap on the scrim does not lose the tips (the Dashboard coach's rule).
        Surface(color = MaterialTheme.colorScheme.scrim.copy(alpha = 0.62f), modifier = Modifier.fillMaxSize()) {}
        val target = if (step == 0) firstCard else pauseAction
        if (target != Rect.Zero) {
            val density = LocalDensity.current
            val pad = with(density) { 6.dp.toPx() }
            Box(
                Modifier.offset { IntOffset((target.left - pad).roundToInt(), (target.top - pad).roundToInt()) }
                    .size(with(density) { (target.width + pad * 2).toDp() }, with(density) { (target.height + pad * 2).toDp() })
                    .border(3.dp, MaterialTheme.colorScheme.primary, RoundedCornerShape(16.dp)),
            )
        }
        CoachPanel(
            body = stringResource(if (step == 0) R.string.routines_coach_card else R.string.routines_coach_pause),
            isLast = step >= 1,
            onAdvance = onAdvance,
            onDismiss = onDismiss,
            modifier =
                Modifier.align(Alignment.BottomCenter)
                    .navigationBarsPadding()
                    .widthIn(max = 560.dp)
                    .padding(horizontal = 20.dp, vertical = 28.dp),
        )
    }
}
