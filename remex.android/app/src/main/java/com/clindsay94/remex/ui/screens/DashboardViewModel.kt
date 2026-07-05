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

enum class HomeCardType { PC_STATUS, TELEMETRY, WAKE_ON_LAN }
enum class TelemetryDisplayMode { VALUE, GAUGE, LINE, BAR, AREA, CIRCLE_GAUGE }

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
    val unit: String
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
    val displayMode: TelemetryDisplayMode = TelemetryDisplayMode.GAUGE,
    val pinned: Boolean = false
)

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

    private val _enabledCardIds = MutableStateFlow(setOf("pc_status", "wake_pc", "sensor:cpu", "sensor:gpu", "sensor:ram"))
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

    /** Toggle a card's pinned state — pinned cards can't be dragged or resized. */
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

    val cardCornerRadius = settingsManager.cardCornerRadiusFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 20)

    val cardOpacity = settingsManager.cardOpacityFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 1.0f)

    val pcCardShapePreset = settingsManager.pcCardShapePresetFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 0f)

    val telemetryCardShapePreset = settingsManager.telemetryCardShapePresetFlow
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5000), 0f)

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
                        RemexCoreClient.WakePc(mac, broadcast, 9).getOrNull()
                        Log.d("DashboardVM", "Wake-on-LAN packet sent to $mac via $broadcast:9")
                        _wakeStatus.tryEmit(getApplication<Application>().getString(R.string.wake_pc_sent))
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

    fun moveCard(cardId: String, deltaXDp: Float, deltaYDp: Float) {
        _homeCards.update { cards ->
            cards.map { card ->
                if (card.id == cardId) {
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

    fun cycleTelemetryDisplayMode(cardId: String) {
        beginInteraction()
        _homeCards.update { cards ->
            cards.map { card ->
                if (card.id == cardId && card.type == HomeCardType.TELEMETRY) {
                    val next = when (card.displayMode) {
                        TelemetryDisplayMode.VALUE -> TelemetryDisplayMode.GAUGE
                        TelemetryDisplayMode.GAUGE -> TelemetryDisplayMode.LINE
                        TelemetryDisplayMode.LINE -> TelemetryDisplayMode.BAR
                        TelemetryDisplayMode.BAR -> TelemetryDisplayMode.AREA
                        TelemetryDisplayMode.AREA -> TelemetryDisplayMode.CIRCLE_GAUGE
                        TelemetryDisplayMode.CIRCLE_GAUGE -> TelemetryDisplayMode.VALUE
                    }
                    card.copy(displayMode = next)
                } else {
                    card
                }
            }
        }
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
        val telemetryDefaults = listOf("sensor:cpu", "sensor:gpu", "sensor:ram")
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
                val sensor = _telemetrySensors.value.firstOrNull { it.id == cardId }
                    ?: return
                
                val defaultMode = when {
                    sensor.unit == "%" -> TelemetryDisplayMode.AREA
                    sensor.unit.contains("°") -> TelemetryDisplayMode.LINE
                    else -> TelemetryDisplayMode.BAR
                }

                HomeCardState(
                    id = cardId,
                    title = sensor.name,
                    type = HomeCardType.TELEMETRY,
                    sensorId = sensor.id,
                    xDp = 12f + nextOffset,
                    yDp = 12f + nextOffset,
                    widthDp = 170f,
                    heightDp = 150f,
                    displayMode = defaultMode
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

            val normalized = normalizeSensorId(name, category)
            parsed += TelemetrySensor(
                id = normalized,
                name = name,
                category = category,
                value = value,
                unit = unit
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

            val cards = mutableListOf<HomeCardState>()
            val array = JSONArray(savedLayout)
            for (i in 0 until array.length()) {
                val obj = array.optJSONObject(i) ?: continue
                val type = when (obj.optString("type")) {
                    "PC_STATUS" -> HomeCardType.PC_STATUS
                    "WAKE_ON_LAN" -> HomeCardType.WAKE_ON_LAN
                    else -> HomeCardType.TELEMETRY
                }
                val mode = when (obj.optString("displayMode")) {
                    "VALUE" -> TelemetryDisplayMode.VALUE
                    "LINE" -> TelemetryDisplayMode.LINE
                    "BAR" -> TelemetryDisplayMode.BAR
                    "AREA" -> TelemetryDisplayMode.AREA
                    "CIRCLE_GAUGE" -> TelemetryDisplayMode.CIRCLE_GAUGE
                    else -> TelemetryDisplayMode.GAUGE
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
                    pinned = obj.optBoolean("pinned", false)
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
                    put("pinned", card.pinned)
                }
                layoutArray.put(obj)
            }

            val enabledArray = JSONArray()
            _enabledCardIds.value.forEach { enabledArray.put(it) }

            settingsManager.saveHomeLayout(layoutArray.toString())
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
                    displayMode = TelemetryDisplayMode.AREA
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
                    displayMode = TelemetryDisplayMode.AREA
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
                    displayMode = TelemetryDisplayMode.AREA
                )
            )
        }
    }
}
