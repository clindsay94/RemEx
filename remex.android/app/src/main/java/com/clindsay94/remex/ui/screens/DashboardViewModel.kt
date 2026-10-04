package com.clindsay94.remex.ui.screens

import android.app.Application
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.data.HomePinsState
import com.clindsay94.remex.data.SensorAlertDirection
import com.clindsay94.remex.data.SensorAlertSeverity
import com.clindsay94.remex.data.SensorAlertsState
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.ui.screens.sensors.CardSpan
import com.clindsay94.remex.ui.screens.sensors.GridWidth
import com.clindsay94.remex.ui.screens.sensors.SensorGridMigration
import com.clindsay94.remex.ui.screens.sensors.SensorLayout
import com.clindsay94.remex.ui.screens.sensors.SensorLayoutCodec
import com.clindsay94.remex.ui.screens.sensors.SensorLayoutEditor
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject
import kotlin.math.roundToInt
import com.clindsay94.remex.ui.telemetry.MetricKind
import com.clindsay94.remex.ui.telemetry.MetricUnits

/**
 * What a saved card is. Only [TELEMETRY] cards live on the Sensors grid; [PC_STATUS] and
 * [WAKE_ON_LAN] are the free-form canvas's old cards, read only so the grid migration can drop them
 * (Home owns both since RemEx-wqo7a.7).
 */
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

/**
 * One Sensors grid card (RemEx-wqo7a.7). Its place is its position in the layout's card list and
 * its size is [span]; the free-form x/y/width/height and the position-anchor `pinned` flag went with
 * the canvas. [shapePreset] is still stored, so a downgrade keeps the user's choice, but the grid
 * draws every card in the same tile shape.
 */
data class HomeCardState(
    val id: String,
    val title: String,
    val type: HomeCardType,
    val sensorId: String? = null,
    val span: CardSpan = CardSpan.ONE_BY_ONE,
    val displayMode: TelemetryDisplayMode = TelemetryDisplayMode.AUTO,
    val secondarySensorId: String? = null,
    val shapePreset: Float = DashboardShapes.SHAPE_PRESET_INHERIT,
    val customTitle: String? = null,
    val showValueOverlay: Boolean = false
)

/**
 * The kinds a curated card will accept, in the order it prefers them.
 *
 * Extracted so the list has ONE definition shared by the direct lookup and the indexed one - two
 * copies of a priority order is how they come to disagree, and a disagreement here binds a card to a
 * plausible-looking wrong reading rather than failing.
 */
private fun acceptableKinds(cardId: String): List<MetricKind> = when (cardId) {
    "sensor:cpu" -> listOf(MetricKind.CPU_LOAD)
    "sensor:gpu" -> listOf(MetricKind.GPU_LOAD)
    "sensor:ram" -> listOf(MetricKind.RAM_USED_GB, MetricKind.RAM_LOAD)
    "sensor:ramtotal" -> listOf(MetricKind.RAM_TOTAL_GB)
    "sensor:cputemp" -> listOf(MetricKind.CPU_TEMP_C, MetricKind.TEMP_C)
    "sensor:gputemp" -> listOf(MetricKind.GPU_TEMP_C, MetricKind.TEMP_C)
    "sensor:nettotal" -> listOf(MetricKind.NET_THROUGHPUT_MBPS)
    else -> emptyList()
}

/**
 * One tick's sensors, indexed so a card lookup is O(1) instead of a scan (RemEx-cite item 4).
 *
 * **EVERY MAP KEEPS THE FIRST ENTRY, NOT THE LAST, AND THAT IS THE WHOLE CORRECTNESS ARGUMENT.**
 * [selectSensor] encodes three first-match preferences, and `associateBy` - the obvious way to build
 * these - keeps the LAST value per key. Building them that way inverts all three at once and
 * produces a dashboard that looks entirely plausible while bound to the wrong readings. `putIfAbsent`
 * is what preserves the order the scans had. SelectSensorTest pins all three.
 *
 * Rebuilt every tick on purpose: the sensors it holds carry the values that change every tick, so a
 * memoised index would serve stale readings. The win is one O(n) pass instead of two scans per
 * visible card.
 */
class SensorIndex(sensors: List<TelemetrySensor>) {
    private val byKind = HashMap<MetricKind, TelemetrySensor>(sensors.size)
    private val byId = HashMap<String, TelemetrySensor>(sensors.size)
    private val byIdTyped = HashMap<String, TelemetrySensor>(sensors.size)

    init {
        for (sensor in sensors) {
            byKind.putIfAbsent(sensor.kind, sensor)
            byId.putIfAbsent(sensor.id, sensor)
            if (sensor.kind != MetricKind.UNKNOWN) byIdTyped.putIfAbsent(sensor.id, sensor)
        }
    }

    fun select(cardId: String?): TelemetrySensor? {
        if (cardId == null) return null

        val acceptable = acceptableKinds(cardId)
        if (acceptable.isNotEmpty()) {
            for (kind in acceptable) {
                byKind[kind]?.let { return it }
            }
            // Curated card, but a new host sent no kind-matched sensor: prefer a same-id sensor that
            // isn't the Unknown sink; only an old host with no kinds at all falls through to a raw
            // id match.
            return byIdTyped[cardId] ?: byId[cardId]
        }
        return byId[cardId]
    }
}

/**
 * Everything one telemetry tick yields, computed before any of it is applied (RemEx-cite).
 *
 * Grouped into one value so the whole derivation crosses the dispatcher boundary once. Returning
 * four separate results would mean four hops, which costs more than the parse it was meant to
 * move.
 */
internal data class DerivedTelemetry(
    val cpu: Int,
    val gpu: Int,
    val ram: Int,
    val sensors: List<TelemetrySensor>,
)

/**
 * One headline percentage, accumulated as the sensor array is walked rather than scanned for.
 *
 * Same rule the three separate passes followed: a sensor whose unit is "%" in the category and whose lowercased
 * name contains EVERY preferred token, else the first "%" sensor in the category, else 0. The
 * original returned the moment it found a preferred match; a folded loop cannot stop early because
 * it is still building the sensor list, so the first preferred reading LATCHES instead - the same
 * answer by a different route, and the reason `offer` is a no-op once one has landed.
 */
private class PercentPicker(private val category: String, private val preferredTokens: List<String>) {
    private var preferred = Double.NaN
    private var fallback = Double.NaN

    fun offer(sensorCategory: String, unit: String, name: String, value: Double) {
        // NaN IS REFUSED HERE AND NOT ONLY BY THE CALLER. `preferred` doubles as the latch, so a NaN
        // reaching it would read as "never latched" - a later match would overwrite it and, with
        // none, result() would fall through to the fallback. Sound today because parseTelemetry
        // skips NaN first; this makes it sound wherever it is called from.
        if (value.isNaN() || !preferred.isNaN() || unit != "%") return
        if (!sensorCategory.equals(category, ignoreCase = true)) return

        val lowered = name.lowercase()
        if (preferredTokens.all(lowered::contains)) {
            preferred = value
        } else if (fallback.isNaN()) {
            fallback = value
        }
    }

    fun result(): Int {
        val chosen = if (preferred.isNaN()) fallback else preferred
        return if (chosen.isNaN()) 0 else chosen.roundToInt().coerceIn(0, 100)
    }
}

/**
 * Perf audit P2-11: `current` used to be copied whole and every reported sensor's list rebuilt
 * (list-concat + takeLast, two new lists) on every 1Hz tick with no key eviction, so a sensor that
 * stopped being reported (a USB drive unplugged, a host feature toggled off) stayed in the map
 * forever. Pulled out to a pure top-level function, same reason `parseTelemetry` above is one:
 * `DashboardViewModel` is an `AndroidViewModel` with no Robolectric in this module, so this is the
 * only way to exercise the logic directly rather than by source-scanning the private method.
 *
 * ONLY THE EVICTION HALF OF THE AUDIT'S SUGGESTED FIX IS HERE. Narrowing to only sensors bound to
 * a visible home card was NOT implemented: `telemetryHistory` also backs the sensor detail/picker
 * view (`DashboardScreen.kt`'s picked-sensor sparkline), which can show history for a sensor the
 * user just tapped that has never been a home card. "Bound" is therefore not simply "on a card",
 * and getting that union wrong would silently blank a freshly-picked sensor's sparkline with
 * nothing here able to catch it.
 */
internal fun pruneAndAppendTelemetryHistory(
    current: Map<String, List<Float>>,
    sensors: List<TelemetrySensor>,
): Map<String, List<Float>> =
    // Built straight from `sensors`, which both evicts (nothing not in `sensors` is ever put) and
    // avoids the intermediate HashSet + double map copy an explicit filterKeys().toMutableMap()
    // pass would cost - review round 1 LOW, since this row is a perf fix and a fix that adds
    // allocation on a stable sensor set would be the wrong trade. `this[sensor.id] ?: current[...]`
    // (not just `current[...]`) keeps today's behaviour if two sensors ever normalize to the same
    // id within one tick - the second one's append lands on the first one's already-updated list,
    // same as the old sequential-mutation code did, not on the stale pre-tick value.
    buildMap(sensors.size) {
        for (sensor in sensors) {
            val prior = (this[sensor.id] ?: current[sensor.id]).orEmpty()
            put(sensor.id, (prior + sensor.value.toFloat()).takeLast(40))
        }
    }

internal fun parseTelemetry(sensors: JSONArray?): DerivedTelemetry {
    val cpu = PercentPicker("CPU", listOf("cpu", "usage"))
    val gpu = PercentPicker("GPU", listOf("gpu", "usage"))
    val ram = PercentPicker("Memory", listOf("memory", "load"))
    val parsed = mutableListOf<TelemetrySensor>()

    if (sensors != null) {
        for (index in 0 until sensors.length()) {
            val sensor = sensors.optJSONObject(index) ?: continue
            val name = sensor.optString("name")
            val category = sensor.optString("category")
            val value = sensor.optDouble("value", Double.NaN)
            val unit = sensor.optString("unit")
            if (value.isNaN()) {
                continue
            }

            // THE PICKERS SEE THIS SENSOR AND THE CARD LIST BELOW MAY NOT, AND THAT ASYMMETRY IS
            // LOAD BEARING. The percentages used to come from a separate scan that discarded only
            // NaN readings, while the card list discards blank names too - so a host that
            // categorises its sensors correctly and labels them poorly still lit up the CPU/GPU/RAM
            // gauges even though it produced no cards. Folding the loops is only equivalent if the
            // offers happen BEFORE the blank-name skip; move these three lines below it and
            // ParseTelemetryTest fails.
            cpu.offer(category, unit, name, value)
            gpu.offer(category, unit, name, value)
            ram.offer(category, unit, name, value)

            if (name.isBlank()) {
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
    }

    return DerivedTelemetry(cpu.result(), gpu.result(), ram.result(), parsed)
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

/**
 * Which sensor a card binds to.
 *
 * Curated cards (cpu/gpu/ram) bind by semantic [MetricKind] so an Unknown or timing sensor can never
 * win a load slot - the fix for the "1089.0ms" RAM bug. Everything else matches by stable id. This is
 * the ONE selection rule, replacing the two divergent lookups (associateBy last-wins in the view,
 * firstOrNull first-wins in the VM) that used to disagree.
 *
 * Delegates to [SensorIndex] so there is ONE implementation of it. A caller resolving many cards
 * against the same tick should build the index once and call [SensorIndex.select] directly - this
 * overload builds one per call and exists for the occasional single lookup.
 */
fun selectSensor(cardId: String?, sensors: List<TelemetrySensor>): TelemetrySensor? =
    SensorIndex(sensors).select(cardId)

/**
 * Number of sequential Sensors coach-mark hints (RemEx-km0i.10, reduced to the grid's three in
 * RemEx-wqo7a.8). Single source of truth shared by [DashboardViewModel]'s advance logic and
 * [DashboardCoachOverlay].
 */
const val DASHBOARD_COACH_HINT_COUNT = 3

/** The curated cards a fresh install starts with, in grid order. */
private val DEFAULT_CARD_IDS =
    listOf(
        "sensor:cpu", "sensor:gpu", "sensor:ram",
        "sensor:ramtotal", "sensor:cputemp", "sensor:gputemp", "sensor:nettotal"
    )

class DashboardViewModel(application: Application) : AndroidViewModel(application) {

    private val settingsManager = SettingsManager(application)

    val isConnected: StateFlow<Boolean> = RemexClientManager.isConnected
    val isConnecting: StateFlow<Boolean> = RemexClientManager.isConnecting

    private val _telemetryState = MutableStateFlow(TelemetryState())
    val telemetryState: StateFlow<TelemetryState> = _telemetryState.asStateFlow()

    private val _telemetrySensors = MutableStateFlow<List<TelemetrySensor>>(emptyList())
    val telemetrySensors: StateFlow<List<TelemetrySensor>> = _telemetrySensors.asStateFlow()

    private val _telemetryHistory = MutableStateFlow<Map<String, List<Float>>>(emptyMap())
    val telemetryHistory: StateFlow<Map<String, List<Float>>> = _telemetryHistory.asStateFlow()

    // ── The grid and its edit mode (RemEx-wqo7a.7 / .8) ─────────────────────────
    private val editor =
        SensorLayoutEditor(
            initial = SensorLayout(defaultCards(), DEFAULT_CARD_IDS.toSet()),
            onCommitted = { persistHomeLayout(it) },
        )

    /** The grid's cards in order, and which are shown. */
    val layout: StateFlow<SensorLayout> = editor.layout
    val editMode: StateFlow<Boolean> = editor.editMode
    val draggingCardId: StateFlow<String?> = editor.draggingId
    val canUndo: StateFlow<Boolean> = editor.canUndo
    val canRedo: StateFlow<Boolean> = editor.canRedo

    /** The connected PC's Home pins; edit mode's Pin to Home reads and writes this. */
    val homePins: StateFlow<HomePinsState> = RemexClientManager.homePins

    fun enterEditMode() {
        // A hint showing over a card the user is about to drag would sit on top of the drag.
        _coachStep.value = -1
        editor.enterEditMode()
    }

    fun exitEditMode() = editor.exitEditMode()

    fun beginCardDrag(cardId: String): Boolean = editor.beginDrag(cardId)

    fun dragCardTo(toIndex: Int) = editor.dragTo(toIndex)

    fun endCardDrag() = editor.endDrag()

    fun moveCard(cardId: String, toIndex: Int) = editor.moveCard(cardId, toIndex)

    fun cycleCardSpan(cardId: String) = editor.cycleSpan(cardId)

    fun removeCard(cardId: String) = editor.removeCard(cardId)

    fun clearAllCards() = editor.clearAll()

    fun undo() = editor.undo()

    /** Counts changes to the layout's history; see [SensorLayoutEditor.revision]. */
    val editRevision: StateFlow<Long> = editor.revision

    /** The card-removed snackbar's Undo: reverts the removal only if it is still the latest change. */
    fun undoIfUnchanged(revision: Long) = editor.undoIfUnchanged(revision)

    fun redo() = editor.redo()

    /** Pins or unpins a sensor on the PC's Home (or the phone's own list for an older PC). */
    fun setHomePin(sensorName: String, pinned: Boolean) = RemexClientManager.setHomePin(sensorName, pinned)

    // ── The PC's sensor alerts on the phone (RemEx-pp4cm.12) ──

    /** The connected PC's alert rules; the cards' bells, the "Alert me..." sheet and the Alerts list read this. */
    val sensorAlerts: StateFlow<SensorAlertsState> = RemexClientManager.sensorAlerts

    /** Whether the PC's alerts show as notifications on this phone ("Alerts from your PC"; on until switched off). */
    val pcAlertsEnabled: StateFlow<Boolean> =
        settingsManager.pcAlertsEnabledFlow.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), true)

    fun setPcAlertsEnabled(enabled: Boolean) {
        viewModelScope.launch { settingsManager.setPcAlertsEnabled(enabled) }
    }

    /** How many columns the Sensors grid has: Auto, 2, 3 or 4 (RemEx-pp4cm.16). */
    val gridWidth = settingsManager.sensorGridWidthFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), GridWidth.AUTO)

    fun setGridWidth(width: GridWidth) {
        viewModelScope.launch { settingsManager.setSensorGridWidth(width) }
    }

    /**
     * Sets (adds or replaces) the alert on a sensor; the PC owns the rule and confirms it with its next
     * list. False means nothing was sent, so the sheet stays open and says why.
     */
    suspend fun setSensorAlert(
        sensorName: String,
        displayName: String,
        unit: String?,
        threshold: Double,
        direction: SensorAlertDirection,
        severity: SensorAlertSeverity,
    ): Boolean = RemexClientManager.setSensorAlert(sensorName, displayName, unit, threshold, direction, severity)

    /** A sensor whose alert the PC refused after it was sent, for a snackbar. */
    val sensorAlertRefusals: SharedFlow<String> = RemexClientManager.sensorAlertRefusals

    fun removeSensorAlert(sensorName: String) = RemexClientManager.removeSensorAlert(sensorName)

    /** Asks the PC for its rules again, so a sheet opened on a stale list shows the PC's own. */
    fun refreshSensorAlerts() = RemexClientManager.refreshSensorAlerts()

    // ── Coach marks (RemEx-km0i.10) ──
    // -1 = hidden; 0..DASHBOARD_COACH_HINT_COUNT-1 = the sequential hints. Never shown in edit mode.
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

    /** Replay from the first hint. Ignored in edit mode. */
    fun replayCoach() {
        if (editor.editMode.value) return
        _coachStep.value = 0
    }

    /** First visit only: auto-show unless already seen. Called once from init. */
    private fun maybeAutoShowCoach() {
        viewModelScope.launch {
            if (!settingsManager.dashboardCoachSeenFlow.first() && !editor.editMode.value) {
                _coachStep.value = 0
            }
        }
    }

    val cardCornerRadius = settingsManager.cardCornerRadiusFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), com.clindsay94.remex.ui.theme.CardShapes.DEFAULT_CORNER_RADIUS_DP)

    val cardOpacity = settingsManager.cardOpacityFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 1.0f)

    val pcCardShapePreset = settingsManager.pcCardShapePresetFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), DashboardShapes.SHAPE_PRESET_INHERIT)

    val telemetryCardShapePreset = settingsManager.telemetryCardShapePresetFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), DashboardShapes.SHAPE_PRESET_INHERIT)

    /** Per-category card-shape overrides; absent entries mean inherit (RemEx-mycn). */
    val categoryShapePresets = settingsManager.categoryShapePresetsFlow

    // P1-16: mirrors TaskManagerViewModel.setAutoRefreshEnabled's role - a screen-driven flag, not a
    // repeatOnLifecycle inside the VM (a VM has no Lifecycle to key one on).
    private val _isDashboardVisible = MutableStateFlow(false)

    fun setVisible(visible: Boolean) {
        // Review fix (P1-16): the sparkline history kept filling in real time before this row, so a
        // gap while off-screen (e.g. minutes on another tab) used to be invisible - the history was
        // simply denser for that period. Now the collector is gated and history freezes at the last
        // on-screen sample, so resuming without a reset would stitch a real multi-minute swing onto
        // one 1s-wide slot on the chart, reading as a snap that never happened. Clearing on the
        // false->true edge trades that visual lie for an honest "history restarts" gap instead.
        if (visible && !_isDashboardVisible.value) {
            _telemetryHistory.value = emptyMap()
        }
        _isDashboardVisible.value = visible
        // Leaving the screen ends edit mode, so coming back never lands in it unannounced.
        if (!visible) editor.exitEditMode()
    }

    init {
        loadSavedHomeLayout()

        viewModelScope.launch {
            settingsManager.migrateShapeDefaultsV2()
            settingsManager.migrateShapeDefaultsV3()
        }

        maybeAutoShowCoach()

        viewModelScope.launch {
            RemexClientManager.telemetry.collect { telemetryData ->
                // P1-16: this collector used to parse every tick regardless of whether the dashboard
                // was on screen, since viewModelScope survives for as long as this VM's nav back-stack
                // entry does (the whole pager session, not just while composed - the pager's
                // beyondViewportPageCount doesn't stop it). Skip the parse entirely while off-screen;
                // setVisible is driven by the same pager isVisible signal TaskManagerScreen already
                // uses for its own poll (P0-1).
                if (!_isDashboardVisible.value) return@collect
                // PARSED OFF THE MAIN THREAD, IN ONE PASS (RemEx-cite). Every tick this deserialises
                // the whole sensor payload and derives everything the screen needs from it, at 1 Hz,
                // for as many sensors as the PC reports. Only the derivation moves; the state
                // applications below stay on the collector's thread.
                val derived =
                    withContext(Dispatchers.Default) {
                        runCatching {
                            parseTelemetry(JSONObject(telemetryData).optJSONArray("sensors"))
                        }
                            .onFailure { Log.w("DashboardVM", "Failed to parse telemetry", it) }
                            .getOrNull()
                    }

                if (derived != null) {
                    _telemetryState.update {
                        it.copy(cpuUsage = derived.cpu, gpuUsage = derived.gpu, ramUsage = derived.ram)
                    }
                    _telemetrySensors.value = derived.sensors
                    updateTelemetryHistory(derived.sensors)
                    ensureDefaultCardsExist(derived.sensors)
                }
            }
        }
    }

    /**
     * Adds or removes a card from the Add-card list (edit mode only). A card that has never existed
     * is built from the reporting sensor with that id.
     */
    fun setCardEnabled(cardId: String, enabled: Boolean) {
        editor.setCardShown(cardId, enabled) { newCardFor(cardId) }
    }

    /** Direct setter replacing blind cycling - one undo step and one save per pick. */
    fun setTelemetryDisplayMode(cardId: String, mode: TelemetryDisplayMode, secondarySensorId: String? = null) {
        editor.updateCard(cardId) { card ->
            if (card.type != HomeCardType.TELEMETRY) card
            else card.copy(displayMode = mode, secondarySensorId = secondarySensorId ?: card.secondarySensorId)
        }
    }

    /** Blank/null clears the override - the card falls back to its original [HomeCardState.title]. */
    fun setCardCustomTitle(cardId: String, title: String?) {
        val normalized = title?.takeIf { it.isNotBlank() }
        editor.updateCard(cardId) { it.copy(customTitle = normalized) }
    }

    fun setCardValueOverlay(cardId: String, enabled: Boolean) {
        editor.updateCard(cardId) { it.copy(showValueOverlay = enabled) }
    }

    /** The default cards fill in as their sensors start reporting; no undo step, like the canvas. */
    private fun ensureDefaultCardsExist(sensors: List<TelemetrySensor>) {
        val enabled = editor.layout.value.enabled
        DEFAULT_CARD_IDS.forEach { requiredId ->
            if (requiredId in enabled && sensors.any { it.id == requiredId }) {
                newCardFor(requiredId)?.let(editor::addIfMissing)
            }
        }
    }

    private fun newCardFor(cardId: String): HomeCardState? {
        val sensor = selectSensor(cardId, _telemetrySensors.value) ?: return null
        return HomeCardState(
            id = cardId,
            title = sensor.name,
            type = HomeCardType.TELEMETRY,
            sensorId = sensor.id,
            displayMode = TelemetryDisplayMode.AUTO
        )
    }

    private fun updateTelemetryHistory(sensors: List<TelemetrySensor>) {
        _telemetryHistory.update { current -> pruneAndAppendTelemetryHistory(current, sensors) }
    }

    /**
     * Reads the saved grid, migrating a free-form canvas save (schema 0/1) once and writing the
     * result straight back, so the migration never runs twice and an older save is never left
     * behind for the next read (RemEx-wqo7a.7).
     */
    private fun loadSavedHomeLayout() {
        viewModelScope.launch {
            val savedLayout = settingsManager.homeLayoutJsonFlow.first()
            val enabledCardsJson = settingsManager.homeEnabledCardsJsonFlow.first()

            // Null when never saved; an empty set is Clear all, not "use the defaults" (review R4).
            val savedEnabled = SensorGridMigration.parseSavedEnabled(enabledCardsJson)
            val migratedEnabled = savedEnabled?.let(SensorGridMigration::migrateEnabled)

            val decoded =
                if (savedLayout.isBlank()) null
                else runCatching { SensorLayoutCodec.decode(savedLayout) }
                    .onFailure { Log.w("DashboardVM", "Saved Sensors layout is unreadable; using the defaults", it) }
                    .getOrNull()
            val cards = decoded?.let(SensorLayoutCodec::toCards).orEmpty()

            if (cards.isEmpty() && migratedEnabled == null) return@launch
            val layout =
                SensorLayout(
                    cards = cards.ifEmpty { editor.layout.value.cards },
                    enabled = migratedEnabled ?: editor.layout.value.enabled
                )
            editor.load(layout)
            val needsRewrite =
                (decoded != null && decoded.schemaVersion < SensorLayoutCodec.SCHEMA_VERSION) ||
                    (savedEnabled != null && migratedEnabled != savedEnabled)
            if (needsRewrite) persistHomeLayout(layout)
        }
    }

    private fun persistHomeLayout(layout: SensorLayout) {
        viewModelScope.launch {
            val enabledArray = JSONArray()
            layout.enabled.forEach { enabledArray.put(it) }
            settingsManager.saveHomeLayout(SensorLayoutCodec.encode(layout.cards))
            settingsManager.saveHomeEnabledCards(enabledArray.toString())
        }
    }

    private companion object {
        fun defaultCards(): List<HomeCardState> {
            val titles = listOf("CPU", "GPU", "RAM", "RAM Total", "CPU Temp", "GPU Temp", "Network")
            return DEFAULT_CARD_IDS.zip(titles) { id, title ->
                HomeCardState(
                    id = id,
                    title = title,
                    type = HomeCardType.TELEMETRY,
                    sensorId = id,
                    displayMode = TelemetryDisplayMode.AUTO
                )
            }
        }
    }
}
