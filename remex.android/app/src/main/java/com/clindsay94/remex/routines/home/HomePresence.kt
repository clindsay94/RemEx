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
import com.clindsay94.remex.routines.model.RoutineRunSources
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import com.clindsay94.remex.security.HostIdentity
import com.clindsay94.remex.security.PinnedHostStore
import java.util.UUID
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.withContext
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

    private val started = AtomicBoolean(false)
    private val watchingAuth = AtomicBoolean(false)
    private val planLock = Mutex()

    /**
     * Process start (RemexApplication). **Zero cost when nothing is armed (§12):** the armed flag is a
     * plain pref, so a phone with no home routine reads no store, touches no native core and starts no
     * watcher here. When armed, it re-registers (a process start may follow a force stop that dropped
     * the PendingIntent) and watches authentications for drift and `reachableAway`.
     */
    fun onProcessStart(context: Context) {
        val app = context.applicationContext
        if (!started.compareAndSet(false, true)) return
        if (!PresencePlanStore.isArmed(app)) return
        scope.launch {
            sync(app, force = true)
            startAuthWatch(app)
        }
    }

    /**
     * Keeps registrations in step with every routine and home edit (called once per process by the
     * routine graph, which only exists once routines are in use). Each emission is a plan check:
     * [PresenceRegistrar.apply] does nothing when the plan did not change.
     */
    fun watch(context: Context, repository: RoutineRepository) {
        val app = context.applicationContext
        startAuthWatch(app)
        scope.launch {
            combine(repository.routines, repository.home) { items, home ->
                val homeRoutines = items.map { it.routine }.filter { isHomeRoutine(it) && it.enabled }
                Triple(home?.id to home?.presence?.state, homeRoutines.map { it.id to it.trigger }, repository.status.value.health)
            }.distinctUntilChanged().collect { sync(app) }
        }
    }

    private fun startAuthWatch(app: Context) {
        if (!watchingAuth.compareAndSet(false, true)) return
        scope.launch { watchAuthentications(app) }
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
     *
     * It never registers anything (the registration plan follows the store through [watch]), so the
     * PendingIntent's own "available" broadcast cannot feed back into another registration. The
     * starts it decides are written with the transition and made under NonCancellable
     * ([PresenceFiring]); a start still owed from an interrupted run is replayed here first.
     */
    suspend fun evaluate(context: Context, reason: String) {
        val app = context.applicationContext
        evaluation.withLock {
            val repository = Routines.repository(app)
            repository.load()
            val home = repository.home.value ?: return
            val now = System.currentTimeMillis()
            PresenceFiring.fireOwed(repository, home.id, now)
            val routines = repository.routines.value.map { it.routine }.filter { it.enabled && it.trigger?.homeId == home.id }
            val arrive = routines.filter { it.trigger?.type == RoutineTriggerTypes.HOME_ARRIVE }.mapNotNull { it.id }
            val leave =
                routines.filter { it.trigger?.type == RoutineTriggerTypes.HOME_LEAVE }
                    .mapNotNull { r -> r.id?.let { it to (r.trigger?.leaveDebounceSeconds ?: RoutineLimits.DEFAULT_LEAVE_DEBOUNCE_SECONDS) } }
                    .toMap()
            val networks = HomeNetworks.current(app)
            val tokenizer = HomeTokenizer(Routines.secrets(app).homeKey())
            val verdicts = networks.map { it to NetworkFingerprintMatcher.match(home.tokens, tokenizer.tokens(HomeFacts.of(it)), it.qualifies).matches }
            val matches = verdicts.any { it.second }
            val foreign = verdicts.any { (network, match) -> network.qualifies && !match }
            val current = repository.home.value?.takeIf { it.id == home.id }?.presence ?: return
            val step = PresenceStateMachine.step(current, matches, now, leave, RoutineLimits.DEFAULT_LEAVE_DEBOUNCE_SECONDS, foreign)
            val owed = PendingFires.of(step.events, arrive, RoutineRunSources.HOME_ARRIVE, RoutineRunSources.HOME_LEAVE, now)
            val next = step.presence.copy(pendingFire = PendingFires.merge(current.pendingFire, owed, now))
            if (next != current && !PresenceFiring.record(repository, home.id, next)) return
            RoutineLog.d("Home presence ($reason): ${current.state} -> ${next.state}, ${owed.size} start(s).")
            PresenceFiring.fireOwed(repository, home.id, now)
            withContext(NonCancellable) { scheduleRecheck(app, step.recheckAtUnixMs?.let { it - now }) }
        }
    }

    /**
     * After a reboot nothing is known: the next evaluation learns the state without firing (spec L9).
     * Starts still owed from before the reboot are kept; they are dropped once too old.
     */
    internal suspend fun forgetPresence(context: Context) {
        Routines.repository(context.applicationContext).updateHome { it?.copy(presence = PresenceRecord(pendingFire = it.presence.pendingFire)) }
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

    /**
     * Brings the registrations in line with the routines now. Does nothing when the plan is the one
     * last applied, unless [force] (process start, reboot, update: a registration may be gone without
     * a trace). Never called from the network broadcast or after an evaluation (REGRESSION-GUARDS).
     */
    suspend fun sync(context: Context, force: Boolean = false) {
        val app = context.applicationContext
        try {
            planLock.withLock {
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
                val port = AndroidPresenceRegistrations(app)
                val previous = PresencePlanStore.load(app)
                // Stored BEFORE registering: the registration's own "available" broadcast reads the
                // armed flag, and must find it on, or it would drop the registration as stale.
                PresencePlanStore.save(app, plan)
                val applied = PresenceRegistrar.apply(plan, previous, force, port)
                // The in-process callback lives and dies with this process, whatever the stored plan says.
                if (applied.networkCallback) port.registerInProcessCallback() else port.unregisterInProcessCallback()
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            RoutineLog.e("Updating home presence registrations failed.", e)
        }
    }

    /**
     * Queues one evaluation. APPEND_OR_REPLACE, never REPLACE: a second network event must not cancel
     * the evaluation already running (with REPLACE, a burst of events kept cancelling each other and
     * presence never moved).
     */
    internal fun enqueueEvaluation(context: Context, reason: String) {
        try {
            val request =
                OneTimeWorkRequestBuilder<RoutinePresenceWorker>()
                    .setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST)
                    .setInputData(androidx.work.Data.Builder().putString(RoutinePresenceWorker.KEY_REASON, reason).build())
                    .build()
            WorkManager.getInstance(context.applicationContext).enqueueUniqueWork(NET_EVAL_WORK, EVAL_POLICY, request)
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

    const val NET_EVAL_WORK = "routine-net-eval"
    const val RECHECK_WORK = "routine-presence-recheck"
    const val PERIODIC_WORK = "routine-presence-check"
    val EVAL_POLICY = ExistingWorkPolicy.APPEND_OR_REPLACE
    private const val RECHECK_MARGIN_MS = 500L
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
        val event =
            when (intent.action) {
                ACTION_NETWORK -> PresenceEventRouter.NETWORK
                Intent.ACTION_BOOT_COMPLETED -> PresenceEventRouter.BOOT
                Intent.ACTION_MY_PACKAGE_REPLACED -> PresenceEventRouter.PACKAGE_REPLACED
                else -> return
            }
        val app = context.applicationContext
        // The armed flag is a plain pref: nothing below runs, and no store is read, when it is off (§12).
        val actions = PresenceEventRouter.actionsFor(event, PresencePlanStore.isArmed(app))
        if (actions.isEmpty()) return
        if (PresenceAction.UNREGISTER_STALE in actions) {
            AndroidPresenceRegistrations(app).unregisterPendingIntentCallback()
            return
        }
        // The network broadcast only evaluates: re-registering here is what made the loop.
        if (actions == listOf(PresenceAction.EVALUATE)) {
            HomePresence.enqueueEvaluation(app, "network")
            return
        }
        val pending = goAsync()
        receiverScope.launch {
            try {
                if (PresenceAction.FORGET_PRESENCE in actions) HomePresence.forgetPresence(app)
                if (PresenceAction.SYNC_FORCED in actions) HomePresence.sync(app, force = true)
                if (PresenceAction.EVALUATE in actions) HomePresence.enqueueEvaluation(app, "restart")
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
