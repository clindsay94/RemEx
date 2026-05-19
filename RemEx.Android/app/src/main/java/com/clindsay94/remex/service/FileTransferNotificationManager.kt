package com.clindsay94.remex.service

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import com.clindsay94.remex.MainActivity
import com.clindsay94.remex.R

object FileTransferNotificationManager {

    private const val CHANNEL_ID = "remex_file_transfer"
    private const val NOTIFICATION_ID = 1002

    fun showTransferStarted(context: Context, fileName: String, isDownload: Boolean) {
        notify(
                context = context,
                title =
                        context.getString(
                                if (isDownload) R.string.file_transfer_notification_download_title
                                else R.string.file_transfer_notification_upload_title,
                                fileName,
                        ),
                text = context.getString(R.string.file_transfer_notification_preparing),
                progress = null,
                indeterminate = true,
                ongoing = true,
        )
    }

    fun showTransferProgress(
            context: Context,
            fileName: String,
            isDownload: Boolean,
            transferredBytes: Long,
            totalBytes: Long,
    ) {
        val titleRes =
                if (isDownload) R.string.file_transfer_notification_download_title
                else R.string.file_transfer_notification_upload_title
        val progress =
                if (totalBytes > 0L)
                        ((transferredBytes.coerceAtMost(totalBytes) * 100) / totalBytes)
                else null
        val body =
                when {
                    progress != null ->
                            context.getString(
                                    R.string.file_transfer_notification_progress,
                                    transferredBytes,
                                    totalBytes,
                            )
                    transferredBytes > 0L ->
                            // Total unknown — show running bytes so the user sees activity
                            // instead of a stuck "Preparing transfer" message.
                            formatBytes(transferredBytes)
                    else -> context.getString(R.string.file_transfer_notification_preparing)
                }

        notify(
                context = context,
                title = context.getString(titleRes, fileName),
                text = body,
                progress = progress?.toInt(),
                indeterminate = progress == null,
                ongoing = true,
        )
    }

    private fun formatBytes(bytes: Long): String =
            when {
                bytes >= 1_073_741_824L -> "%.1f GB".format(bytes / 1_073_741_824.0)
                bytes >= 1_048_576L -> "%.1f MB".format(bytes / 1_048_576.0)
                bytes >= 1_024L -> "%.1f KB".format(bytes / 1_024.0)
                else -> "$bytes B"
            }

    fun showTransferComplete(context: Context, fileName: String, isDownload: Boolean) {
        notify(
                context = context,
                title = context.getString(R.string.file_transfer_notification_complete_title),
                text =
                        context.getString(
                                if (isDownload)
                                        R.string.file_transfer_notification_download_complete
                                else R.string.file_transfer_notification_upload_complete,
                                fileName,
                        ),
                progress = null,
                indeterminate = false,
                ongoing = false,
        )
    }

    fun showTransferFailed(context: Context, message: String) {
        notify(
                context = context,
                title = context.getString(R.string.file_transfer_notification_failed_title),
                text = message,
                progress = null,
                indeterminate = false,
                ongoing = false,
        )
    }

    fun cancel(context: Context) {
        if (!canPostNotifications(context)) return
        NotificationManagerCompat.from(context).cancel(NOTIFICATION_ID)
    }

    private fun notify(
            context: Context,
            title: String,
            text: String,
            progress: Int?,
            indeterminate: Boolean,
            ongoing: Boolean,
    ) {
        if (!canPostNotifications(context)) return

        ensureChannel(context)
        val tapIntent =
                Intent(context, MainActivity::class.java).apply {
                    flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
                }
        val pendingIntent =
                PendingIntent.getActivity(
                        context,
                        0,
                        tapIntent,
                        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
                )

        val builder =
                NotificationCompat.Builder(context, CHANNEL_ID)
                        .setSmallIcon(R.drawable.ic_notification)
                        .setContentTitle(title)
                        .setContentText(text)
                        .setContentIntent(pendingIntent)
                        .setOnlyAlertOnce(true)
                        .setSilent(true)
                        .setOngoing(ongoing)
                        .setAutoCancel(!ongoing)
                        .setPriority(NotificationCompat.PRIORITY_LOW)

        if (progress != null) {
            builder.setProgress(100, progress.coerceIn(0, 100), false)
            builder.setSubText("$progress%")
        } else if (indeterminate) {
            builder.setProgress(100, 0, true)
        } else {
            builder.setProgress(0, 0, false)
        }

        NotificationManagerCompat.from(context).notify(NOTIFICATION_ID, builder.build())
    }

    private fun ensureChannel(context: Context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return

        val channel =
                NotificationChannel(
                                CHANNEL_ID,
                                context.getString(R.string.file_transfer_notification_channel_name),
                                NotificationManager.IMPORTANCE_LOW,
                        )
                        .apply {
                            description =
                                    context.getString(
                                            R.string.file_transfer_notification_channel_description
                                    )
                            setShowBadge(false)
                        }

        val manager = context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(channel)
    }

    private fun canPostNotifications(context: Context): Boolean {
        return Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
                ContextCompat.checkSelfPermission(
                        context,
                        Manifest.permission.POST_NOTIFICATIONS,
                ) == PackageManager.PERMISSION_GRANTED
    }
}
