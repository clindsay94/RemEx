package com.clindsay94.remex.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import com.clindsay94.remex.MainActivity
import com.clindsay94.remex.R
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.sample
import kotlinx.coroutines.launch

/**
 * Foreground service of type `dataSync` (plan WP5) that keeps the process alive while the
 * [FileTransferEngine] queue drains, so a transfer is not bound to `viewModelScope` and survives the
 * app being backgrounded or killed. Requires the `FOREGROUND_SERVICE_DATA_SYNC` permission (declared
 * in the manifest).
 *
 * Started when a transfer is enqueued and stopped once the engine reports the queue idle. It hosts no
 * transfer logic of its own — that lives in the (process-scoped) [FileTransferEngine]; the service's
 * job is to hold the foreground lifetime + a **live progress** notification (a determinate bar with
 * percentage and file count), which is what makes the data-sync foreground service "noticeable to the
 * user" as Google Play requires for the FGS declaration.
 */
class FileTransferForegroundService : Service() {

    // Observes the engine queue for the service's lifetime; cancelled in onDestroy.
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        ensureChannel()
        FileTransferEngine.start(applicationContext)
        // When the engine finishes draining, retire the foreground lifetime.
        FileTransferEngine.onQueueIdle = { stopSelf() }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // Post the mandatory FGS notification immediately (inside the 5s window) from the current queue…
        startForegroundWith(buildNotification(modelOf(FileTransferEngine.queue.value)))
        // …then keep it live as bytes flow.
        observeProgress()
        // Sticky so the OS restarts us (and the engine reloads its persisted queue) after a kill.
        return START_STICKY
    }

    override fun onDestroy() {
        FileTransferEngine.onQueueIdle = null
        serviceScope.cancel()
        super.onDestroy()
    }

    /**
     * Re-posts the ongoing notification with live progress. Data frames fire per-chunk (thousands/sec),
     * so we quantize to a whole-percent [NotifModel] — [distinctUntilChanged] then collapses identical
     * frames to at most ~100 updates over a whole transfer — and [sample] throttles bursts to stay well
     * under the OS notification-update rate limit.
     */
    private fun observeProgress() {
        serviceScope.launch {
            FileTransferEngine.queue
                .map { modelOf(it) }
                .distinctUntilChanged()
                .sample(UPDATE_INTERVAL_MS)
                .collect { model ->
                    getSystemService(NotificationManager::class.java)
                        .notify(NOTIFICATION_ID, buildNotification(model))
                }
        }
    }

    private fun startForegroundWith(notification: Notification) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

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
            // Queue is draining to idle; keep a generic ongoing card until stopSelf() lands.
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
                    getString(R.string.file_transfer_service_progress, percent, number, total) to false
                TransferState.Verifying ->
                    getString(R.string.file_manager_transfer_verifying) to true
                else -> // Negotiating / Queued: no byte progress yet.
                    getString(R.string.file_transfer_service_active) to true
            }
        return NotifModel(title, text, percent, indeterminate)
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

    private fun ensureChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val channel =
            NotificationChannel(
                CHANNEL_ID,
                getString(R.string.file_transfer_notification_channel_name),
                NotificationManager.IMPORTANCE_LOW,
            ).apply {
                description = getString(R.string.file_transfer_notification_channel_description)
                setShowBadge(false)
            }
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    companion object {
        private const val CHANNEL_ID = "remex_file_transfer"
        private const val NOTIFICATION_ID = 1003

        /** Min gap between notification re-posts; caps update rate well under the OS limit. */
        private const val UPDATE_INTERVAL_MS = 500L

        /** Starts the service (idempotent). Call after enqueuing a transfer. */
        fun start(context: Context) {
            val intent = Intent(context, FileTransferForegroundService::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        }
    }
}
