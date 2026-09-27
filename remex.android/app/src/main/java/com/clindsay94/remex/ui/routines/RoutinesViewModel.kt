package com.clindsay94.remex.ui.routines

import android.app.Application
import android.content.pm.PackageManager
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.KnownHosts
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.routines.RoutineHostSync
import com.clindsay94.remex.routines.RoutineItem
import com.clindsay94.remex.routines.RoutineNfcBinding
import com.clindsay94.remex.routines.RoutinePcRunStart
import com.clindsay94.remex.routines.home.Home
import com.clindsay94.remex.routines.home.HomeCaptureProbe
import com.clindsay94.remex.routines.home.HomeFacts
import com.clindsay94.remex.routines.home.HomePresence
import com.clindsay94.remex.routines.manual.RoutineManualEntry
import com.clindsay94.remex.routines.manual.RoutineShortcuts
import com.clindsay94.remex.routines.widget.RoutineWidget
import com.clindsay94.remex.routines.RoutinePcSyncView
import com.clindsay94.remex.routines.RoutineReasonText
import com.clindsay94.remex.routines.RoutineRepository
import com.clindsay94.remex.routines.RoutineRunStart
import com.clindsay94.remex.routines.RoutineSaveResult
import com.clindsay94.remex.routines.RoutineStoreStatus
import com.clindsay94.remex.routines.RoutineSyncStates
import com.clindsay94.remex.routines.RoutineSyncView
import com.clindsay94.remex.routines.Routines
import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineLimits
import com.clindsay94.remex.routines.model.RoutineReasonArgs
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunSources
import com.clindsay94.remex.routines.model.RoutineStepTypes
import com.clindsay94.remex.routines.model.RoutineTrigger
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import com.clindsay94.remex.security.HostIdentity
import com.clindsay94.remex.security.PinnedHostStore
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChangedBy
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.mapLatest
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray

/** A paired PC as the routines UI names it. [name] is the user's nickname, or null (shown as "your PC"). */
data class RoutinePc(val identity: String, val name: String?)

/** An app from the connected PC's launcher list, with the stable id a `launchApp` step stores. */
data class RoutineAppChoice(val id: String, val name: String)

/**
 * The Routines screen's state holder (RemEx-pp0rt.6). Everything routine-shaped comes from the S1c
 * [RoutineRepository]; this adds what the UI needs around it: the paired PCs, the selected PC's MAC,
 * the connected PC's launcher list, the coach and badge flags, and the one open editor draft.
 *
 * Its flows are shared `Eagerly`: the ViewModel lives exactly as long as the Routines destination,
 * and [openEditor] reads the selected PC and MAC synchronously the moment a template opens, which a
 * `WhileSubscribed` flow with no collector yet would answer with its initial null.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class RoutinesViewModel(application: Application) : AndroidViewModel(application) {
    private val app = application
    private val repository: RoutineRepository = Routines.repository(application)
    private val settings = SettingsManager(application)

    val routines: StateFlow<List<RoutineItem>> = repository.routines
    val pausedAll: StateFlow<Boolean> = repository.pausedAll
    val status: StateFlow<RoutineStoreStatus> = repository.status
    val activeRuns: StateFlow<Map<String, RoutineRun>> = repository.activeRuns
    val history: StateFlow<List<RoutineRun>> = repository.history

    /** PC runs in progress, from live run reports (RemEx-pp0rt.12). */
    val pcActiveRuns: StateFlow<Map<String, RoutineRun>> = repository.pcActiveRuns

    /** Per-PC sync bookkeeping; read through [pcSyncView] and [syncView]. */
    val hostSync: StateFlow<Map<String, RoutineHostSync>> = repository.hostSync

    private val syncClient = Routines.syncClient(application)

    fun pcSyncView(identity: String): RoutinePcSyncView = RoutineSyncStates.pc(identity, hostSync.value[identity])

    fun syncView(routine: Routine): RoutineSyncView? = RoutineSyncStates.routine(routine, routine.hostIdentity?.let { hostSync.value[it] })

    private val messageChannel = RoutinesMessageChannel()

    /** The snackbar message to show now (latest wins, see [RoutinesMessageChannel]). */
    val messages: StateFlow<RoutinesMessage?> = messageChannel.current

    fun messageShown(message: RoutinesMessage) = messageChannel.consumed(message)

    val hasNfc: Boolean = application.packageManager.hasSystemFeature(PackageManager.FEATURE_NFC)

    private val paired = MutableStateFlow<Map<String, String>>(emptyMap())

    /** Paired PCs, most recently connected first. */
    val pcs: StateFlow<List<RoutinePc>> =
        combine(paired, settings.knownHostRecordsFlow) { byAddress, records ->
            KnownHosts.build(byAddress, records).map { RoutinePc(it.identity, it.nickname.takeIf { n -> n.isNotBlank() }) }
        }.stateIn(viewModelScope, SharingStarted.Eagerly, emptyList())

    /** The PC RemEx is set to (Connection), which is the default target for a new routine. */
    val selectedPc: StateFlow<String?> =
        settings.hostFlow
            .mapLatest { host -> identityOf(host) }
            .stateIn(viewModelScope, SharingStarted.Eagerly, null)

    /** The selected PC's saved MAC in the `AA:BB:CC:DD:EE:FF` form a `wake` step stores, or null. */
    val selectedMac: StateFlow<String?> =
        settings.macAddressFlow
            .map(::normalizeMac)
            .stateIn(viewModelScope, SharingStarted.Eagerly, null)

    /** The PC this phone is authenticated to right now, or null. */
    val connectedPc: StateFlow<String?> =
        RemexClientManager.authenticatedConnection
            .mapLatest { connection -> connection?.host?.let { identityOf(it) } }
            .stateIn(viewModelScope, SharingStarted.Eagerly, null)

    private val launcherApps: StateFlow<List<RoutineAppChoice>?> =
        RemexClientManager.launcherEntries
            .map(::parseLauncher)
            .stateIn(viewModelScope, SharingStarted.Eagerly, null)

    /** The connected PC and its launcher list; the list is null until it has arrived. */
    val launcher: StateFlow<Pair<String?, List<RoutineAppChoice>?>> =
        combine(connectedPc, launcherApps) { pc, apps -> pc to (if (pc == null) null else apps) }
            .stateIn(viewModelScope, SharingStarted.Eagerly, null to null)

    /**
     * The connected PC and its sensor catalog (routines S5), from its telemetry stream; the list is
     * null until a frame arrives. Parsed off the main thread, and only when the set of sensors could
     * have changed: a 1 Hz frame whose ids are unchanged keeps the previous list.
     */
    val sensors: StateFlow<Pair<String?, List<RoutineSensorOption>?>> =
        combine(connectedPc, RemexClientManager.telemetry.mapLatest { json -> withContext(Dispatchers.Default) { RoutineSensorCatalog.parse(json) } }) { pc, list ->
            pc to (if (pc == null) null else list)
        }.distinctUntilChangedBy { (pc, list) -> pc to list?.map { it.id } }
            .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), null to null)

    /**
     * A health template opened before the PC's catalog arrived has no sensor; once the catalog of the
     * draft's PC is known it gets the template's sensor (by kind, then name), in the original too, so
     * the fill is not an unsaved change. The person can still pick another.
     */
    fun adoptTemplateSensor() {
        val current = _draft.value ?: return
        if (!current.isNew) return
        val preset = RoutineTemplates.byId(current.templateId)?.sensorPreset ?: return
        val trigger = current.trigger?.takeIf { it.type == RoutineTriggerTypes.PC_SENSOR && it.sensorId == null } ?: return
        val (pc, list) = sensors.value
        if (pc == null || pc != current.hostIdentity) return
        val option = RoutineSensorCatalog.preselect(list, preset) ?: return

        fun fill(d: RoutineDraft): RoutineDraft =
            if (d.trigger?.sensorId == null && d.trigger?.type == RoutineTriggerTypes.PC_SENSOR) d.copy(trigger = RoutineSensorCatalog.choose(trigger, option)) else d
        _draft.value = fill(current)
        _draftOriginal.value = _draftOriginal.value?.let(::fill)
    }

    /**
     * The connected PC and whether it accepts key presses (`supportsInputSimulation`), for the media
     * step's warning (spec 4.2 "Media keys"). Read from `hostInfoForConnection` checked against the
     * authenticated epoch, never the replaying `hostCapabilities`, which can still hold the previous
     * PC's answer. An absent key means an older PC that accepts them, as on Remote Control.
     */
    val mediaKeys: StateFlow<Pair<String?, Boolean?>> =
        combine(connectedPc, RemexClientManager.authenticatedConnection, RemexClientManager.hostInfoForConnection) { pc, connection, info ->
            pc to RoutineMediaKeys.supported(info?.json, sameConnection = info != null && connection != null && info.connection.epoch == connection.epoch)
        }.stateIn(viewModelScope, SharingStarted.Eagerly, null to null)

    /**
     * The connected PC and whether it advertises `supportsRoutines` (§7.5, §7.6), from THIS
     * connection's `host_info` only. Null while unknown; the editor disables the PC triggers only on
     * a definite false ("Update RemEx on <PC>").
     */
    val routinesSupport: StateFlow<Pair<String?, Boolean?>> =
        combine(connectedPc, RemexClientManager.authenticatedConnection, RemexClientManager.hostInfoForConnection) { pc, connection, info ->
            val json = info?.takeIf { connection != null && it.connection.epoch == connection.epoch }?.json
            pc to json?.let { runCatching { org.json.JSONObject(it).optBoolean("supportsRoutines", false) }.getOrNull() }
        }.stateIn(viewModelScope, SharingStarted.Eagerly, null to null)

    /**
     * The connected PC and the power verbs it advertises as `routinePowerVerbs` (§7.5), from THIS
     * connection's `host_info` only. Null while unknown or when an older PC does not say.
     */
    val powerVerbs: StateFlow<Pair<String?, List<String>?>> =
        combine(connectedPc, RemexClientManager.authenticatedConnection, RemexClientManager.hostInfoForConnection) { pc, connection, info ->
            val json = info?.takeIf { connection != null && it.connection.epoch == connection.epoch }?.json
            pc to json?.let { RoutinePowerVerbsCapability.parse(it) }
        }.stateIn(viewModelScope, SharingStarted.Eagerly, null to null)

    val isConnected: StateFlow<Boolean> = RemexClientManager.isConnected

    /**
     * A PC message could not be shown because notifications are off (routines S5). The PC keeps it
     * (unacknowledged) for up to an hour; the list says so and opens the settings.
     */
    val pcMessagesBlocked: StateFlow<Boolean> = syncClient.pcMessagesBlocked

    /** Called when the screen resumes: notifications allowed again clears the notice. */
    fun recheckNotifications() {
        val permitted =
            androidx.core.content.ContextCompat.checkSelfPermission(app, android.Manifest.permission.POST_NOTIFICATIONS) ==
                PackageManager.PERMISSION_GRANTED
        if (permitted && androidx.core.app.NotificationManagerCompat.from(app).areNotificationsEnabled()) syncClient.clearPcMessagesBlocked()
    }

    /** RemEx's notification settings, where notifications can be allowed. */
    fun openNotificationSettings() {
        val intent =
            android.content.Intent(android.provider.Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                .putExtra(android.provider.Settings.EXTRA_APP_PACKAGE, app.packageName)
                .addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
        runCatching { app.startActivity(intent) }.onFailure { Log.w(TAG, "Opening notification settings failed", it) }
    }

    val coachSeen: StateFlow<Boolean?> =
        settings.routinesCoachSeenFlow.stateIn(viewModelScope, SharingStarted.Eagerly, null)

    // ── The one open editor ──

    private val _draft = MutableStateFlow<RoutineDraft?>(null)
    val draft: StateFlow<RoutineDraft?> = _draft.asStateFlow()
    private val _draftOriginal = MutableStateFlow<RoutineDraft?>(null)
    val draftOriginal: StateFlow<RoutineDraft?> = _draftOriginal.asStateFlow()
    private var draftSource: RoutineDetail.Editor? = null

    init {
        viewModelScope.launch { repository.load() }
        // A "Switch and run" that did not start says why here, on the screen's own message path,
        // usually when the person comes back from Connection (RemEx-pp0rt.12 review).
        viewModelScope.launch {
            syncClient.switchRunOutcomes.collect { outcome ->
                if (outcome == null) return@collect
                val routine = routine(outcome.routineId) ?: repository.routine(outcome.routineId)
                val args = RoutineReasonArgs(pc = routine?.hostIdentity?.let(::pcName), routine = routine?.name)
                postPcRunStart(outcome.routineId, outcome.testRun, outcome.start, args)
                syncClient.consumeSwitchRunOutcome(outcome)
            }
        }
        refreshPaired()
        viewModelScope.launch { settings.markRoutinesOpened() }
    }

    fun refreshPaired() {
        viewModelScope.launch {
            paired.value = runCatchingIo("Reading paired PCs", emptyMap()) { PinnedHostStore.listPaired(app) }
        }
    }

    /** Whether the open draft is already [source]'s (same key, or the routine it was saved as). */
    private fun isCurrentSource(source: RoutineDetail.Editor): Boolean =
        _draft.value != null && (source == draftSource || (source.routineId != null && source.routineId == _draft.value?.base?.id))

    /**
     * True when opening [target] would throw away unsaved edits (R-UX-58): the screen then asks
     * "Discard changes?" before it navigates, and only [closeEditor] on Discard lets the switch happen.
     */
    fun needsDiscardBefore(target: RoutineDetail): Boolean =
        target is RoutineDetail.Editor &&
            RoutineEditorSwitch.needsDiscardPrompt(_draft.value, _draftOriginal.value, sameSource = isCurrentSource(target))

    /**
     * Starts (or keeps) the draft for [source]. Returns false while the routine is not loaded yet,
     * and refuses to replace a draft with unsaved edits: the screen asks first ([needsDiscardBefore]).
     */
    fun openEditor(source: RoutineDetail.Editor): Boolean {
        if (isCurrentSource(source)) {
            draftSource = source
            return true
        }
        if (RoutineEditorSwitch.needsDiscardPrompt(_draft.value, _draftOriginal.value, sameSource = false)) return false
        val initial =
            when {
                source.routineId != null -> {
                    val routine = routines.value.firstOrNull { it.routine.id == source.routineId }?.routine ?: return false
                    RoutineDrafts.fromRoutine(routine)
                }
                source.templateId != null -> {
                    val template = RoutineTemplates.byId(source.templateId) ?: return false
                    val hostIdentity = selectedPc.value ?: pcs.value.firstOrNull()?.identity
                    RoutineDrafts.fromTemplate(
                        template = template,
                        name = app.getString(template.nameRes),
                        hostIdentity = hostIdentity,
                        mac = selectedMac.value,
                        notifyBody = { res -> templateMessage(res, template.trigger, template.id) },
                        homeId = home.value?.id,
                    ).let { draft -> if (template.id == STEAM_TEMPLATE) preselectSteam(draft, hostIdentity) else draft }
                }
                else -> RoutineDrafts.blank(selectedPc.value ?: pcs.value.firstOrNull()?.identity)
            }
        draftSource = source
        draftSession++
        _draft.value = initial
        _draftOriginal.value = initial
        return true
    }

    /** Which draft is open: changes whenever a different draft opens or the editor closes. */
    var draftSession: Long = 0
        private set

    fun updateDraft(transform: (RoutineDraft) -> RoutineDraft) {
        _draft.value =
            _draft.value?.let { before ->
                // A template message that says "over 90% for 2 minutes" follows the limit and hold
                // time until the user edits it (RoutineTemplateMessages).
                RoutineTemplateMessages.rederive(before, transform(before)) { res, trigger -> templateMessage(res, trigger, before.templateId) }
            }
    }

    /** A template notify body, formatted with [trigger]'s limit (in the template sensor's unit) and hold time. */
    private fun templateMessage(res: Int, trigger: RoutineTrigger?, templateId: String?): String {
        val unit = templateId?.let { RoutineTemplates.byId(it) }?.sensorPreset?.unitHints?.firstOrNull()
        val seconds = (trigger?.sustainSeconds ?: RoutineLimits.DEFAULT_SUSTAIN_SECONDS).toString()
        return app.getString(res, RoutineTemplateMessages.limitText(trigger, unit), RoutineReasonText.formatDuration(app, seconds))
    }

    /**
     * [transform] applies only if the draft from [session] is still the open one. A step's Undo is
     * bound this way, so tapping it after switching routines never edits the other routine.
     */
    fun updateDraftIn(session: Long, transform: (RoutineDraft) -> RoutineDraft) {
        if (session == draftSession) updateDraft(transform)
    }

    /**
     * A new draft opened before the paired PCs and the selected PC had loaded has no target. Once
     * they arrive it adopts the default PC (and its MAC for `wake` steps), in the original too, so the
     * fill is not an unsaved change and a single-PC user is never stuck without a PC to pick.
     */
    fun adoptDefaultPc() {
        val current = _draft.value ?: return
        if (!current.isNew) return
        val identity = current.hostIdentity ?: selectedPc.value ?: pcs.value.firstOrNull()?.identity ?: return
        val mac = macFor(identity)
        val needsMac = mac != null && current.steps.any { it.step.type == RoutineStepTypes.WAKE && it.step.mac == null }
        if (current.hostIdentity == identity && !needsMac) return

        fun fill(d: RoutineDraft): RoutineDraft {
            val targeted = d.copy(hostIdentity = identity)
            return if (needsMac) targeted.withWakeMac(mac) else targeted
        }
        _draft.value = fill(current)
        _draftOriginal.value = _draftOriginal.value?.let(::fill)
    }

    fun closeEditor() {
        draftSource = null
        draftSession++
        _draft.value = null
        _draftOriginal.value = null
    }

    /** The MAC a new `wake` step for [identity] gets: known only for the selected PC. */
    fun macFor(identity: String?): String? = if (identity != null && identity == selectedPc.value) selectedMac.value else null

    fun environmentFor(identity: String?): EditorEnvironment {
        val (pc, apps) = launcher.value
        val (keysPc, keys) = mediaKeys.value
        val (sensorPc, sensorList) = sensors.value
        return EditorEnvironment(
            launcherAppIds = if (pc != null && pc == identity && !apps.isNullOrEmpty()) apps.map { it.id }.toSet() else null,
            mediaKeysSupported = if (keysPc != null && keysPc == identity) keys else null,
            // An empty frame says nothing about the catalog; only a non-empty one can say "missing".
            sensors = if (sensorPc != null && sensorPc == identity && !sensorList.isNullOrEmpty()) sensorList else null,
            homeId = home.value?.id,
            reachableAway = identity != null && hostSync.value[identity]?.reachableAwayAtUnixMs != null,
        )
    }

    /**
     * Saves the open draft. The target PC's MAC is refreshed into its `wake` steps first when RemEx
     * knows it, so a MAC fixed in Connection settings reaches the routine on its next save.
     */
    suspend fun saveDraft(autoName: String): RoutineSaveResult {
        val draft = _draft.value ?: return RoutineSaveResult.Unavailable
        val mac = macFor(draft.hostIdentity)
        val prepared = if (mac != null) draft.withWakeMac(mac) else draft
        val result = repository.save(prepared.toRoutine(autoName))
        if (result is RoutineSaveResult.Saved) {
            // The pane keeps the key it was opened with (a template or blank); draftSource stays the
            // same so a recomposition that asks for that key again keeps this saved draft.
            val saved = RoutineDrafts.fromRoutine(result.routine)
            _draft.value = saved
            _draftOriginal.value = saved
        }
        return result
    }

    // ── List actions ──

    fun setEnabled(routineId: String, enabled: Boolean) {
        viewModelScope.launch {
            val result = repository.setEnabled(routineId, enabled)
            if (result !is RoutineSaveResult.Saved) post(saveFailureText(result))
            _draft.value?.takeIf { it.base?.id == routineId }?.let { d ->
                val stored = repository.routines.value.firstOrNull { it.routine.id == routineId }?.routine
                if (stored != null) {
                    _draft.value = d.copy(enabled = stored.enabled, base = stored)
                    _draftOriginal.value = _draftOriginal.value?.copy(enabled = stored.enabled, base = stored)
                }
            }
        }
    }

    fun setPausedAll(paused: Boolean) {
        viewModelScope.launch {
            if (!repository.setPausedAll(paused)) post(app.getString(R.string.routines_error_not_saved))
        }
    }

    // ── Home (S3, RemEx-pp0rt.8) ──

    /** The phone's one home, or null. */
    val home: StateFlow<Home?> = repository.home

    /** "Your home network looks different" (`home_fingerprint_stale`). */
    val homeDrift: StateFlow<Boolean> = HomePresence.drift

    private val _backgroundRestricted = MutableStateFlow(false)

    /** Android restricts RemEx in the background, so home routines may be held back (spec 1.3 step 8). */
    val backgroundRestricted: StateFlow<Boolean> = _backgroundRestricted.asStateFlow()

    /** Re-read on resume: the person may have just changed it in system settings. */
    fun refreshBackgroundRestricted() {
        _backgroundRestricted.value = app.getSystemService(android.app.ActivityManager::class.java)?.isBackgroundRestricted == true
    }

    /** The capture API (§8.3.1): what the home sheet shows before the person confirms. */
    suspend fun probeHome(): HomeCaptureProbe = HomePresence.probe(app)

    /** "Use this network as home" (spec 1.4). The open draft's home trigger picks the home up at once. */
    fun saveHome(facts: HomeFacts, pcIdentity: String) {
        viewModelScope.launch {
            if (!HomePresence.saveHome(app, facts, pcIdentity)) {
                post(app.getString(R.string.routines_error_not_saved))
                return@launch
            }
            val id = home.value?.id ?: return@launch
            updateDraft { d -> if (RoutineHomeRules.isHomeTrigger(d.trigger?.type) && d.trigger?.homeId != id) d.copy(trigger = d.trigger?.copy(homeId = id)) else d }
            post(app.getString(R.string.routines_home_saved))
        }
    }

    /** "Forget home" (spec 1.4): the routines that use it stay, and need a home before they run again. */
    fun forgetHome() {
        viewModelScope.launch {
            if (HomePresence.forgetHome(app)) post(app.getString(R.string.routines_home_forgotten)) else post(app.getString(R.string.routines_error_not_saved))
        }
    }

    /** Names of the routines that start at home, for the "Forget home" confirmation. */
    fun homeRoutineNames(): List<String> =
        routines.value.map { it.routine }.filter { RoutineHomeRules.isHomeTrigger(it.trigger?.type) }.mapNotNull { it.name }

    // ── NFC tags, shortcuts and widgets (S2, RemEx-pp0rt.7) ──

    private val secrets = Routines.secrets(application)

    /** Bumped whenever a tag is written, so the trigger card re-reads "Tag written <date>". */
    private val _nfcRevision = MutableStateFlow(0)
    val nfcRevision: StateFlow<Int> = _nfcRevision.asStateFlow()

    suspend fun nfcBinding(routineId: String): RoutineNfcBinding? =
        runCatchingIo("Reading a routine's tag token", null) { secrets.nfcBinding(routineId) }

    /** A tag was written with [token] (a rotation when it differs: older tags stop working). */
    suspend fun commitNfcToken(routineId: String, token: String) {
        runCatchingIo("Storing a routine's tag token", Unit) { secrets.commitNfcToken(routineId, token, System.currentTimeMillis()) }
        _nfcRevision.value++
    }

    /** A routine's name on this phone, or null; safe from any thread. */
    fun routineName(routineId: String): String? = routine(routineId)?.name

    /** "Add to home screen" (spec 1.6 "Pin", R-UX-24). */
    fun requestShortcut(routineId: String) {
        viewModelScope.launch {
            val routine = routine(routineId) ?: return@launch
            when (RoutineShortcuts.requestPin(app, routine)) {
                RoutineShortcuts.PinResult.REQUESTED -> Unit
                RoutineShortcuts.PinResult.UNSUPPORTED -> post(app.getString(R.string.routines_shortcut_unsupported))
                RoutineShortcuts.PinResult.NOT_PINNABLE -> post(app.getString(R.string.routines_pin_manual_only))
            }
        }
    }

    /** "Add widget" (spec 1.6 "Pin", R-UX-25). */
    fun requestWidget(routineId: String) {
        val routine = routine(routineId)
        when {
            !RoutineManualEntry.isPinnable(routine) -> post(app.getString(R.string.routines_pin_manual_only))
            !RoutineWidget.requestPin(app, routineId) -> post(app.getString(R.string.routines_widget_unsupported))
        }
    }

    fun delete(routineId: String) {
        viewModelScope.launch {
            if (repository.delete(routineId)) {
                // Its tags stop working (spec 1.5); shortcuts and widgets follow via RoutineSurfaces.
                runCatchingIo("Forgetting a deleted routine's tag", Unit) { secrets.removeRoutine(routineId) }
                if (_draft.value?.base?.id == routineId) closeEditor()
                post(app.getString(R.string.routines_deleted))
            } else {
                post(app.getString(R.string.routines_error_not_saved))
            }
        }
    }

    fun duplicate(routineId: String) {
        viewModelScope.launch {
            val routine = routines.value.firstOrNull { it.routine.id == routineId }?.routine ?: return@launch
            val copy = RoutineCopies.copyOf(routine) { name -> app.getString(R.string.routines_copy_name, name) }
            when (val result = repository.save(copy)) {
                is RoutineSaveResult.Saved -> post(app.getString(R.string.routines_duplicated, result.routine.name.orEmpty()))
                else -> post(saveFailureText(result))
            }
        }
    }

    fun move(routineId: String, delta: Int) {
        viewModelScope.launch {
            val order = RoutineOrder.move(routines.value.map { it.routine }, routineId, delta) ?: return@launch
            if (!repository.reorder(order)) post(app.getString(R.string.routines_error_not_saved))
        }
    }

    /** In-app Run and Test (spec 1.6): source `manual.app`, so a switched-off routine still runs. */
    fun run(routineId: String, testRun: Boolean) {
        viewModelScope.launch {
            val routine = routines.value.firstOrNull { it.routine.id == routineId }?.routine
            val args = RoutineReasonArgs(pc = routine?.hostIdentity?.let(::pcName), routine = routine?.name)
            when (val start = repository.run(routineId, RoutineRunSources.MANUAL_APP, testRun)) {
                is RoutineRunStart.Started -> Unit
                is RoutineRunStart.Skipped -> post(RoutineReasonText.message(app, start.reasonCode, args))
                is RoutineRunStart.Failed ->
                    postMessage(
                        RoutinesMessage(RoutineReasonText.message(app, start.reasonCode, args), RoutinesMessageAction.OpenRun(start.runId)),
                    )
                is RoutineRunStart.Invalid ->
                    post(app.getString(R.string.routines_run_invalid, RoutineReasonText.message(app, start.verdict.reasonCode, args.copy(detail = start.verdict.detail))))
                RoutineRunStart.RunsOnPc -> runOnPc(routineId, testRun, args)
                RoutineRunStart.NotFound -> post(app.getString(R.string.routines_run_not_found))
                RoutineRunStart.Unavailable -> post(app.getString(R.string.routines_run_unavailable))
            }
        }
    }

    /** Run and Test of a PC-run routine: `routine_run_request`, the PC runs its own copy (§7.3.8). */
    private suspend fun runOnPc(routineId: String, testRun: Boolean, args: RoutineReasonArgs) {
        postPcRunStart(routineId, testRun, syncClient.runOnPc(routineId, testRun), args)
    }

    private fun postPcRunStart(routineId: String, testRun: Boolean, start: RoutinePcRunStart, args: RoutineReasonArgs) {
        when (start) {
            is RoutinePcRunStart.Started ->
                post(app.getString(if (testRun) R.string.routines_pc_test_started else R.string.routines_pc_run_started, pcNameOrDefault(args.pc)))
            is RoutinePcRunStart.Skipped ->
                postMessage(RoutinesMessage(RoutineReasonText.message(app, start.reasonCode, args), RoutinesMessageAction.OpenRun(start.runId)))
            is RoutinePcRunStart.NotSelected ->
                postMessage(
                    RoutinesMessage(
                        RoutineReasonText.message(app, RoutineReasonCodes.PC_NOT_SELECTED, args),
                        RoutinesMessageAction.SwitchAndRun(routineId, start.hostIdentity, testRun),
                    ),
                )
            is RoutinePcRunStart.Failed -> post(RoutineReasonText.message(app, start.reasonCode, args))
            RoutinePcRunStart.NotFound -> post(app.getString(R.string.routines_run_not_found))
        }
    }

    /** "Switch and run" (D8): the run waits for the person to switch to [hostIdentity] in Connection. */
    fun switchAndRun(routineId: String, hostIdentity: String, testRun: Boolean) {
        syncClient.runAfterSwitch(routineId, hostIdentity, testRun)
    }

    private fun pcNameOrDefault(name: String?): String = name?.takeIf { it.isNotBlank() } ?: app.getString(R.string.routine_pc_fallback_name)

    /** Stop: the phone run of [routineId], or else its PC run in progress (`routine_cancel`). */
    fun cancel(routineId: String) {
        viewModelScope.launch {
            if (repository.cancel(routineId)) return@launch
            val pcRun = pcActiveRuns.value[routineId]?.runId ?: return@launch
            if (!syncClient.cancelPcRun(pcRun)) post(RoutineReasonText.message(app, RoutineReasonCodes.PC_UNREACHABLE, RoutineReasonArgs(pc = routine(routineId)?.hostIdentity?.let(::pcName))))
        }
    }

    fun dismissStoreReset() {
        viewModelScope.launch { repository.dismissStoreReset() }
    }

    suspend fun unreadableText(): String? = repository.unreadableDocumentText()

    fun resetUnreadable() {
        viewModelScope.launch { if (!repository.resetUnreadableStore()) post(app.getString(R.string.routines_error_not_saved)) }
    }

    fun setCoachSeen(seen: Boolean) {
        viewModelScope.launch { settings.setRoutinesCoachSeen(seen) }
    }

    /** Asks the connected PC for its launcher list again (the same request App Launcher sends). */
    fun refreshLauncher() {
        viewModelScope.launch(Dispatchers.IO) {
            if (RemexCoreClient.isLibraryLoaded) RemexCoreClient.SendMessage("{\"type\":\"launcher_sync_request\"}")
        }
    }

    fun post(text: String) {
        messageChannel.post(RoutinesMessage(text))
    }

    fun postMessage(message: RoutinesMessage) {
        messageChannel.post(message)
    }

    fun pcName(identity: String): String? = pcs.value.firstOrNull { it.identity == identity }?.name

    fun routine(routineId: String?): Routine? = routines.value.firstOrNull { it.routine.id == routineId }?.routine

    fun saveFailureText(result: RoutineSaveResult): String =
        when (result) {
            is RoutineSaveResult.Invalid ->
                RoutineReasonText.message(app, result.verdict.reasonCode, RoutineReasonArgs(detail = result.verdict.detail))
            RoutineSaveResult.ReadOnly -> app.getString(R.string.routines_read_only_body)
            else -> app.getString(R.string.routines_error_not_saved)
        }

    private suspend fun identityOf(host: String): String? {
        if (host.isBlank()) return null
        return runCatchingIo("Reading a PC's pin", null) { HostIdentity.keyFor(PinnedHostStore.getPin(app, host)) }
    }

    private suspend fun <T> runCatchingIo(what: String, fallback: T, block: suspend () -> T): T =
        try {
            withContext(Dispatchers.IO) { block() }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            Log.w(TAG, "$what failed.", e)
            fallback
        }

    /** `tpl.game.steam` preselects a launcher entry named Steam when the PC's list has one (spec 4.1). */
    private fun preselectSteam(draft: RoutineDraft, hostIdentity: String?): RoutineDraft {
        val (pc, apps) = launcher.value
        val steam = apps?.takeIf { pc != null && pc == hostIdentity }?.firstOrNull { it.name.equals("Steam", ignoreCase = true) } ?: return draft
        return draft.copy(
            steps =
                draft.steps.map {
                    if (it.step.type == RoutineStepTypes.LAUNCH_APP && it.step.appId == null) it.copy(step = it.step.copy(appId = steam.id, appLabel = steam.name)) else it
                },
        )
    }

    companion object {
        private const val TAG = "RoutinesViewModel"
        private const val STEAM_TEMPLATE = "tpl.game.steam"

        /** `aa-bb-cc-dd-ee-ff`, `AABBCCDDEEFF` or `aa:bb:...` to `AA:BB:CC:DD:EE:FF`; null when it is not a MAC. */
        fun normalizeMac(raw: String?): String? {
            val hex = raw.orEmpty().filter { it.isLetterOrDigit() }.uppercase()
            if (hex.length != 12 || !hex.all { it in '0'..'9' || it in 'A'..'F' }) return null
            return hex.chunked(2).joinToString(":")
        }

        /** The launcher JSON (PC `AppEntry`, camelCase). Entries without a GUID id are left out. */
        fun parseLauncher(json: String): List<RoutineAppChoice> =
            runCatching {
                val array = JSONArray(json)
                (0 until array.length()).mapNotNull { i ->
                    val obj = array.optJSONObject(i) ?: return@mapNotNull null
                    val id = obj.optString("id").takeIf { it.length == 36 } ?: return@mapNotNull null
                    val name = obj.optString("displayName").takeIf { it.isNotBlank() } ?: return@mapNotNull null
                    RoutineAppChoice(id.lowercase(), name)
                }.distinctBy { it.id }
            }.getOrDefault(emptyList())
    }
}
