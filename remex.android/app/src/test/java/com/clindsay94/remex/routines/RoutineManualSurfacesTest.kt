package com.clindsay94.remex.routines

import com.clindsay94.remex.routines.manual.RoutineManualDecision
import com.clindsay94.remex.routines.manual.RoutineManualEntry
import com.clindsay94.remex.routines.manual.RoutineShortcutPlan
import com.clindsay94.remex.routines.model.RoutineRun
import com.clindsay94.remex.routines.model.RoutineRunOrigins
import com.clindsay94.remex.routines.model.RoutineRunOutcomes
import com.clindsay94.remex.routines.model.RoutineRunSources
import com.clindsay94.remex.routines.model.RoutineTrigger
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * `RoutineManualSurfacesTest` and `RoutineShortcutsTest.dynamic` of spec §13.3 (R-SYS-18, R-UX-24,
 * R-UX-26): each surface starts only the routines it may, a destructive routine goes through the
 * confirm first, and every surface ends in the same repository run (so the same unique work).
 */
class RoutineManualSurfacesTest {
    private val manual = manualRoutine(lock())
    private val destructive = manualRoutine(shutdown())
    private val nfc = manualRoutine(lock()).copy(trigger = RoutineTrigger(type = RoutineTriggerTypes.NFC_TAP))

    @Test
    fun `shortcut and widget run a manual routine`() {
        assertEquals(RoutineManualDecision.RUN, RoutineManualEntry.decide(manual, RoutineRunSources.MANUAL_SHORTCUT, confirmed = false))
        assertEquals(RoutineManualDecision.RUN, RoutineManualEntry.decide(manual, RoutineRunSources.MANUAL_WIDGET, confirmed = false))
    }

    @Test
    fun `a destructive routine confirms first on every surface, then runs`() {
        for (source in listOf(RoutineRunSources.MANUAL_SHORTCUT, RoutineRunSources.MANUAL_WIDGET, RoutineRunSources.MANUAL_APP)) {
            assertEquals(source, RoutineManualDecision.CONFIRM, RoutineManualEntry.decide(destructive, source, confirmed = false))
            assertEquals(source, RoutineManualDecision.RUN, RoutineManualEntry.decide(destructive, source, confirmed = true))
        }
        val destructiveTag = destructive.copy(trigger = RoutineTrigger(type = RoutineTriggerTypes.NFC_TAP))
        assertEquals(RoutineManualDecision.CONFIRM, RoutineManualEntry.decide(destructiveTag, RoutineRunSources.NFC_TAP, confirmed = false))
    }

    @Test
    fun `a surface never starts a routine it does not own`() {
        assertEquals(RoutineManualDecision.WRONG_TRIGGER, RoutineManualEntry.decide(nfc, RoutineRunSources.MANUAL_SHORTCUT, false))
        assertEquals(RoutineManualDecision.WRONG_TRIGGER, RoutineManualEntry.decide(nfc, RoutineRunSources.MANUAL_WIDGET, false))
        assertEquals(RoutineManualDecision.WRONG_TRIGGER, RoutineManualEntry.decide(manual, RoutineRunSources.NFC_TAP, false))
        assertEquals(RoutineManualDecision.WRONG_TRIGGER, RoutineManualEntry.decide(pcIdleRoutine(), RoutineRunSources.MANUAL_SHORTCUT, false))
        assertEquals(RoutineManualDecision.NOT_FOUND, RoutineManualEntry.decide(null, RoutineRunSources.MANUAL_SHORTCUT, false))
        assertEquals(RoutineManualDecision.RUN, RoutineManualEntry.decide(nfc, RoutineRunSources.NFC_TAP, false))
    }

    @Test
    fun `only manual routines can be pinned`() {
        assertTrue(RoutineManualEntry.isPinnable(manual))
        assertFalse(RoutineManualEntry.isPinnable(nfc))
        assertFalse(RoutineManualEntry.isPinnable(pcIdleRoutine()))
        assertFalse(RoutineManualEntry.isPinnable(manual.copy(id = null)))
    }

    @Test
    fun `every manual surface starts through the same repository run`() {
        // Shortcut, widget, NFC tap and confirm all call RoutineManualSurfaces.start, which is the
        // only place that calls repository.run for them: one unique work name per routine.
        val dir = listOf(File("src/main/java"), File("app/src/main/java")).first { it.isDirectory }
        val surfaces = File(dir, "com/clindsay94/remex/routines/manual/RoutineManualSurfaces.kt").readText()
        assertTrue(surfaces.contains("repository.run(routineId, source)"))
        for (path in listOf(
            "routines/manual/RoutineShortcutActivity.kt",
            "routines/manual/RoutineConfirmActivity.kt",
            "routines/nfc/NfcRoutineActivity.kt",
            "routines/widget/RoutineWidget.kt",
        )) {
            val src = File(dir, "com/clindsay94/remex/$path").readText()
            assertTrue("$path must start runs through RoutineManualSurfaces.start", src.contains("RoutineManualSurfaces.start("))
            assertFalse("$path must not start runs itself", src.contains("repository.run("))
        }
    }

    private fun run(routineId: String?, at: Long, origin: String = RoutineRunOrigins.PHONE) =
        RoutineRun(runId = uuid(), routineId = routineId, origin = origin, triggeredAtUnixMs = at, outcome = RoutineRunOutcomes.SUCCEEDED)

    @Test
    fun `dynamic shortcuts are the four most recently run manual routines`() {
        val routines = (1..6).map { manualRoutine(lock(), name = "R$it") }
        val history =
            routines.mapIndexed { i, r -> run(r.id, 1_000L + i) } +
                run(routines[0].id, 5_000L) +
                run(nfc.id, 9_000L) +
                run(routines[1].id, 9_500L, origin = RoutineRunOrigins.PC)
        val ids = RoutineShortcutPlan.dynamicIds(routines + nfc, history)
        assertEquals(listOf(routines[0].id, routines[5].id, routines[4].id, routines[3].id), ids)
    }

    @Test
    fun `never-run and deleted routines get no dynamic shortcut`() {
        val kept = manualRoutine(lock())
        val history = listOf(run(kept.id, 10), run(uuid(), 20))
        assertEquals(listOf(kept.id), RoutineShortcutPlan.dynamicIds(listOf(kept, manualRoutine(lock())), history))
        assertEquals(emptyList<String>(), RoutineShortcutPlan.dynamicIds(listOf(kept), history, max = 0))
    }

    @Test
    fun `shortcut ids round-trip`() {
        val id = manual.id!!
        assertEquals(id, RoutineShortcutPlan.routineIdOf(RoutineShortcutPlan.shortcutId(id)))
        assertEquals(null, RoutineShortcutPlan.routineIdOf("tile-lock"))
    }
}
