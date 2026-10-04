package com.clindsay94.remex.alerts

import android.Manifest
import android.annotation.SuppressLint
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.util.Log
import androidx.annotation.StringRes
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import com.clindsay94.remex.MainActivity
import com.clindsay94.remex.R
import com.clindsay94.remex.data.SensorAlertDirection
import com.clindsay94.remex.data.SensorAlertFired
import com.clindsay94.remex.data.SensorAlertSeverity
import com.clindsay94.remex.data.SensorAlerts
import java.util.Locale
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * The two "PC alerts" notification channels (RemEx-pp4cm.12).
 *
 * TWO CHANNELS BECAUSE IMPORTANCE BELONGS TO A CHANNEL, NOT TO A NOTIFICATION: a Critical alert from the
 * PC is high importance (it shows heads-up), a Warning is default importance (it sits in the shade).
 * Android fixes a channel's importance at first creation, so each is constructed exactly once, one call
 * each, in [ensure] (`NotificationChannelOwnershipTest`).
 *
 * Both are private on the lock screen, with a public version that says only "PC alert": the sensor name
 * and reading never show on a locked screen.
 */
internal object PcAlertChannels {
        const val CRITICAL = "pc_alerts_critical"
        const val WARNING = "pc_alerts_warning"

        class Spec(val id: String, @StringRes val name: Int, @StringRes val description: Int, val importance: Int)

        val CriticalSpec = Spec(CRITICAL, R.string.pc_alert_channel_critical_name, R.string.pc_alert_channel_critical_description, NotificationManager.IMPORTANCE_HIGH)
        val WarningSpec = Spec(WARNING, R.string.pc_alert_channel_warning_name, R.string.pc_alert_channel_warning_description, NotificationManager.IMPORTANCE_DEFAULT)

        /** The channel a firing of [severity] is posted on. */
        fun idFor(severity: SensorAlertSeverity): String = if (severity == SensorAlertSeverity.CRITICAL) CRITICAL else WARNING

        fun ensure(context: Context) {
                val manager = context.getSystemService(NotificationManager::class.java) ?: return
                manager.createNotificationChannel(
                        NotificationChannel(CriticalSpec.id, context.getString(CriticalSpec.name), CriticalSpec.importance).apply {
                                description = context.getString(CriticalSpec.description)
                                lockscreenVisibility = NotificationCompat.VISIBILITY_PRIVATE
                        }
                )
                manager.createNotificationChannel(
                        NotificationChannel(WarningSpec.id, context.getString(WarningSpec.name), WarningSpec.importance).apply {
                                description = context.getString(WarningSpec.description)
                                lockscreenVisibility = NotificationCompat.VISIBILITY_PRIVATE
                        }
                )
        }
}

/**
 * What one PC alert says on the phone, as resource ids and arguments so it is provable without a
 * device. [key] is the notification's identity: one per sensor, so the next alert from the same
 * sensor replaces the last instead of stacking.
 */
data class PcAlertNotification(
        val channelId: String,
        @StringRes val titleRes: Int,
        val titleArgs: List<String>,
        @StringRes val textRes: Int,
        val textArgs: List<String>,
        val key: String
)

/** Maps a `sensor_alert_fired` to the notification it becomes. Pure JVM. */
object PcAlertNotificationMapper {

        fun map(fired: SensorAlertFired, locale: Locale = Locale.getDefault()): PcAlertNotification =
                PcAlertNotification(
                        channelId = PcAlertChannels.idFor(fired.severity),
                        titleRes = R.string.pc_alert_notification_title,
                        titleArgs = listOf(fired.displayName, SensorAlerts.formatValue(fired.value, fired.unit, locale)),
                        textRes =
                                if (fired.direction == SensorAlertDirection.ABOVE) R.string.pc_alert_notification_above
                                else R.string.pc_alert_notification_below,
                        textArgs = listOf(SensorAlerts.formatValue(fired.threshold, fired.unit, locale)),
                        key = "pcalert:" + fired.sensorName.lowercase(Locale.ROOT)
                )
}

/**
 * A notification's tap asks MainActivity to open Sensors. MainActivity hands the intent here and
 * AppNavigation navigates, the same arrangement as the routines' `RoutineOpenRequests`.
 */
object SensorAlertOpenRequests {
        const val EXTRA_OPEN_SENSORS = "com.clindsay94.remex.alerts.OPEN_SENSORS"

        private val _pending = MutableStateFlow(false)
        val pending: StateFlow<Boolean> = _pending.asStateFlow()

        /** True when [intent] asked for Sensors (it is then remembered until consumed). */
        fun offer(intent: Intent?): Boolean {
                if (intent == null) return false
                // MainActivity is exported, so any app can start it with extras, and reading any extra
                // unparcels the whole bundle, which can throw below API 33. A request we cannot read is
                // not a request. Only this one boolean is ever read, so the intent chooses nothing else.
                val requested =
                        try {
                                if (!intent.getBooleanExtra(EXTRA_OPEN_SENSORS, false)) return false
                                intent.removeExtra(EXTRA_OPEN_SENSORS)
                                true
                        } catch (e: RuntimeException) {
                                Log.w("SensorAlertOpenRequests", "Ignoring an intent whose extras could not be read.", e)
                                false
                        }
                if (requested) _pending.value = true
                return requested
        }

        /** Takes the pending request, if any, so it opens Sensors once. */
        fun consume(): Boolean = _pending.getAndSetFalse()

        private fun MutableStateFlow<Boolean>.getAndSetFalse(): Boolean {
                var was: Boolean
                do {
                        was = value
                } while (!compareAndSet(was, false))
                return was
        }
}

/** Posts the PC's alerts to the notification shade (RemEx-pp4cm.12). */
class PcAlertNotificationPresenter(context: Context) {
        private val appContext = context.applicationContext

        /** False, posting nothing, when RemEx may not post notifications. */
        fun post(fired: SensorAlertFired): Boolean {
                if (!canPost()) return false
                val spec = PcAlertNotificationMapper.map(fired)
                PcAlertChannels.ensure(appContext)
                val title = appContext.getString(spec.titleRes, *spec.titleArgs.toTypedArray())
                val text = appContext.getString(spec.textRes, *spec.textArgs.toTypedArray())
                val critical = spec.channelId == PcAlertChannels.CRITICAL
                val builder =
                        NotificationCompat.Builder(appContext, spec.channelId)
                                .setSmallIcon(R.drawable.ic_notification)
                                .setContentTitle(title)
                                .setContentText(text)
                                .setStyle(NotificationCompat.BigTextStyle().bigText(text))
                                .setContentIntent(openSensorsIntent())
                                .setAutoCancel(true)
                                .setCategory(NotificationCompat.CATEGORY_STATUS)
                                .setPriority(if (critical) NotificationCompat.PRIORITY_HIGH else NotificationCompat.PRIORITY_DEFAULT)
                                .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
                                .setPublicVersion(
                                        NotificationCompat.Builder(appContext, spec.channelId)
                                                .setSmallIcon(R.drawable.ic_notification)
                                                .setContentTitle(appContext.getString(R.string.pc_alert_notification_public_title))
                                                .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
                                                .build()
                                )
                return notify(spec.key.hashCode(), builder.build())
        }

        private fun openSensorsIntent(): PendingIntent {
                val intent =
                        Intent(appContext, MainActivity::class.java).apply {
                                flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
                                putExtra(SensorAlertOpenRequests.EXTRA_OPEN_SENSORS, true)
                        }
                return PendingIntent.getActivity(
                        appContext,
                        REQUEST_OPEN_SENSORS,
                        intent,
                        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
                )
        }

        private fun canPost(): Boolean =
                ContextCompat.checkSelfPermission(appContext, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED &&
                        NotificationManagerCompat.from(appContext).areNotificationsEnabled()

        // SUPPRESSED BECAUSE THE GUARD IS THE FIRST LINE: canPost() is an ordinary call, which lint's
        // permission analysis cannot see through (the RemEx-cljx note in FileTransferNotificationManager).
        @SuppressLint("MissingPermission")
        private fun notify(id: Int, notification: android.app.Notification): Boolean {
                if (!canPost()) return false
                return try {
                        NotificationManagerCompat.from(appContext).notify(id, notification)
                        true
                } catch (e: SecurityException) {
                        Log.w("PcAlertNotifications", "Posting a PC alert was refused.", e)
                        false
                }
        }

        private companion object {
                const val REQUEST_OPEN_SENSORS = 0x6000_0001
        }
}
