package com.clindsay94.remex.routines

import androidx.core.app.NotificationCompat
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Lock-screen redaction of routine notifications and the three channels (spec §9 T10, R-SEC-09,
 * R-UX-32; RemEx-pp0rt.5).
 *
 * The JVM has no NotificationManager, so this pins the channel specs as data and scans the one file
 * that builds routine notifications: every builder must come from `baseBuilder`, which sets private
 * visibility and the "RemEx routine update" public version.
 */
class RoutineNotificationRedactionTest {
    private val source: String by lazy {
        listOf(
            File("src/main/java/com/clindsay94/remex/routines/RoutineNotifications.kt"),
            File("app/src/main/java/com/clindsay94/remex/routines/RoutineNotifications.kt"),
        ).first { it.isFile }.readText()
    }

    @Test
    fun `four channels with distinct ids, progress silent, countdown heads-up, all private on the lock screen`() {
        val specs = RoutineNotificationChannels.SPECS
        assertEquals(
            listOf(
                RoutineNotificationChannels.PROGRESS,
                RoutineNotificationChannels.RESULTS,
                RoutineNotificationChannels.MESSAGES,
                RoutineNotificationChannels.COUNTDOWN,
            ),
            specs.map { it.id },
        )
        assertEquals(android.app.NotificationManager.IMPORTANCE_LOW, specs.first { it.id == RoutineNotificationChannels.PROGRESS }.importance)
        // A countdown before a power-off must show heads-up with its Cancel button (live pass 2026-09-27).
        assertEquals(android.app.NotificationManager.IMPORTANCE_HIGH, specs.first { it.id == RoutineNotificationChannels.COUNTDOWN }.importance)
        assertTrue(source.contains("baseBuilder(RoutineNotificationChannels.COUNTDOWN)"))
        assertEquals(NotificationCompat.VISIBILITY_PRIVATE, RoutineNotificationChannels.LOCKSCREEN_VISIBILITY)
        assertEquals(
            "each channel sets the private lock-screen visibility",
            4,
            Regex("""lockscreenVisibility = LOCKSCREEN_VISIBILITY""").findAll(source).count(),
        )
    }

    @Test
    fun `every routine notification is built from the redacting base builder`() {
        // Exactly two builders: the base one and its public version. A third would be a notification
        // built without the redaction.
        assertEquals(2, Regex("""NotificationCompat\.Builder\(""").findAll(source).count())
        assertEquals(1, Regex("""\.setPublicVersion\(""").findAll(source).count())
        assertTrue(source.contains(".setVisibility(RoutineNotificationChannels.LOCKSCREEN_VISIBILITY)"))
        assertTrue(source.contains("R.string.routine_notification_public_title"))
        for (channel in listOf("PROGRESS", "RESULTS", "MESSAGES")) {
            assertTrue("$channel notifications go through baseBuilder", source.contains("baseBuilder(RoutineNotificationChannels.$channel)"))
        }
    }
}
