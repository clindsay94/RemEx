package com.clindsay94.remex.ui.routines

import com.clindsay94.remex.routines.HOST
import com.clindsay94.remex.routines.RepositoryHarness
import com.clindsay94.remex.routines.RoutineSaveResult
import com.clindsay94.remex.routines.lock
import com.clindsay94.remex.routines.manualRoutine
import com.clindsay94.remex.routines.model.RoutineLimits
import com.clindsay94.remex.routines.model.RoutineStepTypes
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import com.clindsay94.remex.routines.model.RoutineValidator
import com.clindsay94.remex.routines.pcIdleRoutine
import com.clindsay94.remex.routines.shutdown
import com.clindsay94.remex.routines.uuid
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The editor against the real S1c repository over in-memory stores (RemEx-pp0rt.6): R-UX-08
 * (template creates nothing until Save), R-UX-15 (duplicate), R-UX-18 (list order persists), and
 * every offered template becoming a valid routine once its blanks are filled.
 */
class RoutineEditorStoreTest {
    private val mac = "0A:1B:2C:3D:4E:5F"
    private val app = "3f2504e0-4f89-41d3-9a0c-0305e82c3301"

    @Test
    fun templateDoesNotPersistUntilSave() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            repo.load()
            val template = checkNotNull(RoutineTemplates.byId("tpl.game.night"))
            val draft = RoutineDrafts.fromTemplate(template, "Game night", HOST, mac) { "Ready to play." }

            // Opening (and leaving) a template only builds a draft.
            assertTrue(repo.routines.value.isEmpty())
            assertTrue(h.docKv.map.isEmpty())

            val launch = draft.steps.indexOfFirst { it.step.type == RoutineStepTypes.LAUNCH_APP }
            val filled = draft.updateStep(launch, draft.steps[launch].step.copy(appId = app, appLabel = "Steam"))
            val saved = repo.save(filled.toRoutine("unused"))
            assertTrue(saved is RoutineSaveResult.Saved)
            assertEquals(1, repo.routines.value.size)
        }

    @Test
    fun `every offered template is a valid routine once its blanks are filled`() {
        val sensor = RoutineSensorOption("/gpu/0/temperature/0", "GPU Core", "°C", com.clindsay94.remex.ui.telemetry.MetricKind.GPU_TEMP_C, "GPU", 60.0)
        for (template in RoutineTemplates.offered()) {
            val draft = RoutineDrafts.fromTemplate(template, "Name", HOST, mac) { "Body" }
            val withApps =
                draft.steps.foldIndexed(draft) { i, d, s ->
                    if (s.step.type == RoutineStepTypes.LAUNCH_APP) d.updateStep(i, s.step.copy(appId = app, appLabel = "Steam")) else d
                }
            // A health template's sensor is the one blank only the PC's catalog can fill.
            val filled = withApps.trigger?.takeIf { template.sensorPreset != null }?.let { withApps.copy(trigger = RoutineSensorCatalog.choose(it, sensor)) } ?: withApps
            val routine = filled.toRoutine("x").copy(id = uuid(), revision = 1, createdAtUnixMs = 1, updatedAtUnixMs = 1)
            assertEquals(template.id, "ok", RoutineValidator.validateRoutine(routine).reasonCode)
            assertTrue(template.id, RoutineEditorRules.errors(RoutineEditorRules.problems(filled, EditorEnvironment(setOf(app)))).isEmpty())
        }
    }

    @Test
    fun `S5 offers manual, sensor, idle and session templates and features three of them`() {
        val expected = listOf(RoutineTriggerTypes.MANUAL, RoutineTriggerTypes.PC_SENSOR, RoutineTriggerTypes.PC_IDLE, RoutineTriggerTypes.PC_SESSION)
        assertEquals(expected, RoutineTriggerFamilies.offered)
        assertTrue(RoutineTemplates.offered().all { it.trigger.type in expected })
        val s4 = listOf("tpl.home.lock", "tpl.home.sleep", "tpl.priv.unlock", "tpl.power.sleep", "tpl.power.screen")
        assertTrue(RoutineTemplates.offered().map { it.id }.containsAll(s4))
        val s5 = listOf("tpl.health.gpu", "tpl.health.cpu", "tpl.health.ram")
        assertTrue(RoutineTemplates.offered().map { it.id }.containsAll(s5))
        assertTrue(RoutineTemplates.offered().filter { it.id in s5 }.all { it.sensorPreset != null && it.trigger.sensorId == null })
        assertEquals(3, RoutineTemplates.featured(hasNfc = true).size)
        assertEquals(3, RoutineTemplates.featured(hasNfc = false).size)
        // The spec's featured order: Sleep my PC when it's idle is offered now, so it leads; without
        // NFC, Game night follows it.
        assertEquals(listOf("tpl.power.sleep", "tpl.game.night"), RoutineTemplates.featured(hasNfc = false).take(2).map { it.id })
        assertTrue(RoutineTemplates.categories().all { c -> RoutineTemplates.offered().any { it.category == c } })
    }

    @Test
    fun `a picked PC trigger arrives with defaults the validator accepts`() {
        for (type in listOf(RoutineTriggerTypes.PC_IDLE, RoutineTriggerTypes.PC_SESSION, RoutineTriggerTypes.PC_SENSOR)) {
            val fresh = RoutineTriggerFamilies.newTrigger(type)
            // A sensor trigger's sensor comes from the PC's catalog; everything else has a default.
            val trigger = if (type == RoutineTriggerTypes.PC_SENSOR) fresh.copy(sensorId = "/cpu/0/temperature/0", sensorLabel = "CPU Package") else fresh
            val routine =
                com.clindsay94.remex.routines.model.Routine(
                    id = uuid(), name = "x", hostIdentity = HOST, enabled = true, revision = 1, createdAtUnixMs = 1, updatedAtUnixMs = 1,
                    trigger = trigger,
                    steps = listOf(com.clindsay94.remex.routines.model.RoutineStep(type = RoutineStepTypes.POWER, verb = "LOCK")),
                )
            assertEquals(type, "ok", RoutineValidator.validateRoutine(routine).reasonCode)
        }
    }

    @Test
    fun `a template's names and messages are copied in as data`() {
        val template = RoutineTemplates.byId("tpl.game.night")
        assertNotNull(template)
        val draft = RoutineDrafts.fromTemplate(checkNotNull(template), "Soirée jeux", HOST, mac) { "Prêt à jouer." }
        val notify = draft.steps.single { it.step.type == RoutineStepTypes.NOTIFY }.step
        assertEquals("Soirée jeux", draft.name)
        assertEquals("Soirée jeux", notify.title)
        assertEquals("Prêt à jouer.", notify.body)
        assertEquals(mac, draft.steps.single { it.step.type == RoutineStepTypes.WAKE }.step.mac)
        assertEquals("tpl.game.night", draft.templateId)
        assertTrue(draft.isNew)
    }

    @Test
    fun duplicate() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            val original = (repo.save(manualRoutine(lock(), shutdown(), name = "Bedtime")) as RoutineSaveResult.Saved).routine

            val copy = RoutineCopies.copyOf(original) { "$it (copy)" }
            assertNull(copy.id)
            val saved = (repo.save(copy) as RoutineSaveResult.Saved).routine

            assertEquals("Bedtime (copy)", saved.name)
            assertFalse(saved.enabled)
            assertEquals(original.steps, saved.steps)
            assertEquals(original.trigger, saved.trigger)
            assertNotEquals(original.id, saved.id)
            assertEquals(1L, saved.revision)
            assertEquals(2, repo.routines.value.size)
        }

    @Test
    fun `a copy of a 40-character name still fits`() {
        val long = manualRoutine(lock(), name = "x".repeat(RoutineLimits.MAX_NAME_LENGTH))
        val copy = RoutineCopies.copyOf(long) { "$it (copy)" }
        assertEquals(RoutineLimits.MAX_NAME_LENGTH, copy.name?.length)
        assertTrue(copy.name!!.endsWith(" (copy)"))
    }

    @Test
    fun `list order moves within a Runs-on group and survives a restart`() =
        runTest {
            val h = RepositoryHarness()
            val repo = h.repository()
            val a = (repo.save(manualRoutine(lock(), name = "A")) as RoutineSaveResult.Saved).routine
            val pc = (repo.save(pcIdleRoutine()) as RoutineSaveResult.Saved).routine
            val b = (repo.save(manualRoutine(lock(), name = "B")) as RoutineSaveResult.Saved).routine
            val all = repo.routines.value.map { it.routine }

            // B moves up past A; the PC routine between them is in another group and keeps its slot.
            val order = checkNotNull(RoutineOrder.move(all, checkNotNull(b.id), -1))
            assertEquals(listOf(b.id, pc.id, a.id), order)
            assertTrue(repo.reorder(order))

            val reopened = h.repository()
            reopened.load()
            assertEquals(listOf("B", "Bedtime", "A"), reopened.routines.value.map { it.routine.name })

            // The first of a group cannot move up; the last cannot move down.
            val now = reopened.routines.value.map { it.routine }
            assertNull(RoutineOrder.move(now, checkNotNull(b.id), -1))
            assertNull(RoutineOrder.move(now, checkNotNull(a.id), 1))
            assertNull(RoutineOrder.move(now, checkNotNull(pc.id), 1))
        }
}
