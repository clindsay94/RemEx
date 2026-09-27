package com.clindsay94.remex.ui.routines

import com.clindsay94.remex.routines.HOST
import com.clindsay94.remex.routines.model.RoutineStep
import com.clindsay94.remex.routines.model.RoutineStepTypes
import com.clindsay94.remex.routines.model.RoutineTrigger
import com.clindsay94.remex.routines.model.RoutineTriggerTypes
import com.clindsay94.remex.ui.telemetry.MetricKind
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The `pc.sensor` editor pieces (routines S5, §6.3, §6.5, spec 4.1): the catalog parsed from the PC's
 * telemetry frame, the template preselection by kind then name, the limits, and the editor problems.
 */
class RoutineSensorCatalogTest {
    private val frame =
        """
        {"sensors":[
          {"id":"/gpu-nvidia/0/temperature/0","name":"GPU Core","unit":"°C","kind":"GpuTempC","group":"GPU","value":61.5},
          {"id":"/amdcpu/0/temperature/2","name":"Core (Tctl/Tdie)","unit":"°C","group":"CPU","value":55},
          {"id":"/ram/load/0","name":"Memory","unit":"%","kind":"RamLoad","group":"Memory","value":42},
          {"id":"","name":"No id","unit":"%","value":1},
          {"id":"/x","name":"","unit":"%","value":1},
          {"id":"/gpu-nvidia/0/temperature/0","name":"Duplicate","unit":"°C","value":1}
        ]}
        """.trimIndent()

    @Test
    fun `the catalog keeps the PC's own ids, drops unnamed and duplicate readings, and sorts by group`() {
        val options = checkNotNull(RoutineSensorCatalog.parse(frame))
        assertEquals(listOf("/amdcpu/0/temperature/2", "/gpu-nvidia/0/temperature/0", "/ram/load/0"), options.map { it.id })
        assertEquals("GPU Core", options.single { it.id == "/gpu-nvidia/0/temperature/0" }.name)
        assertEquals(MetricKind.GPU_TEMP_C, options.single { it.id == "/gpu-nvidia/0/temperature/0" }.kind)
    }

    @Test
    fun `no frame or an unreadable one is an unknown catalog`() {
        assertNull(RoutineSensorCatalog.parse(null))
        assertNull(RoutineSensorCatalog.parse("not json"))
    }

    @Test
    fun `templates preselect by kind first, then by name and unit`() {
        val options = RoutineSensorCatalog.parse(frame)
        assertEquals("/gpu-nvidia/0/temperature/0", RoutineSensorCatalog.preselect(options, RoutineSensorPreset.GPU_TEMP)?.id)
        // No CpuTempC stamp on this PC: the name and unit find it.
        assertEquals("/amdcpu/0/temperature/2", RoutineSensorCatalog.preselect(options, RoutineSensorPreset.CPU_TEMP)?.id)
        assertEquals("/ram/load/0", RoutineSensorCatalog.preselect(options, RoutineSensorPreset.RAM_LOAD)?.id)
        assertNull(RoutineSensorCatalog.preselect(emptyList(), RoutineSensorPreset.RAM_LOAD))
    }

    @Test
    fun `choosing a sensor stores its id and a label of at most 64 characters`() {
        val long = RoutineSensorOption("/id", "x".repeat(80), "°C", MetricKind.TEMP_C, "", 1.0)
        val trigger = RoutineSensorCatalog.choose(RoutineTriggerFamilies.newTrigger(RoutineTriggerTypes.PC_SENSOR), long)
        assertEquals("/id", trigger.sensorId)
        assertEquals(64, trigger.sensorLabel?.length)
    }

    @Test
    fun `limits accept either decimal separator and nothing that is not a number`() {
        assertEquals(85.5, RoutineSensorCatalog.parseLimit("85,5")!!, 0.0)
        assertEquals(-5.0, RoutineSensorCatalog.parseLimit(" -5 ")!!, 0.0)
        assertNull(RoutineSensorCatalog.parseLimit(""))
        assertNull(RoutineSensorCatalog.parseLimit("-"))
        assertNull(RoutineSensorCatalog.parseLimit("NaN"))
        assertEquals("85", RoutineSensorCatalog.formatLimit(85.0))
        assertEquals("85.5", RoutineSensorCatalog.formatLimit(85.5))
    }

    private fun draft(trigger: RoutineTrigger) =
        RoutineDrafts.blank(HOST).copy(
            trigger = trigger,
            steps = listOf(DraftStep(1, RoutineStep(type = RoutineStepTypes.NOTIFY, target = "phone", title = "Hot"))),
        )

    private fun codes(trigger: RoutineTrigger, env: EditorEnvironment = EditorEnvironment()) =
        RoutineEditorRules.problems(draft(trigger), env).filter { it.target == ProblemTarget.Trigger }.map { it.code }

    @Test
    fun `a sensor trigger needs a sensor, a numeric limit and a hold of 5 to 600 seconds`() {
        val fresh = RoutineTriggerFamilies.newTrigger(RoutineTriggerTypes.PC_SENSOR)
        assertEquals(listOf(EditorProblemCode.CHOOSE_SENSOR), codes(fresh))

        val chosen = fresh.copy(sensorId = "/gpu-nvidia/0/temperature/0", sensorLabel = "GPU Core")
        assertTrue(codes(chosen).isEmpty())
        assertEquals(listOf(EditorProblemCode.SENSOR_LIMIT), codes(chosen.copy(threshold = null)))
        assertEquals(listOf(EditorProblemCode.SENSOR_SUSTAIN), codes(chosen.copy(sustainSeconds = 4)))
        assertEquals(listOf(EditorProblemCode.SENSOR_SUSTAIN), codes(chosen.copy(sustainSeconds = 601)))
        assertTrue(codes(chosen.copy(sustainSeconds = 5)).isEmpty())
        assertTrue(codes(chosen.copy(sustainSeconds = 600)).isEmpty())
    }

    @Test
    fun `a sensor the connected PC does not report is flagged, an unknown catalog is not`() {
        val chosen = RoutineTriggerFamilies.newTrigger(RoutineTriggerTypes.PC_SENSOR).copy(sensorId = "/gone", sensorLabel = "Old GPU")
        val env = EditorEnvironment(sensors = RoutineSensorCatalog.parse(frame))
        assertEquals(listOf(EditorProblemCode.SENSOR_MISSING), codes(chosen, env))
        assertTrue(codes(chosen).isEmpty())
    }
}
