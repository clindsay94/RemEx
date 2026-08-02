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
import com.clindsay94.remex.share.FileOpener

object FileTransferNotificationManager {

    private const val CHANNEL_ID = "remex_file_transfer"
    private const val NOTIFICATION_ID = 1002

    // Consent prompts use a separate high-importance channel (heads-up + sound) so a sensitive
    // full-browse / incoming-push request the PC raised is noticed, distinct from the silent
    // low-priority transfer-progress channel above. One notification per consentId.
    private const val CONSENT_CHANNEL_ID = "remex_file_consent"
    private const val CONSENT_NOTIFICATION_ID_BASE = 1200

    // Completion notifications use their own id space (one per file, derived from the name) so a
    // finished download's "Open" prompt is not clobbered by the ongoing-progress notification.
    private const val COMPLETE_NOTIFICATION_ID_BASE = 1100

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

    /**
     * NOT THE LIVE TRANSFER NOTIFICATION. Against any current host the ongoing notification is
     * built by [FileTransferJobService], which reads the rate from [FileTransferEngine] directly.
     * This method serves the v2 base64 fallback path only, which is why the rate arrives as
     * parameters rather than being looked up here.
     *
     * @param bytesPerSecond current throughput, or null when it is not yet known.
     * @param secondsRemaining time left, or null when it cannot honestly be said.
     * @see TransferProgressFormat
     */
    fun showTransferProgress(
            context: Context,
            fileName: String,
            isDownload: Boolean,
            transferredBytes: Long,
            totalBytes: Long,
            bytesPerSecond: Double? = null,
            secondsRemaining: Double? = null,
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

        // THE SPEED AND ETA GO IN contentText, NOT subText. subText already carries the percentage
        // and is truncated hard by the system on a collapsed notification, which is where a user
        // glances at a long transfer. A percentage tells them how far along it is; the ETA is the
        // thing they actually want, so it goes in the line that survives.
        val suffix =
                TransferProgressText.progressSuffix(
                        context,
                        TransferProgressFormat.rate(bytesPerSecond),
                        TransferProgressFormat.eta(secondsRemaining),
                )

        notify(
                context = context,
                title = context.getString(titleRes, fileName),
                text =
                        if (suffix == null) body
                        else context.getString(R.string.file_transfer_detail_separator, body, suffix),
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

    /**
     * Posts a "download complete" notification carrying an **Open** action that launches a viewer for
     * the just-downloaded file (plan WP8, open-after-download). [localUri] is the local destination
     * (a SAF `content://` document or a `file://` path); MIME is inferred from [fileName]. Falls back
     * to a plain completion notification when no viewer can be resolved.
     */
    fun showDownloadComplete(context: Context, fileName: String, localUri: String) {
        if (!canPostNotifications(context)) return
        ensureChannel(context)

        val notificationId =
            COMPLETE_NOTIFICATION_ID_BASE + (fileName.hashCode() and 0xFFFF)

        val builder =
                NotificationCompat.Builder(context, CHANNEL_ID)
                        .setSmallIcon(R.drawable.ic_notification)
                        .setContentTitle(
                                context.getString(
                                        R.string.file_transfer_notification_complete_title
                                )
                        )
                        .setContentText(
                                context.getString(
                                        R.string.file_transfer_notification_download_complete,
                                        fileName,
                                )
                        )
                        .setOnlyAlertOnce(true)
                        .setSilent(true)
                        .setAutoCancel(true)
                        .setPriority(NotificationCompat.PRIORITY_LOW)

        val viewIntent = FileOpener.buildViewIntent(context, localUri, fileName)
        if (viewIntent != null) {
            val openPending =
                    PendingIntent.getActivity(
                            context,
                            notificationId,
                            viewIntent,
                            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
                    )
            builder.setContentIntent(openPending)
            builder.addAction(
                    0,
                    context.getString(R.string.file_transfer_notification_open),
                    openPending,
            )
        } else {
            // No viewer resolvable — tapping just opens the app.
            val tapIntent =
                    Intent(context, MainActivity::class.java).apply {
                        flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
                    }
            builder.setContentIntent(
                    PendingIntent.getActivity(
                            context,
                            notificationId,
                            tapIntent,
                            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
                    )
            )
        }

        NotificationManagerCompat.from(context).notify(notificationId, builder.build())
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

    // ── Consent prompts (plan §2 / WP9) ───────────────────────────────────────

    /**
     * Posts a high-priority consent notification for a sensitive request the PC raised — a full-device
     * browse grant or an incoming file push — carrying **Allow** / **Deny** actions that resolve the
     * prompt via [FileConsentActionReceiver]. A parallel foreground dialog mirrors the same prompt; the
     * first responder wins. Silently no-ops when notifications are not permitted, in which case the
     * prompt still auto-denies after its timeout.
     */
    fun showConsentRequest(context: Context, prompt: FileConsentPrompt) {
        if (!canPostNotifications(context)) return
        ensureConsentChannel(context)

        val isPush = prompt.kind == FileConsentKinds.INCOMING_PUSH
        val title =
            context.getString(
                if (isPush) R.string.file_consent_push_title
                else R.string.file_consent_full_browse_title
            )
        val body =
            when {
                isPush && !prompt.detail.isNullOrBlank() ->
                    context.getString(R.string.file_consent_push_message, prompt.detail)
                isPush -> context.getString(R.string.file_consent_push_message_generic)
                else -> context.getString(R.string.file_consent_full_browse_message)
            }

        val notificationId = consentNotificationId(prompt.consentId)
        val allowPending = consentActionIntent(context, prompt.consentId, granted = true, notificationId)
        val denyPending = consentActionIntent(context, prompt.consentId, granted = false, notificationId)

        val builder =
            NotificationCompat.Builder(context, CONSENT_CHANNEL_ID)
                .setSmallIcon(R.drawable.ic_notification)
                .setContentTitle(title)
                .setContentText(body)
                .setStyle(NotificationCompat.BigTextStyle().bigText(body))
                .setContentIntent(denyPending)
                .setDeleteIntent(denyPending)
                .setAutoCancel(true)
                .setOnlyAlertOnce(true)
                .setCategory(NotificationCompat.CATEGORY_RECOMMENDATION)
                .setPriority(NotificationCompat.PRIORITY_HIGH)
                .addAction(0, context.getString(R.string.file_consent_deny), denyPending)
                .addAction(0, context.getString(R.string.file_consent_allow), allowPending)

        NotificationManagerCompat.from(context).notify(notificationId, builder.build())
    }

    /** Clears the consent notification for [consentId] once resolved (by action, dialog, or timeout). */
    fun cancelConsent(context: Context, consentId: String) {
        if (!canPostNotifications(context)) return
        NotificationManagerCompat.from(context).cancel(consentNotificationId(consentId))
    }

    private fun consentActionIntent(
        context: Context,
        consentId: String,
        granted: Boolean,
        notificationId: Int,
    ): PendingIntent {
        val intent =
            Intent(context, FileConsentActionReceiver::class.java).apply {
                action = FileConsentActionReceiver.ACTION_RESOLVE
                putExtra(FileConsentActionReceiver.EXTRA_CONSENT_ID, consentId)
                putExtra(FileConsentActionReceiver.EXTRA_GRANTED, granted)
            }
        // Distinct request code per (notification, decision) so Allow and Deny do not collide.
        val requestCode = notificationId * 2 + if (granted) 1 else 0
        return PendingIntent.getBroadcast(
            context,
            requestCode,
            intent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
    }

    private fun consentNotificationId(consentId: String): Int =
        CONSENT_NOTIFICATION_ID_BASE + (consentId.hashCode() and 0xFFFF)

    private fun ensureConsentChannel(context: Context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val channel =
            NotificationChannel(
                    CONSENT_CHANNEL_ID,
                    context.getString(R.string.file_consent_channel_name),
                    NotificationManager.IMPORTANCE_HIGH,
                )
                .apply {
                    description = context.getString(R.string.file_consent_channel_description)
                    setShowBadge(true)
                }
        context.getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
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
