package com.clindsay94.remex.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import androidx.core.app.NotificationCompat
import com.clindsay94.remex.MainActivity
import com.clindsay94.remex.R
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.FlowPreview
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.sample
import kotlinx.coroutines.launch

/**
 * User-Initiated Data Transfer (UIDT) job that keeps the process alive while the [FileTransferEngine]
 * queue drains, and shows the mandatory ongoing progress notification. This replaces the former
 * `dataSync` foreground service (RemEx-gvbq): Google recommends UIDT jobs over the dataSync FGS for
 * network transfers made in response to an explicit user action, and going UIDT-only (`minSdk 34`)
 * lets us drop the `dataSync` foreground-service type and its declaration entirely.
 *
 * Like the old service, this hosts **no transfer logic of its own** — that lives in the process-scoped
 * [FileTransferEngine]. The job only holds the UIDT lifetime and the user-visible notification (a
 * determinate progress bar with percentage and file count), attached via [setNotification].
 *
 * Scheduled by [schedule] from a **user-visible** context (the Share sheet or the open file manager),
 * which UIDT requires — scheduling from the background returns `RESULT_FAILURE`. One job drains the
 * whole batch: while it runs, newly-enqueued transfers are picked up by the same engine, so [schedule]
 * is a no-op when a job is already pending or running.
 */
class FileTransferJobService : JobService() {

    // Observes the engine queue for the running job's lifetime; cancelled in [cleanup].
    private var scope: CoroutineScope? = null

    // The live job's parameters, needed to (re)attach the notification and to call jobFinished().
    @Volatile private var params: JobParameters? = null

    // sample() is still a @FlowPreview API. Accepted deliberately: it is the only operator that gives
    // the "emit at most one update per window" behaviour the OS notification rate limit needs, it has
    // been preview-stable for years, and a source-incompatible change would surface here at compile
    // time — not at runtime on a user's phone.
    @OptIn(FlowPreview::class)
    override fun onStartJob(params: JobParameters): Boolean {
        this.params = params
        isRunning = true
        ensureChannel()
        FileTransferEngine.start(applicationContext)

        // Post the mandatory UIDT notification immediately from the current queue…
        updateNotification(modelOf(FileTransferEngine.queue.value))

        // …retire the job once the engine has drained the queue…
        FileTransferEngine.onQueueIdle = { scope?.launch { finish(reschedule = false) } }

        // …and keep the notification live as bytes flow. Data frames fire per-chunk (thousands/sec),
        // so quantize to a whole-percent NotifModel (distinctUntilChanged collapses identical frames)
        // and sample() throttles bursts under the OS notification-update rate limit — same as the FGS.
        val s = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
        scope = s
        s.launch {
            FileTransferEngine.queue
                .map { modelOf(it) }
                .distinctUntilChanged()
                .sample(UPDATE_INTERVAL_MS)
                .collect { updateNotification(it) }
        }

        // Work continues asynchronously on the engine; jobFinished() is called from onQueueIdle.
        return true
    }

    override fun onStopJob(params: JobParameters): Boolean {
        // The system is stopping this job (constraints no longer met, system health, etc.). The engine
        // is process-scoped and keeps its persisted queue, so ask to reschedule if work still remains.
        val hasWork =
            FileTransferEngine.queue.value.any {
                it.state == TransferState.Queued ||
                    it.state == TransferState.Negotiating ||
                    it.state == TransferState.Active ||
                    it.state == TransferState.Verifying
            }
        cleanup()
        return hasWork
    }

    private fun finish(reschedule: Boolean) {
        val p = params
        cleanup()
        if (p != null) jobFinished(p, reschedule)
    }

    private fun cleanup() {
        FileTransferEngine.onQueueIdle = null
        scope?.cancel()
        scope = null
        params = null
        isRunning = false
    }

    /** Attaches/updates the UIDT job's user-visible notification; removed when the job ends. */
    private fun updateNotification(model: NotifModel) {
        val p = params ?: return
        setNotification(p, NOTIFICATION_ID, buildNotification(model), JOB_END_NOTIFICATION_POLICY_REMOVE)
    }

    // ── Notification model + rendering (carried over verbatim from the former FGS) ───────────────

    /** Snapshot of the queue reduced to exactly what the notification needs to render. */
    private data class NotifModel(
        val title: String,
        val text: String,
        val percent: Int,
        val indeterminate: Boolean,
    )

    /**
     * Reduces the full queue to a [NotifModel]. "file N of M" counts every not-cancelled entry as the
     * batch (finished items linger until the engine prunes them, giving a stable denominator); the
     * active item is the one currently negotiating/transferring/verifying, else the next queued one.
     */
    private fun modelOf(queue: List<QueuedTransfer>): NotifModel {
        val batch = queue.filter { it.state != TransferState.Cancelled }
        val total = batch.size.coerceAtLeast(1)
        val doneCount = batch.count { it.state == TransferState.Done }
        val active =
            queue.firstOrNull {
                it.state == TransferState.Active ||
                    it.state == TransferState.Negotiating ||
                    it.state == TransferState.Verifying
            } ?: queue.firstOrNull { it.state == TransferState.Queued }

        if (active == null) {
            // Queue is draining to idle; keep a generic ongoing card until jobFinished() lands.
            return NotifModel(
                title = getString(R.string.file_transfer_service_title),
                text = getString(R.string.file_transfer_service_active),
                percent = 0,
                indeterminate = true,
            )
        }

        val number = (doneCount + 1).coerceAtMost(total)
        val percent =
            if (active.size > 0) ((active.bytesTransferred * 100) / active.size).toInt().coerceIn(0, 100)
            else 0
        // Direction glyph is locale-neutral: ↓ receiving from PC, ↑ sending to PC.
        val arrow = if (active.mode == FileTransferModes.DOWNLOAD) "↓ " else "↑ "
        val title = arrow + active.fileName.ifBlank { getString(R.string.file_transfer_file_fallback) }

        val (text, indeterminate) =
            when (active.state) {
                TransferState.Active ->
                    // The speed and ETA ride in contentText beside the percentage, because this is
                    // the notification a user glances at during a long transfer and "how much
                    // longer" is the question they are actually asking.
                    withRateAndEta(
                        getString(R.string.file_transfer_service_progress, percent, number, total),
                        active.id,
                    ) to false
                TransferState.Verifying ->
                    getString(R.string.file_manager_transfer_verifying) to true
                else -> // Negotiating / Queued: no byte progress yet.
                    getString(R.string.file_transfer_service_active) to true
            }
        return NotifModel(title, text, percent, indeterminate)
    }

    /**
     * Appends "12.4 MB/s · 38 seconds left", or returns [base] untouched (RemEx-qmiv).
     *
     * Untouched is the honest early state: the estimator needs two observations, and padding the
     * gap with a placeholder number is what the parent bead ruled out, because a user plans around
     * it even when it is about to change by two orders of magnitude.
     */
    private fun withRateAndEta(base: String, transferId: String): String {
        val suffix = TransferProgressText.progressSuffix(
            this,
            TransferProgressFormat.rate(FileTransferEngine.bytesPerSecond(transferId)),
            TransferProgressFormat.eta(FileTransferEngine.secondsRemaining(transferId)),
        ) ?: return base

        return getString(R.string.file_transfer_detail_separator, base, suffix)
    }

    private fun buildNotification(model: NotifModel): Notification {
        val tapIntent =
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
            }
        val pending =
            PendingIntent.getActivity(
                this,
                0,
                tapIntent,
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
            )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(model.title)
            .setContentText(model.text)
            .setContentIntent(pending)
            .setOngoing(true)
            .setSilent(true)
            .setOnlyAlertOnce(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setProgress(100, model.percent, model.indeterminate)
            .build()
    }

    /** Asks the channel's owner rather than declaring a second copy of it (RemEx-9gbzc). */
    private fun ensureChannel() = FileTransferNotificationManager.ensureTransferChannel(this)

    companion object {
        private const val CHANNEL_ID = "remex_file_transfer"
        private const val NOTIFICATION_ID = 1003
        private const val JOB_ID = 1003

        /** Min gap between notification re-posts; caps update rate well under the OS limit. */
        private const val UPDATE_INTERVAL_MS = 500L

        /** True while a job instance is actively running, so [schedule] never replaces a live job. */
        @Volatile private var isRunning = false

        /**
         * Schedules the UIDT file-transfer job. Idempotent — a no-op if a job is already pending or
         * running (one job drains the whole queue as the engine keeps picking up new items). MUST be
         * called from a user-visible context; UIDT jobs fail to schedule from the background. Replaces
         * the former `FileTransferForegroundService.start`.
         */
        fun schedule(context: Context) {
            val scheduler = context.getSystemService(JobScheduler::class.java) ?: return
            if (isRunning || scheduler.getPendingJob(JOB_ID) != null) return

            val network =
                NetworkRequest.Builder()
                    .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                    .build()

            val (downloadBytes, uploadBytes) = estimatedBytes()
            val info =
                JobInfo.Builder(JOB_ID, ComponentName(context, FileTransferJobService::class.java))
                    .setUserInitiated(true)
                    .setRequiredNetwork(network)
                    .setEstimatedNetworkBytes(downloadBytes, uploadBytes)
                    .build()
            scheduler.schedule(info)
        }

        /** Sums queued transfer sizes per direction; UNKNOWN when a direction has nothing queued. */
        private fun estimatedBytes(): Pair<Long, Long> {
            val queue = FileTransferEngine.queue.value
            val down = queue.filter { it.mode == FileTransferModes.DOWNLOAD }.sumOf { it.size }
            val up = queue.filter { it.mode != FileTransferModes.DOWNLOAD }.sumOf { it.size }
            val unknown = JobInfo.NETWORK_BYTES_UNKNOWN.toLong()
            return (if (down > 0L) down else unknown) to (if (up > 0L) up else unknown)
        }
    }
}
