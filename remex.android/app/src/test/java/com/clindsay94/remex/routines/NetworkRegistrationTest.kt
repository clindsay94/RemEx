package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.home.PresenceRegistrar
import com.clindsay94.remex.routines.home.PresenceRegistrationPlan
import com.clindsay94.remex.routines.home.PresenceRegistrationPort
import com.clindsay94.remex.routines.home.PresenceState
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Spec §13.3 `NetworkRegistrationTest` and `LeaveDetectionSchedulingTest` (R-SYS-15, R-SYS-16, §12):
 * the network callback exists only while a home routine does, the 15-minute leave fallback only
 * while HOME with a leave routine, and every restart path re-registers. A fake port stands in for
 * ConnectivityManager and WorkManager.
 */
class NetworkRegistrationTest {
    private class FakePort : PresenceRegistrationPort {
        var pendingIntent = false
        var inProcess = false
        var periodic = false
        val calls = mutableListOf<String>()

        override fun registerPendingIntentCallback() { pendingIntent = true; calls += "register" }

        override fun unregisterPendingIntentCallback() { pendingIntent = false; calls += "unregister" }

        override fun registerInProcessCallback() { inProcess = true }

        override fun unregisterInProcessCallback() { inProcess = false }

        override fun schedulePeriodicCheck() { periodic = true }

        override fun cancelPeriodicCheck() { periodic = false }
    }

    @Test
    fun `no home routine means no registration and no work (battery budget)`() {
        val port = FakePort().apply { pendingIntent = true; inProcess = true; periodic = true }
        PresenceRegistrar.apply(PresenceRegistrationPlan.of(homeSet = true, enabledHomeRoutines = 0, enabledLeaveRoutines = 0, state = PresenceState.HOME), port)
        assertFalse(port.pendingIntent)
        assertFalse(port.inProcess)
        assertFalse(port.periodic)
        PresenceRegistrar.apply(PresenceRegistrationPlan.of(homeSet = false, enabledHomeRoutines = 2, enabledLeaveRoutines = 1, state = null), port)
        assertFalse(port.pendingIntent)
    }

    @Test
    fun `an enabled home routine registers both callbacks`() {
        val port = FakePort()
        PresenceRegistrar.apply(PresenceRegistrationPlan.of(homeSet = true, enabledHomeRoutines = 1, enabledLeaveRoutines = 0, state = PresenceState.AWAY), port)
        assertTrue(port.pendingIntent)
        assertTrue(port.inProcess)
        assertFalse(port.periodic)
    }

    @Test
    fun `app start, boot, package replace and every edit re-register (idempotently)`() {
        val port = FakePort()
        val plan = PresenceRegistrationPlan.of(homeSet = true, enabledHomeRoutines = 1, enabledLeaveRoutines = 0, state = PresenceState.HOME)
        repeat(4) { PresenceRegistrar.apply(plan, port) }
        // Registering is always done again, never assumed: a reboot or an update dropped it silently.
        assertEquals(List(4) { "register" }, port.calls)
        assertTrue(port.pendingIntent)
    }

    @Test
    fun `the last home routine going away unregisters`() {
        val port = FakePort()
        PresenceRegistrar.apply(PresenceRegistrationPlan.of(true, 1, 1, PresenceState.HOME), port)
        PresenceRegistrar.apply(PresenceRegistrationPlan.of(true, 0, 0, PresenceState.HOME), port)
        assertFalse(port.pendingIntent)
        assertFalse(port.inProcess)
        assertFalse(port.periodic)
    }

    @Test
    fun `LeaveDetectionScheduling - the periodic check runs only while HOME with a leave routine`() {
        assertTrue(PresenceRegistrationPlan.of(true, 1, 1, PresenceState.HOME).periodicCheck)
        assertFalse(PresenceRegistrationPlan.of(true, 1, 1, PresenceState.AWAY).periodicCheck)
        assertFalse(PresenceRegistrationPlan.of(true, 1, 1, PresenceState.UNKNOWN).periodicCheck)
        assertFalse(PresenceRegistrationPlan.of(true, 1, 0, PresenceState.HOME).periodicCheck)
        assertFalse(PresenceRegistrationPlan.of(false, 1, 1, PresenceState.HOME).periodicCheck)
        val port = FakePort()
        PresenceRegistrar.apply(PresenceRegistrationPlan.of(true, 1, 1, PresenceState.HOME), port)
        assertTrue(port.periodic)
        PresenceRegistrar.apply(PresenceRegistrationPlan.of(true, 1, 1, PresenceState.AWAY), port)
        assertFalse(port.periodic)
    }

    @Test
    fun `the manifest re-registers after boot and after an update, through a non-exported receiver`() {
        val manifest = listOf(File("src/main/AndroidManifest.xml"), File("app/src/main/AndroidManifest.xml")).first { it.isFile }.readText()
        val receiver = Regex("""<receiver\s+android:name="\.routines\.home\.RoutineNetworkReceiver"[\s\S]*?</receiver>""").find(manifest)?.value
        requireNotNull(receiver) { "RoutineNetworkReceiver is not declared" }
        assertTrue(receiver.contains("android:exported=\"false\""))
        assertTrue(receiver.contains("android.intent.action.BOOT_COMPLETED"))
        assertTrue(receiver.contains("android.intent.action.MY_PACKAGE_REPLACED"))
        assertTrue(manifest.contains("android.permission.RECEIVE_BOOT_COMPLETED"))
        assertTrue("process start registers through RemexApplication", manifest.contains("android:name=\".RemexApplication\""))
    }
}
