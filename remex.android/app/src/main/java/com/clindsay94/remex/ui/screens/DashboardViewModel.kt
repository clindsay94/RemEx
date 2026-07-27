package com.clindsay94.remex.ui.screens

import android.app.Application
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.RemexCoreClient
import com.clindsay94.remex.data.SettingsManager
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import com.clindsay94.remex.R
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject
import kotlin.math.roundToInt
import com.clindsay94.remex.ui.telemetry.MetricKind
import com.clindsay94.remex.ui.telemetry.MetricUnits

enum class HomeCardType { PC_STATUS, TELEMETRY, WAKE_ON_LAN }

enum class TelemetryDisplayMode {
    AUTO,          // resolves at render via bestDisplayModeFor(sensor) - mirrors PC GraphType.Auto
    VALUE,         // big value + trend delta
    VALUE_SPARK,   // value + mini-sparkline - smart default
    RING_GAUGE,    // wavy ring (was GAUGE)
    ARC_GAUGE,     // true 270 degree radial arc (was CIRCLE_GAUGE, now visually distinct)
    LINE,          // true polyline
    AREA,          // gradient-filled area
    BAR,           // bar histogram
    HUE_PULSE,     // ambient: tile glows cool to warm by load
    LED_METER,     // ambient: segmented LED column
    DUAL_METRIC    // ambient: two metrics overlaid (needs secondarySensorId)
}

data class TelemetryState(
    val cpuUsage: Int = 0,
    val gpuUsage: Int = 0,
    val ramUsage: Int = 0
)

data class TelemetrySensor(
    val id: String,
    val name: String,
    val category: String,
    val value: Double,
    val unit: String,
    val kind: MetricKind = MetricKind.UNKNOWN,
    val group: String = ""
)

data class HomeCardState(
    val id: String,
    val title: String,
    val type: HomeCardType,
    val sensorId: String? = null,
    val xDp: Float = 12f,
    val yDp: Float = 12f,
    val widthDp: Float = 160f,
    val heightDp: Float = 140f,
    val displayMode: TelemetryDisplayMode = TelemetryDisplayMode.AUTO,
    val secondarySensorId: String? = null,
    val pinned: Boolean = false,
    val shapePreset: Float = DashboardShapes.SHAPE_PRESET_INHERIT,
    val customTitle: String? = null,
    val showValueOverlay: Boolean = false
)

/**
 * Deterministically selects the sensor a card should display. Curated cards (cpu/gpu/ram) bind by
 * semantic [MetricKind] so an Unknown/timing sensor can never win a load slot — the fix for the
 * "1089.0ms" RAM bug. Everything else matches by stable id. This is the ONE selection rule,
 * replacing the two divergent lookups (associateBy last-wins in the view, firstOrNull first-wins in
 * the VM) that used to disagree.
 */
fun selectSensor(cardId: String?, sensors: List<TelemetrySensor>): TelemetrySensor? {
    if (cardId == null) return null
    val acceptable: List<MetricKind> = when (cardId) {
        "sensor:cpu" -> listOf(MetricKind.CPU_LOAD)
        "sensor:gpu" -> listOf(MetricKind.GPU_LOAD)
        "sensor:ram" -> listOf(MetricKind.RAM_USED_GB, MetricKind.RAM_LOAD)
        "sensor:ramtotal" -> listOf(MetricKind.RAM_TOTAL_GB)
        "sensor:cputemp" -> listOf(MetricKind.CPU_TEMP_C, MetricKind.TEMP_C)
        "sensor:gputemp" -> listOf(MetricKind.GPU_TEMP_C, MetricKind.TEMP_C)
        "sensor:nettotal" -> listOf(MetricKind.NET_THROUGHPUT_MBPS)
        else -> emptyList()
    }
    if (acceptable.isNotEmpty()) {
        for (k in acceptable) {
            sensors.firstOrNull { it.kind == k }?.let { return it }
        }
        // Curated card, but a new host sent no kind-matched sensor: prefer a same-id sensor that
        // isn't the Unknown sink; only an old host with no kinds at all falls through to a raw id match.
        return sensors.firstOrNull { it.id == cardId && it.kind != MetricKind.UNKNOWN }
            ?: sensors.firstOrNull { it.id == cardId }
    }
    return sensors.firstOrNull { it.id == cardId }
}

/**
 * Number of sequential Home Base coach-mark hints (RemEx-km0i.10). Single source of truth shared by
 * [DashboardViewModel]'s advance logic and [DashboardCoachOverlay].
 */
const val DASHBOARD_COACH_HINT_COUNT = 6

class DashboardViewModel(application: Application) : AndroidViewModel(application) {

    private val settingsManager = SettingsManager(application)

    val isConnected: StateFlow<Boolean> = RemexClientManager.isConnected
    val isConnecting: StateFlow<Boolean> = RemexClientManager.isConnecting

    private val _telemetryState = MutableStateFlow(TelemetryState())
    val telemetryState: StateFlow<TelemetryState> = _telemetryState.asStateFlow()

    private val _wakeStatus = MutableSharedFlow<String>(extraBufferCapacity = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    val wakeStatus = _wakeStatus.asSharedFlow()

    private val _telemetrySensors = MutableStateFlow<List<TelemetrySensor>>(emptyList())
    val telemetrySensors: StateFlow<List<TelemetrySensor>> = _telemetrySensors.asStateFlow()

    private val _telemetryHistory = MutableStateFlow<Map<String, List<Float>>>(emptyMap())
    val telemetryHistory: StateFlow<Map<String, List<Float>>> = _telemetryHistory.asStateFlow()

    private val _homeCards = MutableStateFlow(defaultCards())
    val homeCards: StateFlow<List<HomeCardState>> = _homeCards.asStateFlow()

    private val _enabledCardIds = MutableStateFlow(
        setOf(
            "pc_status", "wake_pc",
            "sensor:cpu", "sensor:gpu", "sensor:ram",
            "sensor:ramtotal", "sensor:cputemp", "sensor:gputemp", "sensor:nettotal"
        )
    )
    val enabledCardIds: StateFlow<Set<String>> = _enabledCardIds.asStateFlow()

    // ── Undo / redo history for canvas edits ────────────────────────────────────
    private data class LayoutSnapshot(val cards: List<HomeCardState>, val enabled: Set<String>)
    private val undoStack = ArrayDeque<LayoutSnapshot>()
    private val redoStack = ArrayDeque<LayoutSnapshot>()
    private val maxHistory = 30

    private val _canUndo = MutableStateFlow(false)
    val canUndo: StateFlow<Boolean> = _canUndo.asStateFlow()
    private val _canRedo = MutableStateFlow(false)
    val canRedo: StateFlow<Boolean> = _canRedo.asStateFlow()

    private fun snapshot() = LayoutSnapshot(_homeCards.value, _enabledCardIds.value)

    /** Capture the current layout before a mutating interaction (drag, resize, toggle, …). */
    fun beginInteraction() {
        undoStack.addLast(snapshot())
        if (undoStack.size > maxHistory) undoStack.removeFirst()
        redoStack.clear()
        _canUndo.value = true
        _canRedo.value = false
    }

    fun undo() {
        if (undoStack.isEmpty()) return
        redoStack.addLast(snapshot())
        val s = undoStack.removeLast()
        _homeCards.value = s.cards
        _enabledCardIds.value = s.enabled
        _canUndo.value = undoStack.isNotEmpty()
        _canRedo.value = true
        persistHomeLayout()
    }

    fun redo() {
        if (redoStack.isEmpty()) return
        undoStack.addLast(snapshot())
        val s = redoStack.removeLast()
        _homeCards.value = s.cards
        _enabledCardIds.value = s.enabled
        _canRedo.value = redoStack.isNotEmpty()
        _canUndo.value = true
        persistHomeLayout()
    }

    /** Toggle a card's pinned state — position-anchored + resize-locked, but still liftable/selectable. */
    fun togglePin(cardId: String) {
        _homeCards.update { cards ->
            cards.map { if (it.id == cardId) it.copy(pinned = !it.pinned) else it }
        }
        persistHomeLayout()
    }

    /** Remove every card from the canvas (undoable). */
    fun clearAllCards() {
        beginInteraction()
        _enabledCardIds.value = emptySet()
        persistHomeLayout()
    }

    // ── Two-layer touch model (transient, never persisted) ────────────────────
    // NORMAL: quick drag pans the canvas. Hold 0.5s -> haptic -> the card is "picked up":
    //   - drag it     -> transient MOVE (draggingCardId); release drops it -> NORMAL.
    //   - release still-> SELECT the card (selectedCardIds); tap more cards -> group; the
    //                     Pin/Reshape/Remove action bar shows for as long as a selection exists.
    // selectionActive is derived by the UI as selectedCardIds.isNotEmpty().
    private val _draggingCardId = MutableStateFlow<String?>(null)
    val draggingCardId: StateFlow<String?> = _draggingCardId.asStateFlow()
    private val _selectedCardIds = MutableStateFlow<Set<String>>(emptySet())
    val selectedCardIds: StateFlow<Set<String>> = _selectedCardIds.asStateFlow()

    // ── Home Base coach marks (RemEx-km0i.10) ──
    // -1 = hidden; 0..DASHBOARD_COACH_HINT_COUNT-1 = the sequential hints. The overlay is additionally
    // render-gated by the UI on drag/selection/sheet state (locked decision #7), so the step persists
    // underneath a transient suppression rather than being torn down and lost.
    private val _coachStep = MutableStateFlow(-1)
    val coachStep: StateFlow<Int> = _coachStep.asStateFlow()

    /** Advance to the next hint; on the last one, finish and remember it was seen. */
    fun advanceCoach() {
        val next = _coachStep.value + 1
        if (next >= DASHBOARD_COACH_HINT_COUNT) dismissCoach() else _coachStep.value = next
    }

    /** Hide the overlay and persist so it never auto-shows again. */
    fun dismissCoach() {
        _coachStep.value = -1
        viewModelScope.launch { settingsManager.markDashboardCoachSeen() }
    }

    /** Replay from the first hint. Ignored while a card is lifted or a group is selected. */
    fun replayCoach() {
        if (_draggingCardId.value != null || _selectedCardIds.value.isNotEmpty()) return
        _coachStep.value = 0
    }

    /** First visit only: auto-show unless already seen (and not mid-gesture). Called once from init. */
    private fun maybeAutoShowCoach() {
        viewModelScope.launch {
            if (!settingsManager.dashboardCoachSeenFlow.first() &&
                _draggingCardId.value == null &&
                _selectedCardIds.value.isEmpty()
            ) {
                _coachStep.value = 0
            }
        }
    }

    /** Card picked up and dragged (not selection) - one undo snapshot for the whole move. */
    fun beginCardDrag(cardId: String) {
        beginInteraction()
        _draggingCardId.value = cardId
    }

    /** Per-frame delta while a single picked-up card is being dragged (pinned cards resist). */
    fun dragCardBy(deltaXDp: Float, deltaYDp: Float) {
        val id = _draggingCardId.value ?: return
        _homeCards.update { cards ->
            cards.map { card ->
                if (card.id == id && !card.pinned) {
                    card.copy(
                        xDp = (card.xDp + deltaXDp).coerceAtLeast(0f),
                        yDp = (card.yDp + deltaYDp).coerceAtLeast(0f)
                    )
                } else {
                    card
                }
            }
        }
    }

    /** Finger lifted after a MOVE - drop the card and return to NORMAL. */
    fun endCardDrag() {
        _draggingCardId.value = null
        persistHomeLayout()
    }

    /** Held-then-released-in-place - select just this card (enters selection mode + shows bar). */
    fun selectCard(cardId: String) {
        _draggingCardId.value = null
        _selectedCardIds.value = setOf(cardId)
    }

    /** Tap a card while selectionActive - add/remove it from the group. */
    fun toggleCardInSelection(cardId: String) {
        _selectedCardIds.value = FileManagerLogic.toggleSelection(_selectedCardIds.value, cardId)
    }

    /** Exit selection entirely - back to NORMAL. */
    fun clearSelection() {
        _selectedCardIds.value = emptySet()
    }

    /**
     * Moves every non-pinned selected card by the same delta - group move while in selection mode.
     * No beginInteraction()/persist here; the screen owns that lifecycle (drag start/end).
     */
    fun moveSelection(deltaXDp: Float, deltaYDp: Float) {
        val ids = _selectedCardIds.value
        if (ids.isEmpty()) return
        _homeCards.update { cards ->
            cards.map { card ->
                if (card.id in ids && !card.pinned) {
                    card.copy(
                        xDp = (card.xDp + deltaXDp).coerceAtLeast(0f),
                        yDp = (card.yDp + deltaYDp).coerceAtLeast(0f)
                    )
                } else {
                    card
                }
            }
        }
    }

    /** Action-bar Pin - all-pinned unpins all, otherwise pins all (one undo step). */
    fun togglePinSelection() {
        beginInteraction()
        val ids = _selectedCardIds.value
        val allPinned = _homeCards.value.filter { it.id in ids }.all { it.pinned }
        _homeCards.update { cards -> cards.map { if (it.id in ids) it.copy(pinned = !allPinned) else it } }
        persistHomeLayout()
    }

    /** Action-bar Remove - disables the selected cards (geometry kept, reversible via undo). */
    fun removeSelection() {
        beginInteraction()
        _enabledCardIds.update { it - _selectedCardIds.value }
        persistHomeLayout()
        clearSelection()
    }

    val cardCornerRadius = settingsManager.cardCornerRadiusFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 20)

    val cardOpacity = settingsManager.cardOpacityFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 1.0f)

    val pcCardShapePreset = settingsManager.pcCardShapePresetFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), DashboardShapes.SHAPE_PRESET_INHERIT)

    val telemetryCardShapePreset = settingsManager.telemetryCardShapePresetFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), DashboardShapes.SHAPE_PRESET_INHERIT)

    val appLauncherCardShapePreset = settingsManager.appLauncherCardShapePresetFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 0f)

    val taskManagerCardShapePreset = settingsManager.taskManagerCardShapePresetFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 0f)

    val remoteDesktopCardShapePreset = settingsManager.remoteDesktopCardShapePresetFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 0f)

    val remoteControlCardShapePreset = settingsManager.remoteControlCardShapePresetFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 0f)

    val remoteMouseCardShapePreset = settingsManager.remoteMouseCardShapePresetFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 0f)

    init {
        loadSavedHomeLayout()

        viewModelScope.launch {
            settingsManager.migrateShapeDefaultsV2()
            settingsManager.migrateShapeDefaultsV3()
        }

        maybeAutoShowCoach()

        viewModelScope.launch {
            RemexClientManager.telemetry.collect { telemetryData ->
                try {
                    val json = JSONObject(telemetryData)
                    val sensors = json.optJSONArray("sensors")

                    _telemetryState.update {
                        it.copy(
                            cpuUsage = sensors.extractPercent("CPU", listOf("cpu", "usage")),
                            gpuUsage = sensors.extractPercent("GPU", listOf("gpu", "usage")),
                            ramUsage = sensors.extractPercent("Memory", listOf("memory", "load"))
                        )
                    }

                    val parsed = parseSensors(sensors)
                    _telemetrySensors.value = parsed
                    updateTelemetryHistory(parsed)
                    ensureDefaultCardsExist(parsed)
                } catch (e: Exception) {
                    Log.w("DashboardVM", "Failed to parse telemetry", e)
                }
            }
        }
    }

    fun wakePc() {
        viewModelScope.launch {
            try {
                if (RemexCoreClient.isLibraryLoaded) {
                    val mac = settingsManager.macAddressFlow.first()
                    val broadcast = settingsManager.broadcastIpFlow.first()
                    if (mac.isNotEmpty()) {
                        // Report the *actual* native result rather than optimistically saying "sent"
                        // (mirrors RemoteControlViewModel.wakePc): the WakePc JNI call returns a JSON
                        // envelope with a success flag, so a failed send now surfaces as a failure. (RemEx-nbfb)
                        val responseJson = RemexCoreClient.WakePc(mac, broadcast, 9).getOrNull() ?: ""
                        val success = try {
                            JSONObject(responseJson).optBoolean("success", false)
                        } catch (e: Exception) {
                            false
                        }
                        if (success) {
                            Log.d("DashboardVM", "Wake-on-LAN packet sent to $mac via $broadcast:9")
                            _wakeStatus.tryEmit(getApplication<Application>().getString(R.string.wake_pc_sent))
                        } else {
                            Log.w("DashboardVM", "Wake-on-LAN not confirmed by native layer for $mac")
                            _wakeStatus.tryEmit(getApplication<Application>().getString(R.string.wake_pc_failed))
                        }
                    } else {
                        Log.w("DashboardVM", "Cannot send Wake-on-LAN: MAC address not configured")
                        _wakeStatus.tryEmit(getApplication<Application>().getString(R.string.wake_pc_mac_not_configured))
                    }
                } else {
                    Log.w("DashboardVM", "Cannot send Wake-on-LAN: RemexCoreClient library not loaded")
                    _wakeStatus.tryEmit(getApplication<Application>().getString(R.string.wake_pc_lib_not_loaded))
                }
            } catch (e: Throwable) {
                Log.e("DashboardVM", "Failed to send Wake-on-LAN packet", e)
                _wakeStatus.tryEmit(getApplication<Application>().getString(R.string.wake_pc_failed))
            }
        }
    }

    fun toggleConnection() {
        RemexClientManager.toggleConnection()
    }

    fun setCardEnabled(cardId: String, enabled: Boolean) {
        beginInteraction()
        if (enabled) {
            _enabledCardIds.update { it + cardId }
            ensureCardExists(cardId)
        } else {
            _enabledCardIds.update { it - cardId }
        }
        persistHomeLayout()
    }

    fun resizeCard(cardId: String, deltaWidthDp: Float, deltaHeightDp: Float) {
        _homeCards.update { cards ->
            cards.map { card ->
                if (card.id == cardId) {
                    card.copy(
                        widthDp = (card.widthDp + deltaWidthDp).coerceIn(140f, 600f),
                        heightDp = (card.heightDp + deltaHeightDp).coerceIn(120f, 500f)
                    )
                } else {
                    card
                }
            }
        }
    }

    /** Direct setter replacing blind cycling - one beginInteraction()/persist per pick. */
    fun setTelemetryDisplayMode(cardId: String, mode: TelemetryDisplayMode, secondarySensorId: String? = null) {
        beginInteraction()
        _homeCards.update { cards ->
            cards.map { card ->
                if (card.id == cardId && card.type == HomeCardType.TELEMETRY) {
                    card.copy(displayMode = mode, secondarySensorId = secondarySensorId ?: card.secondarySensorId)
                } else {
                    card
                }
            }
        }
        persistHomeLayout()
    }

    /** Per-card shape override (the shape picker's single-card write path). */
    fun setCardShape(cardId: String, shapeIndex: Float) {
        beginInteraction()
        _homeCards.update { cards -> cards.map { if (it.id == cardId) it.copy(shapePreset = shapeIndex) else it } }
        persistHomeLayout()
    }

    /** Group-reshape write path - ONE undo snapshot for the whole selection. */
    fun setGroupShape(cardIds: Set<String>, shapeIndex: Float) {
        beginInteraction()
        _homeCards.update { cards -> cards.map { if (it.id in cardIds) it.copy(shapePreset = shapeIndex) else it } }
        persistHomeLayout()
    }

    /** Blank/null clears the override - the card falls back to its original [HomeCardState.title]. */
    fun setCardCustomTitle(cardId: String, title: String?) {
        beginInteraction()
        val normalized = title?.takeIf { it.isNotBlank() }
        _homeCards.update { cards -> cards.map { if (it.id == cardId) it.copy(customTitle = normalized) else it } }
        persistHomeLayout()
    }

    fun setCardValueOverlay(cardId: String, enabled: Boolean) {
        beginInteraction()
        _homeCards.update { cards -> cards.map { if (it.id == cardId) it.copy(showValueOverlay = enabled) else it } }
        persistHomeLayout()
    }

    fun saveCardLayout() {
        persistHomeLayout()
    }

    fun placeCardAt(cardId: String, xDp: Float, yDp: Float) {
        beginInteraction()
        _enabledCardIds.update { it + cardId }
        ensureCardExists(cardId)

        _homeCards.update { cards ->
            cards.map { card ->
                if (card.id == cardId) {
                    card.copy(
                        xDp = xDp.coerceAtLeast(0f),
                        yDp = yDp.coerceAtLeast(0f)
                    )
                } else {
                    card
                }
            }
        }

        persistHomeLayout()
    }

    private fun ensureDefaultCardsExist(sensors: List<TelemetrySensor>) {
        val telemetryDefaults = listOf(
            "sensor:cpu", "sensor:gpu", "sensor:ram",
            "sensor:ramtotal", "sensor:cputemp", "sensor:gputemp", "sensor:nettotal"
        )
        telemetryDefaults.forEach { requiredId ->
            if (sensors.any { it.id == requiredId } && _enabledCardIds.value.contains(requiredId)) {
                ensureCardExists(requiredId)
            }
        }
    }

    private fun ensureCardExists(cardId: String) {
        if (_homeCards.value.any { it.id == cardId }) {
            return
        }

        val nextOffset = (_homeCards.value.size * 18).toFloat()
        val card = when (cardId) {
            "pc_status" -> {
                HomeCardState(
                    id = "pc_status",
                    title = "PC Status",
                    type = HomeCardType.PC_STATUS,
                    xDp = 12f + nextOffset,
                    yDp = 12f + nextOffset,
                    widthDp = 220f,
                    heightDp = 140f
                )
            }
            "wake_pc" -> {
                HomeCardState(
                    id = "wake_pc",
                    title = "Wake PC",
                    type = HomeCardType.WAKE_ON_LAN,
                    xDp = 12f + nextOffset,
                    yDp = 12f + nextOffset,
                    widthDp = 160f,
                    heightDp = 140f
                )
            }
            else -> {
                val sensor = selectSensor(cardId, _telemetrySensors.value)
                    ?: return

                HomeCardState(
                    id = cardId,
                    title = sensor.name,
                    type = HomeCardType.TELEMETRY,
                    sensorId = sensor.id,
                    xDp = 12f + nextOffset,
                    yDp = 12f + nextOffset,
                    widthDp = 170f,
                    heightDp = 150f,
                    displayMode = TelemetryDisplayMode.AUTO
                )
            }
        }

        _homeCards.update { it + card }
    }

    private fun parseSensors(sensors: JSONArray?): List<TelemetrySensor> {
        if (sensors == null) {
            return emptyList()
        }

        val parsed = mutableListOf<TelemetrySensor>()
        for (index in 0 until sensors.length()) {
            val sensor = sensors.optJSONObject(index) ?: continue
            val name = sensor.optString("name")
            val category = sensor.optString("category")
            val value = sensor.optDouble("value", Double.NaN)
            val unit = sensor.optString("unit")
            if (name.isBlank() || value.isNaN()) {
                continue
            }

            val kind = MetricKind.fromWire(sensor.optString("kind").ifBlank { null })
            val hostId = sensor.optString("id")
            val group = sensor.optString("group")
            // Prefer a semantic slug for curated kinds, then the host's stable id, and only fall
            // back to the legacy name-based normalization for older hosts that send neither.
            val id = MetricUnits.cardSlug(kind) ?: hostId.ifBlank { normalizeSensorId(name, category) }
            parsed += TelemetrySensor(
                id = id,
                name = name,
                category = category,
                value = value,
                unit = unit,
                kind = kind,
                group = group
            )
        }

        return parsed
    }

    private fun updateTelemetryHistory(sensors: List<TelemetrySensor>) {
        _telemetryHistory.update { current ->
            val mutable = current.toMutableMap()
            for (sensor in sensors) {
                val prior = mutable[sensor.id].orEmpty()
                val updated = (prior + sensor.value.toFloat()).takeLast(40)
                mutable[sensor.id] = updated
            }
            mutable
        }
    }

    private fun loadSavedHomeLayout() {
        viewModelScope.launch {
            val savedLayout = settingsManager.homeLayoutJsonFlow.first()
            val enabledCardsJson = settingsManager.homeEnabledCardsJsonFlow.first()

            if (enabledCardsJson.isNotBlank()) {
                val enabled = mutableSetOf<String>()
                val array = JSONArray(enabledCardsJson)
                for (i in 0 until array.length()) {
                    val id = array.optString(i)
                    if (id.isNotBlank()) {
                        enabled += id
                    }
                }
                if (enabled.isNotEmpty()) {
                    _enabledCardIds.value = enabled
                }
            }

            if (savedLayout.isBlank()) {
                return@launch
            }

            // Envelope-detecting reader: pre-2.0 saves are a bare array (schemaVersion 0);
            // 2.0+ saves are `{ "schemaVersion": 1, "cards": [...] }`.
            val trimmed = savedLayout.trimStart()
            val array = if (trimmed.startsWith("[")) {
                JSONArray(savedLayout)
            } else {
                JSONObject(savedLayout).optJSONArray("cards") ?: JSONArray()
            }

            val cards = mutableListOf<HomeCardState>()
            for (i in 0 until array.length()) {
                val obj = array.optJSONObject(i) ?: continue
                val type = when (obj.optString("type")) {
                    "PC_STATUS" -> HomeCardType.PC_STATUS
                    "WAKE_ON_LAN" -> HomeCardType.WAKE_ON_LAN
                    else -> HomeCardType.TELEMETRY
                }
                // Backward-compat migration: legacy GAUGE/CIRCLE_GAUGE map onto their visually
                // closest 2.0 replacement; anything unrecognized (including absent) becomes AUTO.
                val mode = when (obj.optString("displayMode")) {
                    "VALUE" -> TelemetryDisplayMode.VALUE
                    "VALUE_SPARK" -> TelemetryDisplayMode.VALUE_SPARK
                    "GAUGE", "RING_GAUGE" -> TelemetryDisplayMode.RING_GAUGE
                    "CIRCLE_GAUGE", "ARC_GAUGE" -> TelemetryDisplayMode.ARC_GAUGE
                    "LINE" -> TelemetryDisplayMode.LINE
                    "AREA" -> TelemetryDisplayMode.AREA
                    "BAR" -> TelemetryDisplayMode.BAR
                    "HUE_PULSE" -> TelemetryDisplayMode.HUE_PULSE
                    "LED_METER" -> TelemetryDisplayMode.LED_METER
                    "DUAL_METRIC" -> TelemetryDisplayMode.DUAL_METRIC
                    else -> TelemetryDisplayMode.AUTO
                }

                val id = obj.optString("id")
                if (id.isBlank()) continue

                cards += HomeCardState(
                    id = id,
                    title = obj.optString("title").ifBlank { id },
                    type = type,
                    sensorId = obj.optString("sensorId").takeIf { it.isNotBlank() },
                    xDp = obj.optDouble("xDp", 12.0).toFloat(),
                    yDp = obj.optDouble("yDp", 12.0).toFloat(),
                    widthDp = obj.optDouble("widthDp", 160.0).toFloat(),
                    heightDp = obj.optDouble("heightDp", 140.0).toFloat(),
                    displayMode = mode,
                    secondarySensorId = obj.optString("secondarySensorId").takeIf { it.isNotBlank() },
                    pinned = obj.optBoolean("pinned", false),
                    shapePreset = obj.optDouble("shapePreset", DashboardShapes.SHAPE_PRESET_INHERIT.toDouble()).toFloat(),
                    customTitle = obj.optString("customTitle").takeIf { it.isNotBlank() },
                    showValueOverlay = obj.optBoolean("showValueOverlay", false)
                )
            }

            if (cards.isNotEmpty()) {
                _homeCards.value = cards
            }
        }
    }

    private fun persistHomeLayout() {
        viewModelScope.launch {
            val layoutArray = JSONArray()
            _homeCards.value.forEach { card ->
                val obj = JSONObject().apply {
                    put("id", card.id)
                    put("title", card.title)
                    put("type", card.type.name)
                    put("sensorId", card.sensorId)
                    put("xDp", card.xDp)
                    put("yDp", card.yDp)
                    put("widthDp", card.widthDp)
                    put("heightDp", card.heightDp)
                    put("displayMode", card.displayMode.name)
                    put("secondarySensorId", card.secondarySensorId)
                    put("pinned", card.pinned)
                    put("shapePreset", card.shapePreset)
                    put("customTitle", card.customTitle)
                    put("showValueOverlay", card.showValueOverlay)
                }
                layoutArray.put(obj)
            }

            val envelope = JSONObject().apply {
                put("schemaVersion", LAYOUT_SCHEMA_VERSION)
                put("cards", layoutArray)
            }

            val enabledArray = JSONArray()
            _enabledCardIds.value.forEach { enabledArray.put(it) }

            settingsManager.saveHomeLayout(envelope.toString())
            settingsManager.saveHomeEnabledCards(enabledArray.toString())
        }
    }

    private fun normalizeSensorId(name: String, category: String): String {
        val loweredName = name.lowercase()
        return when {
            loweredName.contains("cpu") -> "sensor:cpu"
            loweredName.contains("gpu") -> "sensor:gpu"
            loweredName.contains("memory") || loweredName.contains("ram") -> "sensor:ram"
            else -> {
                val slug = "${category}_${name}"
                    .lowercase()
                    .replace(Regex("[^a-z0-9]+"), "_")
                    .trim('_')
                "sensor:$slug"
            }
        }
    }

    private fun org.json.JSONArray?.extractPercent(category: String, preferredTokens: List<String>): Int {
        if (this == null) {
            return 0
        }

        var fallback = Double.NaN
        for (index in 0 until length()) {
            val sensor = optJSONObject(index) ?: continue
            val sensorCategory = sensor.optString("category")
            if (!sensorCategory.equals(category, ignoreCase = true)) {
                continue
            }

            val unit = sensor.optString("unit")
            val value = sensor.optDouble("value", Double.NaN)
            if (value.isNaN()) {
                continue
            }

            if (unit == "%") {
                val name = sensor.optString("name").lowercase()
                if (preferredTokens.all(name::contains)) {
                    return value.roundToInt().coerceIn(0, 100)
                }

                if (fallback.isNaN()) {
                    fallback = value
                }
            }
        }

        return if (fallback.isNaN()) 0 else fallback.roundToInt().coerceIn(0, 100)
    }

    private companion object {
        const val LAYOUT_SCHEMA_VERSION = 1

        fun defaultCards(): List<HomeCardState> {
            return listOf(
                HomeCardState(
                    id = "pc_status",
                    title = "PC Status",
                    type = HomeCardType.PC_STATUS,
                    xDp = 12f,
                    yDp = 12f,
                    widthDp = 220f,
                    heightDp = 140f
                ),
                HomeCardState(
                    id = "wake_pc",
                    title = "Wake PC",
                    type = HomeCardType.WAKE_ON_LAN,
                    xDp = 244f,
                    yDp = 12f,
                    widthDp = 160f,
                    heightDp = 140f
                ),
                HomeCardState(
                    id = "sensor:cpu",
                    title = "CPU",
                    type = HomeCardType.TELEMETRY,
                    sensorId = "sensor:cpu",
                    xDp = 20f,
                    yDp = 168f,
                    widthDp = 170f,
                    heightDp = 150f,
                    displayMode = TelemetryDisplayMode.AUTO
                ),
                HomeCardState(
                    id = "sensor:gpu",
                    title = "GPU",
                    type = HomeCardType.TELEMETRY,
                    sensorId = "sensor:gpu",
                    xDp = 200f,
                    yDp = 168f,
                    widthDp = 170f,
                    heightDp = 150f,
                    displayMode = TelemetryDisplayMode.AUTO
                ),
                HomeCardState(
                    id = "sensor:ram",
                    title = "RAM",
                    type = HomeCardType.TELEMETRY,
                    sensorId = "sensor:ram",
                    xDp = 20f,
                    yDp = 326f,
                    widthDp = 170f,
                    heightDp = 150f,
                    displayMode = TelemetryDisplayMode.AUTO
                ),
                HomeCardState(
                    id = "sensor:ramtotal",
                    title = "RAM Total",
                    type = HomeCardType.TELEMETRY,
                    sensorId = "sensor:ramtotal",
                    xDp = 200f,
                    yDp = 326f,
                    widthDp = 170f,
                    heightDp = 150f,
                    displayMode = TelemetryDisplayMode.AUTO
                ),
                HomeCardState(
                    id = "sensor:cputemp",
                    title = "CPU Temp",
                    type = HomeCardType.TELEMETRY,
                    sensorId = "sensor:cputemp",
                    xDp = 20f,
                    yDp = 484f,
                    widthDp = 170f,
                    heightDp = 150f,
                    displayMode = TelemetryDisplayMode.AUTO
                ),
                HomeCardState(
                    id = "sensor:gputemp",
                    title = "GPU Temp",
                    type = HomeCardType.TELEMETRY,
                    sensorId = "sensor:gputemp",
                    xDp = 200f,
                    yDp = 484f,
                    widthDp = 170f,
                    heightDp = 150f,
                    displayMode = TelemetryDisplayMode.AUTO
                ),
                HomeCardState(
                    id = "sensor:nettotal",
                    title = "Network",
                    type = HomeCardType.TELEMETRY,
                    sensorId = "sensor:nettotal",
                    xDp = 20f,
                    yDp = 642f,
                    widthDp = 170f,
                    heightDp = 150f,
                    displayMode = TelemetryDisplayMode.AUTO
                )
            )
        }
    }

    /** Per-category card-shape overrides; absent entries mean inherit (RemEx-mycn). */
    val categoryShapePresets = settingsManager.categoryShapePresetsFlow
}
