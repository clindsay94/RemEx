package com.clindsay94.remex.routines

import androidx.test.ext.junit.runners.AndroidJUnit4
import com.clindsay94.remex.routines.model.RoutineInbound
import com.clindsay94.remex.routines.model.RoutineInboundMessage
import com.clindsay94.remex.routines.model.RoutineJson
import com.clindsay94.remex.routines.model.RoutineValidator
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The routine reader on ANDROID'S PLATFORM org.json (S1a Kotlin review finding 2, RemEx-pp0rt.5).
 *
 * The JVM unit tests run the reader on Maven's org.json, which is strict. The app runs on the
 * platform copy, which is lenient: it accepts comments, single quotes, unquoted keys and leading-zero
 * numbers, and it coerces nothing itself but hands back different boxed types in places. This is an
 * instrumented test because the project does not use Robolectric (decision on RemEx-ivkq); it runs
 * on a device or emulator with `connectedReleaseAndroidTest`.
 *
 * What it pins: the reader's strict-type rule holds on the platform parser too (a wrong-typed field
 * is a malformed routine, never a coerced value), lenient syntax never throws, and whatever the
 * lenient parser accepted is written back out as a document that reads identically.
 */
@RunWith(AndroidJUnit4::class)
class RoutineJsonPlatformParseTest {
    private fun set(routine: String) = """{"schemaVersion":1,"routines":[$routine]}"""

    private val valid =
        """{"id":"6b86b273-ff34-4ce1-ad6b-804eff5a3f57","name":"Game time","hostIdentity":"9f2c4be07a1d33e5",
           "enabled":true,"revision":1,"trigger":{"type":"manual"},
           "steps":[{"type":"wake","mac":"0A:1B:2C:3D:4E:5F","broadcastIp":"192.168.1.255","port":9},
                    {"type":"delay","seconds":5},{"type":"power","verb":"LOCK"}],
           "createdAtUnixMs":1790000000000,"updatedAtUnixMs":1790000000000}"""

    @Test
    fun a_valid_routine_reads_the_same_on_the_platform_parser() {
        val parsed = checkNotNull(RoutineJson.parseSet(set(valid)))
        val routine = parsed.routines.orEmpty().single()
        assertFalse(routine.isMalformed)
        assertEquals(9, routine.steps?.get(0)?.port)
        assertEquals(1790000000000L, routine.createdAtUnixMs)
        assertTrue(RoutineValidator.validateRoutine(routine).isValid)
    }

    @Test
    fun wrong_json_types_are_malformed_not_coerced() {
        val cases =
            listOf(
                valid.replace("\"enabled\":true", "\"enabled\":\"true\""),
                valid.replace("\"seconds\":5", "\"seconds\":5.5"),
                valid.replace("\"port\":9", "\"port\":\"9\""),
                valid.replace("\"name\":\"Game time\"", "\"name\":17"),
            )
        for (case in cases) {
            val routine = checkNotNull(RoutineJson.parseSet(set(case))).routines.orEmpty().single()
            assertTrue("should be malformed: $case", routine.isMalformed)
            assertEquals("invalid_field", RoutineValidator.validateRoutine(routine).reasonCode)
        }
    }

    @Test
    fun lenient_syntax_never_throws_and_rewrites_to_a_document_that_reads_the_same() {
        val lenient =
            listOf(
                "{schemaVersion:1, routines:[$valid]}",
                "{'schemaVersion':1,'routines':[$valid]} ",
                "{\"schemaVersion\":1 /* a comment */,\"routines\":[$valid]}",
                set(valid.replace("\"port\":9", "\"port\":09")),
            )
        for (text in lenient) {
            val first = RoutineJson.parseSet(text) ?: continue
            val rewritten = RoutineJson.write(first).toString()
            assertEquals(first, RoutineJson.parseSet(rewritten))
        }
    }

    @Test
    fun inbound_messages_never_throw_on_the_platform_parser() {
        val inputs =
            listOf(
                null,
                "",
                "[]",
                "{type:routine_step_result}",
                """{"type":"routine_step_result","routineStepResult":{"runId":5}}""",
                """{"type":"routine_run_report","routineRunReport":{"runs":[null]}}""",
            )
        for (input in inputs) assertNotNull(RoutineInbound.parse(input))
        val ok =
            RoutineInbound.parse(
                """{"type":"routine_step_result","routineStepResult":{"runId":"r","stepIndex":1,"outcome":"succeeded","countdownShown":true}}"""
            )
        assertTrue(ok is RoutineInboundMessage.StepResult)
    }
}
