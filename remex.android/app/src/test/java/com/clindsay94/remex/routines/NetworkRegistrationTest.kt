package com.clindsay94.remex.routines

import androidx.work.ExistingWorkPolicy
import com.clindsay94.remex.routines.home.HomePresence
import com.clindsay94.remex.routines.home.PresenceAction
import com.clindsay94.remex.routines.home.PresenceEventRouter
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
 * while HOME with a leave routine, restart paths re-register, and a network broadcast can never
 * re-register (the S3 review's loop). A fake port stands in for ConnectivityManager and WorkManager.
 */
class NetworkRegistrationTest {
    /**
     * Behaves like ConnectivityService: registering the PendingIntent (again) drops the old request
     * and immediately delivers "available" for the Wi-Fi that already satisfies it.
     */
    private class FakeConnectivity(var onAvailable: () -> Unit = {}) : PresenceRegistrationPort {
        var pendingIntent = false
        var inProcess = false
        var periodic = false
        var registrations = 0
        var periodicSchedules = 0

        override fun registerPendingIntentCallback() {
            pendingIntent = true
            registrations++
            onAvailable()
        }

        override fun unregisterPendingIntentCallback() { pendingIntent = false }

        override fun registerInProcessCallback() { inProcess = true }

        override fun unregisterInProcessCallback() { inProcess = false }

        override fun schedulePeriodicCheck() { periodic = true; periodicSchedules++ }

        override fun cancelPeriodicCheck() { periodic = false }
    }

    private val armedHome = PresenceRegistrationPlan.of(homeSet = true, enabledHomeRoutines = 1, enabledLeaveRoutines = 1, state = PresenceState.HOME)

    /**
     * The receiver + evaluation + plan store, wired the way HomePresence wires them: a network
     * broadcast runs [PresenceEventRouter]; an evaluation never syncs; only a SYNC action applies.
     */
    private class Rig(val plan: () -> PresenceRegistrationPlan) {
        var stored: PresenceRegistrationPlan? = null
        var evaluations = 0
        var broadcasts = 0
        val cm = FakeConnectivity()

        init {
            cm.onAvailable = {
                broadcasts++
                check(broadcasts < 50) { "broadcast -> register loop" }
                deliver(PresenceEventRouter.NETWORK)
            }
        }

        /** As HomePresence.sync: the plan is stored BEFORE registering, so its own "available" sees it armed. */
        fun sync(force: Boolean) {
            val next = plan()
            val previous = stored
            stored = next
            PresenceRegistrar.apply(next, previous, force, cm)
        }

        fun deliver(event: String) {
            for (action in PresenceEventRouter.actionsFor(event, stored?.networkCallback == true)) {
                when (action) {
                    PresenceAction.SYNC_FORCED -> sync(force = true)
                    PresenceAction.EVALUATE -> evaluations++
                    PresenceAction.UNREGISTER_STALE -> cm.unregisterPendingIntentCallback()
                    PresenceAction.FORGET_PRESENCE -> Unit
                }
            }
        }
    }

    @Test
    fun `BLOCKER - one network event causes no registration and exactly one evaluation`() {
        val rig = Rig { armedHome }
        rig.sync(force = false) // first arm: one registration, whose "available" is one evaluation
        assertEquals(1, rig.cm.registrations)
        assertEquals(1, rig.evaluations)
        val before = rig.cm.registrations
        rig.deliver(PresenceEventRouter.NETWORK)
        assertEquals("a network broadcast must never re-register", before, rig.cm.registrations)
        assertEquals(2, rig.evaluations)
    }

    @Test
    fun `an unchanged plan re-registers nothing, however often it is synced`() {
        val rig = Rig { armedHome }
        rig.sync(force = false)
        repeat(10) { rig.sync(force = false) }
        assertEquals(1, rig.cm.registrations)
        assertEquals(1, rig.cm.periodicSchedules)
    }

    @Test
    fun `boot and package replace re-register once, and the loop ends there`() {
        val rig = Rig { armedHome }
        rig.sync(force = false)
        rig.deliver(PresenceEventRouter.BOOT)
        assertEquals(2, rig.cm.registrations)
        rig.deliver(PresenceEventRouter.PACKAGE_REPLACED)
        assertEquals(3, rig.cm.registrations)
        assertTrue(rig.broadcasts <= 3)
    }

    @Test
    fun `evaluations queue behind each other instead of cancelling (APPEND_OR_REPLACE)`() {
        assertEquals(ExistingWorkPolicy.APPEND_OR_REPLACE, HomePresence.EVAL_POLICY)
    }

    @Test
    fun `§12 - nothing armed means no store read, no work, and a stale registration is dropped`() {
        assertEquals(emptyList<PresenceAction>(), PresenceEventRouter.actionsFor(PresenceEventRouter.BOOT, armed = false))
        assertEquals(emptyList<PresenceAction>(), PresenceEventRouter.actionsFor(PresenceEventRouter.PACKAGE_REPLACED, armed = false))
        assertEquals(listOf(PresenceAction.UNREGISTER_STALE), PresenceEventRouter.actionsFor(PresenceEventRouter.NETWORK, armed = false))
        assertEquals(listOf(PresenceAction.EVALUATE), PresenceEventRouter.actionsFor(PresenceEventRouter.NETWORK, armed = true))
        assertEquals(
            listOf(PresenceAction.FORGET_PRESENCE, PresenceAction.SYNC_FORCED, PresenceAction.EVALUATE),
            PresenceEventRouter.actionsFor(PresenceEventRouter.BOOT, armed = true),
        )
    }

    @Test
    fun `§12 - process start reads only the plain armed pref before doing anything`() {
        val src =
            listOf(File("src/main/java"), File("app/src/main/java")).first { it.isDirectory }
                .resolve("com/clindsay94/remex/routines/home/HomePresence.kt").readText()
        val body = src.substringAfter("fun onProcessStart(").substringBefore("fun watch(")
        val armedCheck = body.indexOf("PresencePlanStore.isArmed(app)")
        assertTrue(armedCheck > 0)
        for (costly in listOf("sync(", "startAuthWatch(", "Routines.", "RemexClientManager")) {
            val at = body.indexOf(costly)
            assertTrue("$costly must come after the armed check", at < 0 || at > armedCheck)
        }
    }

    @Test
    fun `no home routine means no registration and no work (battery budget)`() {
        val port = FakeConnectivity().apply { pendingIntent = true; inProcess = true; periodic = true }
        PresenceRegistrar.apply(PresenceRegistrationPlan.of(homeSet = true, enabledHomeRoutines = 0, enabledLeaveRoutines = 0, state = PresenceState.HOME), armedHome, false, port)
        assertFalse(port.pendingIntent)
        assertFalse(port.inProcess)
        assertFalse(port.periodic)
    }

    @Test
    fun `turning the callback on registers it once, turning it off unregisters`() {
        val port = FakeConnectivity()
        val on = PresenceRegistrationPlan.of(true, 1, 0, PresenceState.AWAY)
        val applied = PresenceRegistrar.apply(on, null, false, port)
        assertTrue(port.pendingIntent)
        assertTrue(port.inProcess)
        assertFalse(port.periodic)
        PresenceRegistrar.apply(PresenceRegistrationPlan.NONE, applied, false, port)
        assertFalse(port.pendingIntent)
        assertEquals(1, port.registrations)
    }

    @Test
    fun `a presence change flips only the periodic check, never the PendingIntent`() {
        val port = FakeConnectivity()
        var applied = PresenceRegistrar.apply(armedHome, null, false, port)
        applied = PresenceRegistrar.apply(PresenceRegistrationPlan.of(true, 1, 1, PresenceState.AWAY), applied, false, port)
        assertFalse(port.periodic)
        PresenceRegistrar.apply(armedHome, applied, false, port)
        assertTrue(port.periodic)
        assertEquals(1, port.registrations)
    }

    @Test
    fun `LeaveDetectionScheduling - the periodic check runs only while HOME with a leave routine`() {
        assertTrue(PresenceRegistrationPlan.of(true, 1, 1, PresenceState.HOME).periodicCheck)
        assertFalse(PresenceRegistrationPlan.of(true, 1, 1, PresenceState.AWAY).periodicCheck)
        assertFalse(PresenceRegistrationPlan.of(true, 1, 1, PresenceState.UNKNOWN).periodicCheck)
        assertFalse(PresenceRegistrationPlan.of(true, 1, 0, PresenceState.HOME).periodicCheck)
        assertFalse(PresenceRegistrationPlan.of(false, 1, 1, PresenceState.HOME).periodicCheck)
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
