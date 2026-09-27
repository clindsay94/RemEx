package com.clindsay94.remex.routines

import android.Manifest
import android.annotation.SuppressLint
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.annotation.StringRes
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import com.clindsay94.remex.MainActivity
import com.clindsay94.remex.R
import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutineLimits
import com.clindsay94.remex.routines.model.RoutineNotifyTargets
import com.clindsay94.remex.routines.model.RoutineReasonArgs
import com.clindsay94.remex.routines.model.RoutineReasonCodes
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunOutcomes
import com.clindsay94.remex.routines.model.RoutineStep
import com.clindsay94.remex.routines.model.RoutineStepStatuses
import com.clindsay94.remex.routines.model.RoutineStepTypes
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

/**
 * The three routine notification channels (routines spec §1.7, §14 S1, R-UX-32), so progress can be
 * silenced without silencing messages.
 *
 * **LOCK-SCREEN REDACTION (T10).** Every channel is `VISIBILITY_PRIVATE`, and every routine
 * notification carries a public version that says only "RemEx routine update": routine names, PC
 * names and message bodies never show on a locked screen. Titles are the routine's name, never a PC
 * hostname. `RoutineNotificationRedactionTest` pins [SPECS] and scans this file for a builder that
 * skips [RoutineNotificationPresenter.baseBuilder].
 *
 * Each channel is constructed exactly once, one call each, in [ensure]
 * (`NotificationChannelOwnershipTest`): Android fixes a channel's importance at first creation, so a
 * second declaration that disagrees would win or lose depending on which ran first.
 */
internal object RoutineNotificationChannels {
    const val PROGRESS = "routines_progress"
    const val RESULTS = "routines_results"
    const val MESSAGES = "routines_messages"

    /** Lock-screen visibility of every routine channel and notification (T10). */
    const val LOCKSCREEN_VISIBILITY = NotificationCompat.VISIBILITY_PRIVATE

    class Spec(val id: String, @StringRes val name: Int, @StringRes val description: Int, val importance: Int)

    val SPECS: List<Spec> =
        listOf(
            // Low and silent: an ongoing progress row must never buzz at each step.
            Spec(PROGRESS, R.string.routine_channel_progress_name, R.string.routine_channel_progress_description, NotificationManager.IMPORTANCE_LOW),
            Spec(RESULTS, R.string.routine_channel_results_name, R.string.routine_channel_results_description, NotificationManager.IMPORTANCE_DEFAULT),
            Spec(MESSAGES, R.string.routine_channel_messages_name, R.string.routine_channel_messages_description, NotificationManager.IMPORTANCE_DEFAULT),
        )

    fun ensure(context: Context) {
        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        val progress = SPECS[0]
        val results = SPECS[1]
        val messages = SPECS[2]
        manager.createNotificationChannel(
            NotificationChannel(progress.id, context.getString(progress.name), progress.importance).apply {
                description = context.getString(progress.description)
                lockscreenVisibility = LOCKSCREEN_VISIBILITY
                setShowBadge(false)
            }
        )
        manager.createNotificationChannel(
            NotificationChannel(results.id, context.getString(results.name), results.importance).apply {
                description = context.getString(results.description)
                lockscreenVisibility = LOCKSCREEN_VISIBILITY
            }
        )
        manager.createNotificationChannel(
            NotificationChannel(messages.id, context.getString(messages.name), messages.importance).apply {
                description = context.getString(messages.description)
                lockscreenVisibility = LOCKSCREEN_VISIBILITY
            }
        )
    }
}

/**
 * Routine notifications (routines spec §1.7): the ongoing progress row with Cancel and Open, the
 * countdown mirror for a destructive step this phone sent (§8.6), the result, and `notify(phone)`
 * messages. Also the extra the S1d screens read to open a run's detail.
 */
internal class RoutineNotificationPresenter(context: Context) : RoutineRunObserver {
    private val appContext = context.applicationContext

    override fun onProgress(run: RoutineRun, routine: Routine, stepIndex: Int?) {
        val steps = routine.steps.orEmpty()
        val text = stepIndex?.let { stepText(steps.getOrNull(it), run) } ?: appContext.getString(R.string.routine_progress_starting)
        post(progressId(run), progressBuilder(run, routine, stepIndex, text).build())
    }

    override fun onCountdown(run: RoutineRun, routine: Routine, stepIndex: Int, endsAtUnixMs: Long) {
        val step = routine.steps.orEmpty().getOrNull(stepIndex)
        val text =
            RoutineReasonText.render(
                appContext,
                appContext.getString(R.string.routine_progress_countdown),
                args(run, step).copy(duration = RoutineLimits.COUNTDOWN_SECONDS.toString()),
            )
        val builder =
            progressBuilder(run, routine, stepIndex, text)
                .setWhen(endsAtUnixMs)
                .setShowWhen(true)
                .setUsesChronometer(true)
                .setChronometerCountDown(true)
        post(progressId(run), builder.build())
    }

    override fun onFinished(run: RoutineRun, routine: Routine?) {
        cancel(progressId(run))
        val succeeded = run.outcome == RoutineRunOutcomes.SUCCEEDED
        val code = run.reasonCode ?: if (succeeded) RoutineReasonCodes.OK else RoutineReasonCodes.INTERNAL_ERROR
        val message = RoutineReasonText.message(appContext, code, run.reasonArgs)
        val builder =
            baseBuilder(RoutineNotificationChannels.RESULTS)
                .setContentTitle(title(run, routine?.name ?: run.routineName))
                .setContentText(message)
                .setStyle(NotificationCompat.BigTextStyle().bigText(message))
                .setContentIntent(openIntent(run, REQUEST_OPEN_RESULT))
                .setAutoCancel(true)
        if (succeeded) {
            // A success is news for a moment, not something to clear by hand (§1.7).
            builder.setTimeoutAfter(SUCCESS_TIMEOUT_MS)
        } else {
            builder.addAction(0, appContext.getString(R.string.routine_notification_see_what_happened), openIntent(run, REQUEST_SEE_WHAT_HAPPENED))
        }
        post(resultId(run), builder.build())
    }

    /** A `notify(phone)` step. False when RemEx may not post notifications (`notify_denied_phone`). */
    fun postMessage(run: RoutineRun, stepIndex: Int, title: String, body: String): Boolean {
        if (!canPost()) return false
        val builder =
            baseBuilder(RoutineNotificationChannels.MESSAGES)
                .setContentTitle(title)
                .setContentText(body)
                .setStyle(NotificationCompat.BigTextStyle().bigText(body))
                .setContentIntent(openIntent(run, REQUEST_OPEN_MESSAGE))
                .setAutoCancel(true)
        return post("message:${run.runId}:$stepIndex".hashCode(), builder.build())
    }

    /**
     * EVERY routine notification starts here: private on the lock screen, with the redacted public
     * version (T10). Building one any other way is what the redaction test looks for.
     */
    fun baseBuilder(channel: String): NotificationCompat.Builder {
        RoutineNotificationChannels.ensure(appContext)
        return NotificationCompat.Builder(appContext, channel)
            .setSmallIcon(R.drawable.ic_notification)
            .setVisibility(RoutineNotificationChannels.LOCKSCREEN_VISIBILITY)
            .setPublicVersion(
                NotificationCompat.Builder(appContext, channel)
                    .setSmallIcon(R.drawable.ic_notification)
                    .setContentTitle(appContext.getString(R.string.routine_notification_public_title))
                    .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
                    .build()
            )
    }

    private fun progressBuilder(run: RoutineRun, routine: Routine, stepIndex: Int?, text: String): NotificationCompat.Builder {
        val total = routine.steps.orEmpty().size.coerceAtLeast(1)
        val done =
            run.steps.orEmpty().count { it.status != RoutineStepStatuses.PENDING && it.status != RoutineStepStatuses.RUNNING }
        val builder =
            baseBuilder(RoutineNotificationChannels.PROGRESS)
                .setContentTitle(title(run, routine.name))
                .setContentText(text)
                .setSubText(stepIndex?.let { appContext.getString(R.string.routine_progress_step_count, it + 1, total) })
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setSilent(true)
                .setCategory(NotificationCompat.CATEGORY_PROGRESS)
                .setContentIntent(openIntent(run, REQUEST_OPEN_PROGRESS))
                .addAction(0, appContext.getString(R.string.routine_notification_cancel), cancelIntent(run))
                .addAction(0, appContext.getString(R.string.routine_notification_open), openIntent(run, REQUEST_OPEN_PROGRESS))
                .setProgress(total, done, false)
        if (Build.VERSION.SDK_INT >= 36) {
            // Android 16+: one segment per step (§1.7). Below that, the determinate bar above.
            builder.setStyle(
                NotificationCompat.ProgressStyle()
                    .setProgressSegments(List(total) { NotificationCompat.ProgressStyle.Segment(1) })
                    .setProgress(done)
            )
        }
        return builder
    }

    private fun stepText(step: RoutineStep?, run: RoutineRun): String {
        val template =
            when {
                step == null -> R.string.routine_progress_starting
                step.type == RoutineStepTypes.WAKE -> R.string.routine_progress_wake
                step.type == RoutineStepTypes.WAIT_ONLINE -> R.string.routine_progress_wait
                step.type == RoutineStepTypes.DELAY -> R.string.routine_progress_delay
                step.type == RoutineStepTypes.NOTIFY && step.target == RoutineNotifyTargets.PHONE -> R.string.routine_progress_notify
                else -> R.string.routine_progress_host
            }
        val args = args(run, step).copy(duration = step?.seconds?.toString())
        return RoutineReasonText.render(appContext, appContext.getString(template), args)
    }

    private fun args(run: RoutineRun, step: RoutineStep?): RoutineReasonArgs =
        RoutineReasonArgs(pc = run.reasonArgs?.pc, action = RoutineActionTokens.of(step), app = step?.appLabel)

    private fun title(run: RoutineRun, name: String?): String {
        val routineName = name.orEmpty()
        return if (run.testRun) {
            RoutineMessageTemplate.fill(appContext.getString(R.string.routine_notification_test_title), mapOf("routine" to routineName))
        } else {
            routineName
        }
    }

    private fun openIntent(run: RoutineRun, requestBase: Int): PendingIntent {
        val intent =
            Intent(appContext, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
                putExtra(EXTRA_OPEN_ROUTINE_ID, run.routineId)
                putExtra(EXTRA_OPEN_RUN_ID, run.runId)
            }
        return PendingIntent.getActivity(
            appContext,
            requestBase + (run.runId.orEmpty().hashCode() and REQUEST_MASK),
            intent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
    }

    private fun cancelIntent(run: RoutineRun): PendingIntent {
        // Explicit component on a NON-exported receiver: no other app can cancel a run (T1).
        val intent =
            Intent(appContext, RoutineNotificationActionReceiver::class.java).apply {
                action = RoutineNotificationActionReceiver.ACTION_CANCEL
                putExtra(RoutineNotificationActionReceiver.EXTRA_RUN_ID, run.runId)
            }
        return PendingIntent.getBroadcast(
            appContext,
            REQUEST_CANCEL + (run.runId.orEmpty().hashCode() and REQUEST_MASK),
            intent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
    }

    private fun canPost(): Boolean =
        ContextCompat.checkSelfPermission(appContext, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED &&
            NotificationManagerCompat.from(appContext).areNotificationsEnabled()

    // SUPPRESSED BECAUSE THE GUARD IS THE FIRST LINE: canPost() is an ordinary call, which lint's
    // permission analysis cannot see through (the RemEx-cljx note in FileTransferNotificationManager).
    @SuppressLint("MissingPermission")
    private fun post(id: Int, notification: android.app.Notification): Boolean {
        if (!canPost()) return false
        return try {
            NotificationManagerCompat.from(appContext).notify(id, notification)
            true
        } catch (e: SecurityException) {
            RoutineLog.w("Posting a routine notification was refused.", e)
            false
        }
    }

    private fun cancel(id: Int) {
        NotificationManagerCompat.from(appContext).cancel(id)
    }

    private fun progressId(run: RoutineRun): Int = "progress:${run.routineId}".hashCode()

    private fun resultId(run: RoutineRun): Int = "result:${run.routineId}".hashCode()

    companion object {
        /** Extras on the MainActivity intent of every routine notification, for the S1d screens. */
        const val EXTRA_OPEN_ROUTINE_ID = "com.clindsay94.remex.routines.OPEN_ROUTINE_ID"
        const val EXTRA_OPEN_RUN_ID = "com.clindsay94.remex.routines.OPEN_RUN_ID"

        const val SUCCESS_TIMEOUT_MS = 10_000L

        // Disjoint request-code ranges, so a PendingIntent of one kind can never replace another's
        // (filterEquals ignores extras; the FileTransferNotificationManager *2/*2+1 lesson).
        private const val REQUEST_MASK = 0x00FF_FFFF
        private const val REQUEST_OPEN_PROGRESS = 0x1000_0000
        private const val REQUEST_OPEN_RESULT = 0x2000_0000
        private const val REQUEST_SEE_WHAT_HAPPENED = 0x3000_0000
        private const val REQUEST_OPEN_MESSAGE = 0x4000_0000
        private const val REQUEST_CANCEL = 0x5000_0000
    }
}

/**
 * The progress notification's Cancel (§8.2). Non-exported and reached only through this app's own
 * immutable PendingIntent. Hands the run id to the repository, which asks the attached runner to
 * stop (sending `routine_cancel` if a host step is in flight) or cancels a queued run outright.
 */
class RoutineNotificationActionReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION_CANCEL) return
        val runId = intent.getStringExtra(EXTRA_RUN_ID) ?: return
        val pending = goAsync()
        val appContext = context.applicationContext
        ReceiverScope.launch {
            try {
                // A phone run stops here; a PC run's progress row (RemEx-pp0rt.12) asks the PC.
                if (!Routines.repository(appContext).cancelRun(runId)) Routines.syncClient(appContext).cancelPcRun(runId)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                RoutineLog.e("Cancelling run ${RoutineLog.id(runId)} from the notification failed.", e)
            } finally {
                pending.finish()
            }
        }
    }

    companion object {
        const val ACTION_CANCEL = "com.clindsay94.remex.routines.action.CANCEL_RUN"
        const val EXTRA_RUN_ID = "com.clindsay94.remex.routines.extra.RUN_ID"

        /** Bounded work (one store write, one queued message), so a process-lifetime scope is fine. */
        private val ReceiverScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    }
}
