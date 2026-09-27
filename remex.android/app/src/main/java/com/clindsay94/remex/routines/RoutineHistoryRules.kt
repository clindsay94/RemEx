package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.model.RoutineReasonCodes
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunOrigins
import com.clindsay94.remex.routines.model.RoutineRunOutcomes
import com.clindsay94.remex.routines.model.RoutineRunSources
import com.clindsay94.remex.routines.model.RoutineTriggerTypes

/**
 * The phone's history retention (routines spec §8.8). Pure JVM; `RoutineRetentionTest` pins it.
 *
 * A run is kept while it is among the last [KEEP_PER_ROUTINE] runs of its routine OR younger than
 * [KEEP_DAYS] days; nothing older than [MAX_AGE_DAYS] days is kept whatever its rank; and the whole
 * history never exceeds [MAX_TOTAL] runs, the oldest going first.
 *
 * Age is measured from [RoutineRun.triggeredAtUnixMs]. A record still `running` is never dropped:
 * it is the one record that makes an interruption visible later (§8.1).
 */
object RoutineRetention {
    const val KEEP_PER_ROUTINE = 50
    const val KEEP_DAYS = 30L
    const val MAX_TOTAL = 1000
    const val MAX_AGE_DAYS = 90L

    private const val DAY_MS = 24L * 60 * 60 * 1000

    fun apply(runsByRoutine: Map<String, List<RoutineRun>>, nowUnixMs: Long): Map<String, List<RoutineRun>> {
        val keepYoungerThan = nowUnixMs - KEEP_DAYS * DAY_MS
        val dropOlderThan = nowUnixMs - MAX_AGE_DAYS * DAY_MS

        val kept = LinkedHashMap<String, List<RoutineRun>>()
        for ((routineId, runs) in runsByRoutine) {
            kept[routineId] =
                newestFirst(runs).filterIndexed { rank, run ->
                    run.isRunning ||
                        (run.triggeredAtUnixMs >= dropOlderThan &&
                            (rank < KEEP_PER_ROUTINE || run.triggeredAtUnixMs >= keepYoungerThan))
                }
        }

        val total = kept.values.sumOf { it.size }
        if (total <= MAX_TOTAL) return kept

        // Over the cap: drop the globally oldest finished runs until it fits.
        val evict =
            kept.values.asSequence()
                .flatten()
                .filterNot { it.isRunning }
                .sortedWith(compareBy<RoutineRun> { it.triggeredAtUnixMs }.thenBy { it.runId.orEmpty() })
                .take(total - MAX_TOTAL)
                .mapNotNull { it.runId }
                .toHashSet()
        return kept.mapValues { (_, runs) -> runs.filterNot { it.runId in evict } }
    }

    fun newestFirst(runs: List<RoutineRun>): List<RoutineRun> =
        runs.sortedWith(compareByDescending<RoutineRun> { it.triggeredAtUnixMs }.thenByDescending { it.runId.orEmpty() })

    private val RoutineRun.isRunning: Boolean get() = outcome == RoutineRunOutcomes.RUNNING
}

/**
 * When a new `skipped` record is folded into an earlier identical one instead of being added
 * (spec §8.1: `already_running` at most once per routine per 60 s; §8.7: paused skips once per
 * routine per hour). Everything else that skips is folded at the §8.1 rate. Without this a flapping
 * trigger would push the user's real history out of the 50-run window within minutes.
 */
object RoutineSkipCoalescing {
    const val DEFAULT_WINDOW_MS = 60_000L
    const val PAUSE_WINDOW_MS = 60L * 60 * 1000

    fun windowMs(reasonCode: String?): Long =
        when (reasonCode) {
            RoutineReasonCodes.PAUSED_ON_PHONE, RoutineReasonCodes.PAUSED_ON_PC -> PAUSE_WINDOW_MS
            else -> DEFAULT_WINDOW_MS
        }

    /** True when [reasonCode] already has a skip record for this routine inside its window. */
    fun isCoalesced(existing: List<RoutineRun>, reasonCode: String, nowUnixMs: Long): Boolean {
        val window = windowMs(reasonCode)
        return existing.any {
            it.outcome == RoutineRunOutcomes.SKIPPED &&
                it.reasonCode == reasonCode &&
                nowUnixMs - it.triggeredAtUnixMs in 0 until window
        }
    }
}

/**
 * The phone's start limits (spec §9 T9, §10.1 `cooldown` and `rate_limited`).
 *
 * - **Owner-wide cap (T9):** on the phone the owner is the phone, so at most
 *   [MAX_AUTOMATIC_STARTS_PER_HOUR] AUTOMATIC starts (`home.*`) across ALL routines in any rolling
 *   hour (`rate_limited`). This is the T9 backstop against a runaway trigger loop.
 * - **Per routine:** a routine that STARTED less than [MIN_INTERVAL_MS] ago is not started again
 *   (`cooldown`), and one routine starts at most [MAX_STARTS_PER_ROUTINE_PER_HOUR] times an hour.
 * - **In-app Run and Test ([RoutineRunSources.MANUAL_APP]) are exempt from all of them:** the person
 *   is looking at the screen, single-flight already stops a double tap, and "it ran moments ago"
 *   would read as the Run button being broken.
 *
 * Measured on wall time from the stored history, because the limits must survive the process being
 * killed between two triggers and elapsed-realtime does not survive a reboot. Skipped records and PC
 * runs never count as starts.
 */
object RoutineStartLimits {
    const val MIN_INTERVAL_MS = 60_000L
    const val MAX_STARTS_PER_ROUTINE_PER_HOUR = 30
    const val MAX_AUTOMATIC_STARTS_PER_HOUR = 30
    private const val HOUR_MS = 60L * 60 * 1000

    /**
     * The reason code that blocks this start, or null when it may start. [routineHistory] is this
     * routine's runs; [allHistory] every run on the phone.
     */
    fun check(routineHistory: List<RoutineRun>, allHistory: List<RoutineRun>, source: String, nowUnixMs: Long): String? {
        if (source == RoutineRunSources.MANUAL_APP) return null
        val withinHour = { run: RoutineRun -> nowUnixMs - run.triggeredAtUnixMs in 0 until HOUR_MS }
        if (isAutomatic(source) &&
            allHistory.count { it.isPhoneStart() && isAutomatic(it.source) && withinHour(it) } >= MAX_AUTOMATIC_STARTS_PER_HOUR
        ) {
            return RoutineReasonCodes.RATE_LIMITED
        }
        val starts = routineHistory.filter { it.isPhoneStart() }
        if (starts.any { nowUnixMs - it.triggeredAtUnixMs in 0 until MIN_INTERVAL_MS }) return RoutineReasonCodes.COOLDOWN
        if (starts.count(withinHour) >= MAX_STARTS_PER_ROUTINE_PER_HOUR) return RoutineReasonCodes.RATE_LIMITED
        return null
    }

    private fun RoutineRun.isPhoneStart(): Boolean = outcome != RoutineRunOutcomes.SKIPPED && origin != RoutineRunOrigins.PC

    /** Automatic sources are the ones Pause all stops (§8.7). */
    fun isAutomatic(source: String?): Boolean = RoutineTriggerTypes.isAutomatic(source)
}
