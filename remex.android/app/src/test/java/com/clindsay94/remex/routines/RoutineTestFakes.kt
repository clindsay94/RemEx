package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineStep
import com.clindsay94.remex.routines.model.RoutineStepResultPayload
import com.clindsay94.remex.routines.model.RoutineTrigger
import java.util.Base64
import java.util.UUID
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import org.json.JSONObject

// In-memory stand-ins for the Android side of the routine store and runner (RemEx-pp0rt.5).

internal const val HOST = "9f2c4be07a1d33e5"
internal const val OTHER_HOST = "0123456789abcdef"

internal fun uuid(): String = UUID.randomUUID().toString()

internal fun manualRoutine(vararg steps: RoutineStep, id: String = uuid(), enabled: Boolean = true, name: String = "Game time"): Routine =
    Routine(
        id = id,
        name = name,
        hostIdentity = HOST,
        enabled = enabled,
        trigger = RoutineTrigger(type = "manual"),
        steps = steps.toList(),
    )

internal fun pcIdleRoutine(id: String = uuid()): Routine =
    Routine(
        id = id,
        name = "Bedtime",
        hostIdentity = HOST,
        enabled = true,
        trigger = RoutineTrigger(type = "pc.idle", idleMinutes = 30, ignoreWhileMediaPlaying = true),
        steps = listOf(RoutineStep(type = "power", verb = "SLEEP")),
    )

internal fun lock() = RoutineStep(type = "power", verb = "LOCK")

internal fun shutdown() = RoutineStep(type = "power", verb = "SHUTDOWN")

internal fun delaySeconds(seconds: Int) = RoutineStep(type = "delay", seconds = seconds)

internal fun wake() = RoutineStep(type = "wake", mac = "0A:1B:2C:3D:4E:5F")

internal fun waitOnline(seconds: Int = 60) = RoutineStep(type = "waitOnline", timeoutSeconds = seconds)

internal fun notifyPhone() = RoutineStep(type = "notify", target = "phone", title = "PC ready", body = "Steam is starting.")

internal class FakeKeyValueStore : RoutineKeyValueStore {
    val map = LinkedHashMap<String, String>()
    var failWrites = false

    override suspend fun get(key: String): String? = map[key]

    override suspend fun getAll(): Map<String, String> = LinkedHashMap(map)

    override suspend fun put(key: String, value: String) {
        if (failWrites) throw java.io.IOException("disk full")
        map[key] = value
    }

    override suspend fun remove(key: String) {
        map.remove(key)
    }
}

/** Reversible and bound to its associated data, like an AEAD: the wrong AD does not open. */
internal class FakeCipher : RoutineCipher {
    override fun seal(plainText: String, associatedData: String): String =
        Base64.getEncoder().encodeToString("$associatedData\u0000$plainText".toByteArray(Charsets.UTF_8))

    override fun open(sealed: String, associatedData: String): String? {
        val text = runCatching { String(Base64.getDecoder().decode(sealed), Charsets.UTF_8) }.getOrNull() ?: return null
        val prefix = "$associatedData\u0000"
        return if (text.startsWith(prefix)) text.removePrefix(prefix) else null
    }
}

internal class FakeCipherSource(var keyLossAt: Long? = null) : RoutineCipherSource {
    val cipher = FakeCipher()

    override suspend fun cipher(): RoutineCipher = cipher

    override suspend fun pendingKeyLossAtUnixMs(): Long? = keyLossAt

    override suspend fun clearKeyLossMarker() {
        keyLossAt = null
    }
}

internal class FakeScheduler : RoutineRunScheduler {
    val enqueued = mutableListOf<RoutineRunTicket>()
    val cancelled = mutableListOf<String>()
    val active = mutableSetOf<String>()
    var refuse = false

    override fun enqueue(ticket: RoutineRunTicket): Boolean {
        if (refuse) return false
        enqueued += ticket
        active += ticket.routineId
        return true
    }

    override fun cancel(routineId: String) {
        cancelled += routineId
        active -= routineId
    }

    override suspend fun isActive(routineId: String): Boolean = routineId in active
}

internal class RecordingObserver : RoutineRunObserver {
    val progress = mutableListOf<Int?>()
    val countdowns = mutableListOf<Int>()
    val finished = mutableListOf<RoutineRun>()

    override fun onProgress(run: RoutineRun, routine: Routine, stepIndex: Int?) {
        progress += stepIndex
    }

    override fun onCountdown(run: RoutineRun, routine: Routine, stepIndex: Int, endsAtUnixMs: Long) {
        countdowns += stepIndex
    }

    override fun onFinished(run: RoutineRun, routine: Routine?) {
        finished += run
    }
}

internal class FakeClock(var now: Long = 1_790_000_000_000L, var elapsed: () -> Long = { 0L }) : RoutineClock {
    override fun nowUnixMs(): Long = now

    override fun elapsedRealtimeMs(): Long = elapsed()
}

/**
 * The PC side of the control connection. [onRequest] decides how the PC answers each
 * `routine_step_request` (null = never answers); everything sent is kept in [sent].
 */
internal class FakeHostLink : RoutineHostLink {
    private val _authenticated = MutableStateFlow(true)
    override val authenticated: StateFlow<Boolean> = _authenticated
    val results = MutableSharedFlow<RoutineStepResultPayload>(extraBufferCapacity = 64)
    override val stepResults = results

    var selected: String? = HOST
    var connectedHost: String? = HOST
    var paired = true
    var supports = true
    var connectSucceeds = true
    var connectAttempts = 0
    val sent = mutableListOf<JSONObject>()
    var onRequest: (JSONObject) -> RoutineStepResultPayload? = { request -> succeeded(request) }
    var sendSucceeds = true

    fun setAuthenticated(value: Boolean) {
        _authenticated.value = value
    }

    override suspend fun authenticatedHostIdentity(): String? = if (_authenticated.value) connectedHost else null

    override suspend fun selectedHostIdentity(): String? = selected

    override suspend fun isPaired(hostIdentity: String): Boolean = paired

    /** Thrown by [displayName], like a DataStore IOException reading the known-host records. */
    var displayNameError: Exception? = null

    /** How long one connect attempt takes before it reports back. */
    var connectDelayMs = 0L

    override suspend fun displayName(hostIdentity: String): String? {
        displayNameError?.let { throw it }
        return "Gaming PC"
    }

    override suspend fun startOneShotConnect(): Boolean {
        connectAttempts++
        if (connectDelayMs > 0) kotlinx.coroutines.delay(connectDelayMs)
        if (connectSucceeds) _authenticated.value = true
        return true
    }

    override suspend fun supportsRoutines(hostIdentity: String): Boolean = supports

    override fun send(json: String): Boolean {
        val envelope = JSONObject(json)
        sent += envelope
        if (!sendSucceeds) return false
        if (envelope.getString("type") == "routine_step_request") {
            onRequest(envelope.getJSONObject("routineStepRequest"))?.let { results.tryEmit(it) }
        }
        return true
    }

    fun requests(): List<JSONObject> = sent.filter { it.getString("type") == "routine_step_request" }.map { it.getJSONObject("routineStepRequest") }

    fun cancels(): List<JSONObject> = sent.filter { it.getString("type") == "routine_cancel" }.map { it.getJSONObject("routineCancel") }

    companion object {
        fun result(request: JSONObject, outcome: String, reason: String = "ok", cancelledBy: String? = null) =
            RoutineStepResultPayload(
                runId = request.getString("runId"),
                stepIndex = request.getInt("stepIndex"),
                outcome = outcome,
                reasonCode = reason,
                countdownShown = true,
                cancelledBy = cancelledBy,
            )

        fun succeeded(request: JSONObject) = result(request, "succeeded")
    }
}

internal class FakePhone : RoutinePhone {
    var localNetwork = true
    var wakeSends = true
    var wakeAttempts = 0

    /** Overrides [wakeSends] per attempt (1-based) when set; may suspend to model a slow send. */
    var wakeBehaviour: (suspend (attempt: Int) -> Boolean)? = null
    var notificationsAllowed = true
    val messages = mutableListOf<Pair<String, String>>()
    var away = false

    override fun hasLocalNetworkPermission(): Boolean = localNetwork

    override suspend fun sendWakePacket(mac: String, broadcastIp: String, port: Int): Boolean {
        wakeAttempts++
        return wakeBehaviour?.invoke(wakeAttempts) ?: wakeSends
    }

    override fun postMessage(run: RoutineRun, stepIndex: Int, title: String, body: String): Boolean {
        if (!notificationsAllowed) return false
        messages += title to body
        return true
    }

    override fun isBackgroundRestricted(): Boolean = false

    override suspend fun isAwayFromHome(): Boolean = away
}

/** A repository over in-memory stores, for tests that exercise the real store logic. */
internal class RepositoryHarness(
    val clock: FakeClock = FakeClock(),
    val cipherSource: FakeCipherSource = FakeCipherSource(),
) {
    val docKv = FakeKeyValueStore()
    val historyKv = FakeKeyValueStore()
    val scheduler = FakeScheduler()
    val observer = RecordingObserver()
    val controls = RoutineRunControls()

    fun repository(): RoutineRepository =
        RoutineRepository(
            documents = RoutineDocumentStore(docKv, cipherSource),
            historyStore = RoutineHistoryStore(historyKv, cipherSource),
            cipherSource = cipherSource,
            scheduler = scheduler,
            observer = observer,
            clock = clock,
            controls = controls,
        )
}
