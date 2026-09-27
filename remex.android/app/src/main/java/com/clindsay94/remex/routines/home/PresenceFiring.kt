package com.clindsay94.remex.routines.home

import android.content.Context
import com.clindsay94.remex.routines.RoutineRepository
import com.clindsay94.remex.routines.RoutineStoreNames
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.withContext

/**
 * Starting what a presence evaluation decided (spec §8.3.1), so that a worker cancelled halfway can
 * never lose an arrive or a leave: the owed starts are written in the same store write as the
 * transition ([PresenceRecord.pendingFire]), each is started and cleared under [NonCancellable], and
 * whatever is still owed is replayed by the next evaluation. JVM-testable over the real repository.
 */
internal object PresenceFiring {
    /** Records [presence] for [homeId] with the owed starts in one write. False when the home changed underneath. */
    suspend fun record(repository: RoutineRepository, homeId: String, presence: PresenceRecord): Boolean {
        var matched = false
        val written =
            repository.updateHome { current ->
                if (current?.id == homeId) {
                    matched = true
                    current.copy(presence = presence)
                } else {
                    current
                }
            }
        return written && matched
    }

    /** Starts every start still owed for [homeId] (older ones are dropped), clearing each once made. */
    suspend fun fireOwed(repository: RoutineRepository, homeId: String, nowUnixMs: Long): Int =
        withContext(NonCancellable) {
            val owed = repository.home.value?.takeIf { it.id == homeId }?.presence?.pendingFire.orEmpty()
            val live = PendingFires.merge(owed, emptyList(), nowUnixMs)
            var started = 0
            for (fire in live) {
                if (fire.suppressed) {
                    repository.recordRefusal(fire.routineId, fire.source, RoutineReasonCodes.FLAP_SUPPRESSED)
                } else {
                    repository.run(fire.routineId, fire.source)
                    started++
                }
                repository.updateHome { current ->
                    current?.takeIf { it.id == homeId }?.let { it.copy(presence = it.presence.copy(pendingFire = it.presence.pendingFire - fire)) } ?: current
                }
            }
            // Anything too old to honour goes too.
            if (live.size != owed.size) {
                repository.updateHome { current ->
                    current?.takeIf { it.id == homeId }?.let { it.copy(presence = it.presence.copy(pendingFire = it.presence.pendingFire.filter { f -> f in live })) } ?: current
                }
            }
            started
        }
}

/**
 * The last applied registration plan, in a small plain SharedPreferences file (§12): readable at
 * process start and in a broadcast without decrypting the routine store or loading the native core.
 * It is the "armed" flag: armed means the network callback plan is on. Two booleans, no network facts;
 * still excluded from backup and device transfer with the other routine files (T12), because a
 * restored "armed" on a new phone would describe registrations that phone never made.
 */
internal object PresencePlanStore {
    private const val KEY_SET = "applied"
    private const val KEY_CALLBACK = "networkCallback"
    private const val KEY_PERIODIC = "periodicCheck"

    private fun prefs(context: Context) = context.applicationContext.getSharedPreferences(RoutineStoreNames.PRESENCE_PREFS_FILE, Context.MODE_PRIVATE)

    fun isArmed(context: Context): Boolean = prefs(context).getBoolean(KEY_CALLBACK, false)

    fun load(context: Context): PresenceRegistrationPlan? {
        val prefs = prefs(context)
        if (!prefs.getBoolean(KEY_SET, false)) return null
        return PresenceRegistrationPlan(prefs.getBoolean(KEY_CALLBACK, false), prefs.getBoolean(KEY_PERIODIC, false))
    }

    fun save(context: Context, plan: PresenceRegistrationPlan) {
        prefs(context).edit()
            .putBoolean(KEY_SET, true)
            .putBoolean(KEY_CALLBACK, plan.networkCallback)
            .putBoolean(KEY_PERIODIC, plan.periodicCheck)
            .apply()
    }
}
