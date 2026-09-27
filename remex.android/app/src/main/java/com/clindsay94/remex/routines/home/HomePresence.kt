package com.clindsay94.remex.routines.home

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.OutOfQuotaPolicy
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.routines.RoutineLog
import com.clindsay94.remex.routines.RoutineRepository
import com.clindsay94.remex.routines.Routines
import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineLimits
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import com.clindsay94.remex.routines.model.RoutineRunSources
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import com.clindsay94.remex.security.HostIdentity
import com.clindsay94.remex.security.PinnedHostStore
import java.io.File
import java.util.UUID
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** What the capture sheet shows (spec §1.4, A10): the capture API's result and the PC it was made with. */
data class HomeCaptureProbe(val result: HomeCaptureResult, val pcIdentity: String?)

/**
 * Home presence on the phone (routines spec §8.3.1; RemEx-pp0rt.8): capture, the presence
 * evaluation that fires `home.arrive` / `home.leave`, the event-source registrations, drift and
 * `reachableAway`.
 *
 * Budget (§12): with no home routine there is no registration and no work. With one, a PendingIntent
 * network callback wakes RemEx only on network edges, and the 15-minute check runs only while HOME
 * with a leave routine. No location permission, no exact alarm, no foreground service (T16).
 */
object HomePresence {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val evaluation = Mutex()

    private val _drift = MutableStateFlow(false)

    /** `home_fingerprint_stale`: authenticated to the home's PC on a LAN that no longer matches home. */
    val drift: StateFlow<Boolean> = _drift.asStateFlow()

    @Volatile private var started = false

    /**
     * Process start (RemexApplication): re-registers what the routines need, since a process start
     * after an update or a crash may be the first chance to, and watches authentications for drift and
     * `reachableAway`. Cheap when nothing is armed: no store is read unless a home routine exists.
     */
    fun onProcessStart(context: Context) {
        val app = context.applicationContext
        if (started) return
        started = true
        scope.launch {
            if (isArmed(app)) sync(app)
            watchAuthentications(app)
        }
    }

    /** Keeps registrations in step with every routine and home edit (called once per process by the routine graph). */
    fun watch(context: Context, repository: RoutineRepository) {
        val app = context.applicationContext
        scope.launch {
            combine(repository.routines, repository.home) { items, home ->
                val homeRoutines = items.map { it.routine }.filter { isHomeRoutine(it) && it.enabled }
                Triple(home?.id to home?.presence?.state, homeRoutines.map { it.id to it.trigger }, repository.status.value.health)
            }.distinctUntilChanged().collect { sync(app) }
        }
    }

    // ── Capture (spec §1.4) ──

    suspend fun probe(context: Context): HomeCaptureProbe {
        val app = context.applicationContext
        val connection = RemexClientManager.authenticatedConnection.value
        val identity = connection?.host?.let { identityOf(app, it) }
        val address = HomeNetworks.literal(connection?.host)
        return HomeCaptureProbe(HomeCapture.evaluate(HomeNetworks.active(app), address, connection != null), identity)
    }

    /**
     * Stores [facts] as home. Never silent: only the capture sheet's "Use this network as home" calls
     * it. An existing home keeps its id, so the routines that use it stay valid. The phone is home now,
     * so presence starts HOME without an arrive (T20).
     */
    suspend fun saveHome(context: Context, facts: HomeFacts, pcIdentity: String): Boolean {
        val app = context.applicationContext
        val tokens = HomeTokenizer(Routines.secrets(app).homeKey()).tokens(facts)
        val saved =
            Routines.repository(app).updateHome { old ->
                Home(
                    id = old?.id ?: UUID.randomUUID().toString(),
                    capturedWithHostIdentity = pcIdentity,
                    capturedAtUnixMs = System.currentTimeMillis(),
                    facts = facts,
                    tokens = tokens,
                    presence = PresenceRecord(PresenceState.HOME),
                )
            }
        if (saved) _drift.value = false
        sync(app)
        return saved
    }

    suspend fun forgetHome(context: Context): Boolean {
        val app = context.applicationContext
        val done = Routines.repository(app).updateHome { null }
        _drift.value = false
        sync(app)
        return done
    }

    // ── Evaluation (spec §8.3.1 Arrive / Leave / Flap) ──

    /**
     * Reads every current network, steps the presence state machine and starts what it says. One at
     * a time: two network events in a row must not both see "away" and both fire the leave.
     */
    suspend fun evaluate(context: Context, reason: String) {
        val app = context.applicationContext
        evaluation.withLock {
            val repository = Routines.repository(app)
            repository.load()
            val home = repository.home.value
            if (home == null) {
                sync(app)
                return
            }
            val routines = repository.routines.value.map { it.routine }.filter { it.enabled && it.trigger?.homeId == home.id }
            val arrive = routines.filter { it.trigger?.type == RoutineTriggerTypes.HOME_ARRIVE }.mapNotNull { it.id }
            val leave =
                routines.filter { it.trigger?.type == RoutineTriggerTypes.HOME_LEAVE }
                    .mapNotNull { r -> r.id?.let { it to (r.trigger?.leaveDebounceSeconds ?: RoutineLimits.DEFAULT_LEAVE_DEBOUNCE_SECONDS) } }
                    .toMap()
            val networks = HomeNetworks.current(app)
            val tokenizer = HomeTokenizer(Routines.secrets(app).homeKey())
            val matches = NetworkFingerprintMatcher.anyMatches(home.tokens, networks, tokenizer)
            val now = System.currentTimeMillis()
            val step = PresenceStateMachine.step(home.presence, matches, now, leave, RoutineLimits.DEFAULT_LEAVE_DEBOUNCE_SECONDS)
            if (step.presence != home.presence) {
                repository.updateHome { current -> current?.takeIf { it.id == home.id }?.copy(presence = step.presence) }
            }
            RoutineLog.d("Home presence ($reason): ${home.presence.state} -> ${step.presence.state}, ${step.events.size} event(s).")
            for (event in step.events) {
                when (event) {
                    is PresenceEvent.Arrive -> arrive.forEach { start(repository, it, RoutineRunSources.HOME_ARRIVE, event.suppressed) }
                    is PresenceEvent.Leave -> start(repository, event.routineId, RoutineRunSources.HOME_LEAVE, event.suppressed)
                }
            }
            scheduleRecheck(app, step.recheckAtUnixMs?.let { it - now })
        }
        sync(app)
    }

    private suspend fun start(repository: RoutineRepository, routineId: String, source: String, suppressed: Boolean) {
        if (suppressed) {
            repository.recordRefusal(routineId, source, RoutineReasonCodes.FLAP_SUPPRESSED)
        } else {
            repository.run(routineId, source)
        }
    }

    /** After a reboot nothing is known: the next evaluation learns the state without firing (spec L9). */
    internal suspend fun forgetPresence(context: Context) {
        Routines.repository(context.applicationContext).updateHome { it?.copy(presence = PresenceRecord()) }
    }

    /** The runner's `isAwayFromHome` (`pc_unreachable` vs `pc_unreachable_away`): a home exists and no network matches it. */
    suspend fun isAwayFromHome(context: Context): Boolean {
        val app = context.applicationContext
        val home = Routines.repository(app).home.value ?: return false
        return !NetworkFingerprintMatcher.anyMatches(home.tokens, HomeNetworks.current(app), HomeTokenizer(Routines.secrets(app).homeKey()))
    }

    // ── Drift and reachableAway (spec §8.3.1) ──

    private suspend fun watchAuthentications(app: Context) {
        RemexClientManager.authenticatedConnection
            .filterNotNull()
            .distinctUntilChanged { a, b -> a.epoch == b.epoch }
            .collect { connection ->
                try {
                    onAuthenticated(app, connection.host)
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    RoutineLog.w("Checking home after connecting failed.", e)
                }
            }
    }

    private suspend fun onAuthenticated(app: Context, host: String?) {
        val identity = host?.let { identityOf(app, it) } ?: return
        val repository = Routines.repository(app)
        repository.load()
        val home = repository.home.value ?: return
        val active = HomeNetworks.active(app)
        val tokenizer = HomeTokenizer(Routines.secrets(app).homeKey())
        val matches = active != null && NetworkFingerprintMatcher.match(home.tokens, tokenizer.tokens(HomeFacts.of(active)), active.qualifies).matches
        if (!matches) repository.markReachableAway(identity)
        _drift.value = identity == home.capturedWithHostIdentity && HomeCapture.isDrift(active, HomeNetworks.literal(host), matches)
    }

    // ── Registrations (spec §8.3.1 Event sources) ──

    /** Registers or removes every event source to match the routines now. Idempotent. */
    suspend fun sync(context: Context) {
        val app = context.applicationContext
        try {
            val repository = Routines.repository(app)
            repository.load()
            val home = repository.home.value
            val homeRoutines = repository.routines.value.map { it.routine }.filter { it.enabled && isHomeRoutine(it) && it.trigger?.homeId == home?.id }
            val plan =
                PresenceRegistrationPlan.of(
                    homeSet = home != null,
                    enabledHomeRoutines = homeRoutines.size,
                    enabledLeaveRoutines = homeRoutines.count { it.trigger?.type == RoutineTriggerTypes.HOME_LEAVE },
                    state = home?.presence?.state,
                )
            PresenceRegistrar.apply(plan, AndroidPresenceRegistrations(app))
            setArmed(app, plan.networkCallback)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            RoutineLog.e("Updating home presence registrations failed.", e)
        }
    }

    internal fun enqueueEvaluation(context: Context, reason: String) {
        try {
            val request =
                OneTimeWorkRequestBuilder<RoutinePresenceWorker>()
                    .setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST)
                    .setInputData(androidx.work.Data.Builder().putString(RoutinePresenceWorker.KEY_REASON, reason).build())
                    .build()
            WorkManager.getInstance(context.applicationContext).enqueueUniqueWork(NET_EVAL_WORK, ExistingWorkPolicy.REPLACE, request)
        } catch (e: IllegalStateException) {
            RoutineLog.e("WorkManager is not available.", e)
        }
    }

    /** The one-shot re-check at the settle or debounce deadline (spec §8.3.1). */
    private fun scheduleRecheck(context: Context, delayMs: Long?) {
        try {
            val manager = WorkManager.getInstance(context)
            if (delayMs == null) {
                manager.cancelUniqueWork(RECHECK_WORK)
                return
            }
            val request =
                OneTimeWorkRequestBuilder<RoutinePresenceWorker>()
                    .setInitialDelay(delayMs.coerceAtLeast(0) + RECHECK_MARGIN_MS, TimeUnit.MILLISECONDS)
                    .setInputData(androidx.work.Data.Builder().putString(RoutinePresenceWorker.KEY_REASON, "recheck").build())
                    .build()
            manager.enqueueUniqueWork(RECHECK_WORK, ExistingWorkPolicy.REPLACE, request)
        } catch (e: IllegalStateException) {
            RoutineLog.e("WorkManager is not available.", e)
        }
    }

    private fun isHomeRoutine(routine: Routine): Boolean =
        routine.trigger?.type == RoutineTriggerTypes.HOME_ARRIVE || routine.trigger?.type == RoutineTriggerTypes.HOME_LEAVE

    private suspend fun identityOf(context: Context, host: String): String? =
        try {
            HostIdentity.keyFor(PinnedHostStore.getPin(context, host))
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            null
        }

    // A marker in no-backup storage (never restored to another phone) that says "home routines exist",
    // so a process start with none reads no store at all.
    private fun armedFile(context: Context) = File(context.noBackupFilesDir, ARMED_FILE)

    private fun isArmed(context: Context): Boolean = armedFile(context).exists()

    private fun setArmed(context: Context, armed: Boolean) {
        val file = armedFile(context)
        runCatching { if (armed) file.createNewFile() else file.delete() }
    }

    const val NET_EVAL_WORK = "routine-net-eval"
    const val RECHECK_WORK = "routine-presence-recheck"
    const val PERIODIC_WORK = "routine-presence-check"
    private const val RECHECK_MARGIN_MS = 500L
    private const val ARMED_FILE = "routines_presence_armed"
}

/** Every presence evaluation runs here: network events, re-checks and the 15-minute fallback. */
class RoutinePresenceWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        return try {
            HomePresence.evaluate(applicationContext, inputData.getString(KEY_REASON) ?: "periodic")
            Result.success()
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            RoutineLog.e("Home presence evaluation failed.", e)
            Result.failure()
        }
    }

    companion object {
        const val KEY_REASON = "reason"
    }
}

/**
 * The network callback's PendingIntent target and the reboot and update hook (spec §8.3.1). NOT
 * exported: the PendingIntent is RemEx's own, and BOOT_COMPLETED and MY_PACKAGE_REPLACED come from
 * the system, which reaches a non-exported receiver.
 *
 * **Registrations die on reboot and on update** (REGRESSION-GUARDS): the platform drops every network
 * callback and WorkManager may have to reschedule, so both broadcasts re-register everything.
 */
class RoutineNetworkReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val action = intent.action ?: return
        if (action !in HANDLED) return
        val app = context.applicationContext
        val pending = goAsync()
        receiverScope.launch {
            try {
                if (action == Intent.ACTION_BOOT_COMPLETED) HomePresence.forgetPresence(app)
                HomePresence.sync(app)
                HomePresence.enqueueEvaluation(app, if (action == ACTION_NETWORK) "network" else "restart")
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                RoutineLog.e("Handling a home presence broadcast failed.", e)
            } finally {
                pending.finish()
            }
        }
    }

    companion object {
        const val ACTION_NETWORK = "com.clindsay94.remex.routines.NETWORK_AVAILABLE"
        private val HANDLED = setOf(ACTION_NETWORK, Intent.ACTION_BOOT_COMPLETED, Intent.ACTION_MY_PACKAGE_REPLACED)
        private val receiverScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    }
}

/** [PresenceRegistrationPort] on ConnectivityManager and WorkManager. */
internal class AndroidPresenceRegistrations(context: Context) : PresenceRegistrationPort {
    private val app = context.applicationContext
    private val connectivity get() = app.getSystemService(ConnectivityManager::class.java)

    // Mutable because the system adds the network to it; explicit, so only RemEx receives it. The
    // same Intent every time, so re-registering replaces rather than adds (Intent.filterEquals).
    private fun pendingIntent(): PendingIntent =
        PendingIntent.getBroadcast(
            app,
            0,
            Intent(app, RoutineNetworkReceiver::class.java).setAction(RoutineNetworkReceiver.ACTION_NETWORK),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE,
        )

    override fun registerPendingIntentCallback() {
        runCatching { connectivity?.registerNetworkCallback(HomeNetworks.request(), pendingIntent()) }
            .onFailure { RoutineLog.w("Registering the home network callback failed.", it) }
    }

    override fun unregisterPendingIntentCallback() {
        // Unregistering one that was never registered throws; that is the idle case, not an error.
        runCatching { connectivity?.unregisterNetworkCallback(pendingIntent()) }
    }

    override fun registerInProcessCallback() {
        synchronized(InProcess) {
            if (InProcess.registered) return
            runCatching { connectivity?.registerNetworkCallback(HomeNetworks.request(), InProcess.callback(app)) }
                .onSuccess { InProcess.registered = true }
                .onFailure { RoutineLog.w("Registering the in-process network callback failed.", it) }
        }
    }

    override fun unregisterInProcessCallback() {
        synchronized(InProcess) {
            if (!InProcess.registered) return
            runCatching { connectivity?.unregisterNetworkCallback(InProcess.callback(app)) }
            InProcess.registered = false
        }
    }

    override fun schedulePeriodicCheck() {
        runCatching {
            val request = PeriodicWorkRequestBuilder<RoutinePresenceWorker>(15, TimeUnit.MINUTES).build()
            WorkManager.getInstance(app).enqueueUniquePeriodicWork(HomePresence.PERIODIC_WORK, ExistingPeriodicWorkPolicy.KEEP, request)
        }.onFailure { RoutineLog.w("Scheduling the leave-home check failed.", it) }
    }

    override fun cancelPeriodicCheck() {
        runCatching { WorkManager.getInstance(app).cancelUniqueWork(HomePresence.PERIODIC_WORK) }
    }

    /** The in-process callback: a fast leave (`onLost`) while RemEx is running (spec §8.3.1). */
    private object InProcess {
        var registered = false
        private var instance: ConnectivityManager.NetworkCallback? = null

        fun callback(app: Context): ConnectivityManager.NetworkCallback =
            instance ?: object : ConnectivityManager.NetworkCallback() {
                override fun onAvailable(network: Network) = HomePresence.enqueueEvaluation(app, "available")

                override fun onLost(network: Network) = HomePresence.enqueueEvaluation(app, "lost")

                override fun onLinkPropertiesChanged(network: Network, linkProperties: LinkProperties) =
                    HomePresence.enqueueEvaluation(app, "link")
            }.also { instance = it }
    }
}
