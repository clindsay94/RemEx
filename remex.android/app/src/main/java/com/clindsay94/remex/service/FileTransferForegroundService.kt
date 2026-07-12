package com.clindsay94.remex.service

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

/**
 * Foreground service of type `dataSync` (plan WP5) that keeps the process alive while the
 * [FileTransferEngine] queue drains, so a transfer is not bound to `viewModelScope` and survives the
 * app being backgrounded or killed. Requires the `FOREGROUND_SERVICE_DATA_SYNC` permission (declared
 * in the manifest).
 *
 * Started when a transfer is enqueued and stopped once the engine reports the queue idle. It hosts no
 * transfer logic of its own — that lives in the (process-scoped) [FileTransferEngine]; the service's
 * only job is to hold the foreground lifetime + persistent notification.
 */
class FileTransferForegroundService : Service() {

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        FileTransferEngine.start(applicationContext)
        // When the engine finishes draining, retire the foreground lifetime.
        FileTransferEngine.onQueueIdle = { stopSelf() }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startAsForeground()
        // Sticky so the OS restarts us (and the engine reloads its persisted queue) after a kill.
        return START_STICKY
    }

    override fun onDestroy() {
        FileTransferEngine.onQueueIdle = null
        super.onDestroy()
    }

    private fun startAsForeground() {
        ensureChannel()
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
        val notification =
            NotificationCompat.Builder(this, CHANNEL_ID)
                .setSmallIcon(R.drawable.ic_notification)
                .setContentTitle(getString(R.string.file_transfer_service_title))
                .setContentText(getString(R.string.file_transfer_service_active))
                .setContentIntent(pending)
                .setOngoing(true)
                .setSilent(true)
                .setPriority(NotificationCompat.PRIORITY_LOW)
                .build()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(
                NOTIFICATION_ID,
                notification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC,
            )
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
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
