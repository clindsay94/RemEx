package com.clindsay94.remex.ui.routines

import com.clindsay94.remex.routines.RoutineActionTokens
import com.clindsay94.remex.routines.RoutineSyncStates
import com.clindsay94.remex.routines.model.Routine
import com.clindsay94.remex.routines.model.RoutinePowerVerbs
import com.clindsay94.remex.routines.model.RoutineSensorDirections
import com.clindsay94.remex.routines.model.RoutineStep
import com.clindsay94.remex.routines.model.RoutineStepTypes
import com.clindsay94.remex.routines.model.RoutineTrigger
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The phone-side fixes from the S1/S4 live pass of 2026-09-27 (RemEx-pp0rt.17). */
class RoutineLiveFixesTest {
    @Test
    fun `the power picker offers only what the PC advertised, never WAKEONLAN`() {
        val offered = RoutineStepText.offeredPowerVerbs(listOf(RoutinePowerVerbs.LOCK, RoutinePowerVerbs.SHUTDOWN, "WAKEONLAN"))
        assertEquals(listOf(RoutinePowerVerbs.LOCK, RoutinePowerVerbs.SHUTDOWN), offered)
        assertFalse(RoutineStepText.powerVerbs.contains("WAKEONLAN"))
    }

    @Test
    fun `an unknown capability offers everything, and a step keeps its own verb listed`() {
        assertEquals(RoutineStepText.powerVerbs, RoutineStepText.offeredPowerVerbs(null))
        val kept = RoutineStepText.offeredPowerVerbs(listOf(RoutinePowerVerbs.LOCK), current = RoutinePowerVerbs.SLEEP)
        assertEquals(listOf(RoutinePowerVerbs.LOCK, RoutinePowerVerbs.SLEEP), kept)
    }

    @Test
    fun `routinePowerVerbs is parsed from host_info and WAKEONLAN is dropped`() {
        assertEquals(
            listOf(RoutinePowerVerbs.LOCK, RoutinePowerVerbs.MONITOR_OFF),
            RoutinePowerVerbsCapability.parse("""{"routinePowerVerbs":["LOCK","MONITOROFF","WAKEONLAN"]}"""),
        )
        assertNull("an older PC that does not say", RoutinePowerVerbsCapability.parse("""{"supportsRoutines":true}"""))
        assertNull("unparseable", RoutinePowerVerbsCapability.parse("not json"))
    }

    @Test
    fun `a PC rejection's detail path names the step, so the message has its action`() {
        val routine =
            Routine(
                id = "r1",
                trigger = RoutineTrigger(type = RoutineTriggerTypes.PC_IDLE, idleMinutes = 10),
                steps = listOf(RoutineStep(type = RoutineStepTypes.POWER, verb = RoutinePowerVerbs.LOCK), RoutineStep(type = RoutineStepTypes.POWER, verb = RoutinePowerVerbs.SLEEP)),
            )
        val step = RoutineSyncStates.stepAt(routine, "steps[1].verb")
        assertEquals(RoutinePowerVerbs.SLEEP, RoutineActionTokens.of(step))
        assertNull(RoutineSyncStates.stepAt(routine, "trigger.sensorId"))
        assertNull(RoutineSyncStates.stepAt(routine, "steps[9].verb"))
        assertNull(RoutineSyncStates.stepAt(routine, null))
    }

    @Test
    fun `a template message follows the limit and hold time until the user edits it`() {
        val template = RoutineTemplates.byId("tpl.health.ram")!!
        val body = { res: Int, trigger: RoutineTrigger? -> "over ${RoutineTemplateMessages.limitText(trigger, "%")} for ${trigger?.sustainSeconds}s ($res)" }
        val opened = RoutineDrafts.fromTemplate(template, "RAM", "host", null) { res -> body(res, template.trigger) }
        val notifyIndex = opened.steps.indexOfFirst { it.step.type == RoutineStepTypes.NOTIFY }
        assertTrue(opened.steps[notifyIndex].step.body!!.startsWith("over 90% for 120s"))

        val raised = RoutineTemplateMessages.rederive(opened, opened.copy(trigger = opened.trigger!!.copy(threshold = 95.0)), body)
        assertTrue(raised.steps[notifyIndex].step.body!!.startsWith("over 95% for 120s"))

        val edited = raised.updateStep(notifyIndex, raised.steps[notifyIndex].step.copy(body = "Mine"))
        val again = RoutineTemplateMessages.rederive(edited, edited.copy(trigger = edited.trigger!!.copy(sustainSeconds = 60)), body)
        assertEquals("Mine", again.steps[notifyIndex].step.body)
    }

    @Test
    fun `the limit text keeps a percent sign on the number and spaces other units`() {
        val trigger = RoutineTrigger(type = RoutineTriggerTypes.PC_SENSOR, direction = RoutineSensorDirections.ABOVE, threshold = 85.0)
        assertEquals("85 °C", RoutineTemplateMessages.limitText(trigger, "°C"))
        assertEquals("85%", RoutineTemplateMessages.limitText(trigger, "%"))
        assertEquals("85", RoutineTemplateMessages.limitText(trigger, null))
    }
}
