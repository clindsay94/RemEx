package com.clindsay94.remex.routines

import android.content.Context
import androidx.work.CoroutineWorker
import androidx.work.Data
import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.OutOfQuotaPolicy
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.clindsay94.remex.ConnectionActivity
import com.clindsay94.remex.RemexClientManager
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.first

/**
 * The phone runner's execution vehicle (routines spec §8.2): expedited WorkManager work, never a
 * foreground service (Android 12+ forbids starting one from a network-callback broadcast, T16).
 *
 * One unique work name per routine with [ExistingWorkPolicy.KEEP] is the single-flight backstop; the
 * repository already refuses a second start with `already_running` before it gets here. The run
 * itself is [RoutineRunner]; this class only unpacks the ticket.
 */
class RoutineWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {

    override suspend fun doWork(): Result {
        val ticket = ticketFrom(inputData, runAttemptCount) ?: return Result.failure()
        // The run holds the connection, so the idle teardown never stops it mid-run (RemEx-9yei0).
        val hold = ConnectionActivity.routineRun(ticket.runId)
        ConnectionActivity.hold(hold)
        return try {
            // A background trigger can start this in a fresh process: the client manager must be up
            // (callback registered, routine collector subscribed) before any step talks to the PC.
            RemexClientManager.initialize(applicationContext)
            Routines.runner(applicationContext).execute(ticket)
            Result.success()
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            // The runner finalises its own failures; reaching here means even its finish path threw.
            // Backstop: end the record through the repository, or it stays `running` in activeRuns
            // and every later start of the routine is skipped `already_running`.
            RoutineLog.e("Routine worker for run ${RoutineLog.id(ticket.runId)} failed.", e)
            try {
                Routines.repository(applicationContext).abandonRun(ticket.runId, RoutineReasonCodes.INTERNAL_ERROR)
            } catch (backstop: CancellationException) {
                throw backstop
            } catch (backstop: Exception) {
                RoutineLog.e("Could not finalise run ${RoutineLog.id(ticket.runId)} after a failure.", backstop)
            }
            Result.failure()
        } finally {
            ConnectionActivity.release(hold)
        }
    }

    companion object {
        const val KEY_RUN_ID = "runId"
        const val KEY_ROUTINE_ID = "routineId"
        const val KEY_TRIGGER_TYPE = "triggerType"
        const val KEY_TEST_RUN = "testRun"
        const val KEY_TRIGGERED_AT_UNIX_MS = "triggeredAtUnixMs"
        const val KEY_TRIGGERED_AT_ELAPSED_MS = "triggeredAtElapsedMs"

        fun uniqueWorkName(routineId: String): String = "routine-run-$routineId"

        internal fun inputFor(ticket: RoutineRunTicket): Data =
            Data.Builder()
                .putString(KEY_RUN_ID, ticket.runId)
                .putString(KEY_ROUTINE_ID, ticket.routineId)
                .putString(KEY_TRIGGER_TYPE, ticket.source)
                .putBoolean(KEY_TEST_RUN, ticket.testRun)
                .putLong(KEY_TRIGGERED_AT_UNIX_MS, ticket.triggeredAtUnixMs)
                .putLong(KEY_TRIGGERED_AT_ELAPSED_MS, ticket.triggeredAtElapsedMs)
                .build()

        internal fun ticketFrom(data: Data, attempt: Int): RoutineRunTicket? {
            val runId = data.getString(KEY_RUN_ID) ?: return null
            val routineId = data.getString(KEY_ROUTINE_ID) ?: return null
            val source = data.getString(KEY_TRIGGER_TYPE) ?: return null
            return RoutineRunTicket(
                runId = runId,
                routineId = routineId,
                source = source,
                testRun = data.getBoolean(KEY_TEST_RUN, false),
                triggeredAtUnixMs = data.getLong(KEY_TRIGGERED_AT_UNIX_MS, 0L),
                triggeredAtElapsedMs = data.getLong(KEY_TRIGGERED_AT_ELAPSED_MS, 0L),
                attempt = attempt,
            )
        }
    }
}

/** [RoutineRunScheduler] on WorkManager. */
internal class WorkManagerRoutineScheduler(context: Context) : RoutineRunScheduler {
    private val appContext = context.applicationContext

    private fun workManager(): WorkManager = WorkManager.getInstance(appContext)

    override fun enqueue(ticket: RoutineRunTicket): Boolean =
        try {
            val request =
                OneTimeWorkRequestBuilder<RoutineWorker>()
                    .setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST)
                    .setInputData(RoutineWorker.inputFor(ticket))
                    .addTag(TAG)
                    .build()
            workManager().enqueueUniqueWork(RoutineWorker.uniqueWorkName(ticket.routineId), ExistingWorkPolicy.KEEP, request)
            true
        } catch (e: IllegalStateException) {
            RoutineLog.e("WorkManager is not available.", e)
            false
        }

    override fun cancel(routineId: String) {
        try {
            workManager().cancelUniqueWork(RoutineWorker.uniqueWorkName(routineId))
        } catch (e: IllegalStateException) {
            RoutineLog.e("WorkManager is not available.", e)
        }
    }

    override suspend fun isActive(routineId: String): Boolean =
        try {
            workManager()
                .getWorkInfosForUniqueWorkFlow(RoutineWorker.uniqueWorkName(routineId))
                .first()
                .any { !it.state.isFinished }
        } catch (e: IllegalStateException) {
            RoutineLog.e("WorkManager is not available.", e)
            false
        }

    private companion object {
        const val TAG = "remex-routine-run"
    }
}
