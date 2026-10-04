package com.clindsay94.remex.alerts

import android.app.NotificationManager
import com.clindsay94.remex.R
import com.clindsay94.remex.data.SensorAlertDirection
import com.clindsay94.remex.data.SensorAlertFired
import com.clindsay94.remex.data.SensorAlertSeverity
import java.util.Locale
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

/**
 * How a PC's `sensor_alert_fired` becomes a phone notification (RemEx-pp4cm.12): Critical rides the
 * high-importance channel and Warning the default one, the wording follows the direction, numbers read
 * in the phone's language, and one sensor is one notification.
 */
class PcAlertNotificationMapperTest {

    private fun fired(
            direction: SensorAlertDirection = SensorAlertDirection.ABOVE,
            severity: SensorAlertSeverity = SensorAlertSeverity.CRITICAL,
            name: String = "CPU Package",
            unit: String? = "°C",
    ) = SensorAlertFired(name, "CPU temperature", 92.4, unit, 90.0, direction, severity, "2026-10-04T09:30:00+00:00")

    @Test
    fun `critical goes to the high importance channel and warning to the default one`() {
        assertEquals(PcAlertChannels.CRITICAL, PcAlertNotificationMapper.map(fired(severity = SensorAlertSeverity.CRITICAL)).channelId)
        assertEquals(PcAlertChannels.WARNING, PcAlertNotificationMapper.map(fired(severity = SensorAlertSeverity.WARNING)).channelId)

        assertEquals(NotificationManager.IMPORTANCE_HIGH, PcAlertChannels.CriticalSpec.importance)
        assertEquals(NotificationManager.IMPORTANCE_DEFAULT, PcAlertChannels.WarningSpec.importance)
        assertEquals(PcAlertChannels.CRITICAL, PcAlertChannels.CriticalSpec.id)
        assertEquals(PcAlertChannels.WARNING, PcAlertChannels.WarningSpec.id)
        assertNotEquals(PcAlertChannels.CRITICAL, PcAlertChannels.WARNING)
    }

    @Test
    fun `the title names the sensor and its reading, the text names the threshold crossed`() {
        val spec = PcAlertNotificationMapper.map(fired(), Locale.US)

        assertEquals(R.string.pc_alert_notification_title, spec.titleRes)
        assertEquals(listOf("CPU temperature", "92.4 °C"), spec.titleArgs)
        assertEquals(listOf("90 °C"), spec.textArgs)
    }

    @Test
    fun `the wording follows the direction of the rule`() {
        assertEquals(R.string.pc_alert_notification_above, PcAlertNotificationMapper.map(fired(direction = SensorAlertDirection.ABOVE)).textRes)
        assertEquals(R.string.pc_alert_notification_below, PcAlertNotificationMapper.map(fired(direction = SensorAlertDirection.BELOW)).textRes)
    }

    @Test
    fun `numbers use the phone's language and a sensor without a unit shows a bare number`() {
        val french = PcAlertNotificationMapper.map(fired(), Locale.FRANCE)
        assertEquals(listOf("CPU temperature", "92,4 °C"), french.titleArgs)

        val bare = PcAlertNotificationMapper.map(fired(unit = null), Locale.US)
        assertEquals(listOf("CPU temperature", "92.4"), bare.titleArgs)
        assertEquals(listOf("90"), bare.textArgs)
    }

    @Test
    fun `one sensor is one notification however its name is cased`() {
        val a = PcAlertNotificationMapper.map(fired(name = "CPU Package"))
        val b = PcAlertNotificationMapper.map(fired(name = "cpu package"))
        val c = PcAlertNotificationMapper.map(fired(name = "GPU Temp"))

        assertEquals("the next alert from the same sensor replaces the last instead of stacking", a.key, b.key)
        assertNotEquals(a.key, c.key)
    }
}
