package com.clindsay94.remex.routines.model

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The Kotlin half of the C# <-> Kotlin wire round trip (routines spec §13.1, RemEx-pp0rt.3).
 *
 * The `wire.*.json` fixtures are what C# RoutineWireRoundTripTests reads and re-emits as the same
 * tree. Here each one is read into the Kotlin model and written back, and must come out as the same
 * tree - so a field name, a nullability or a default that differs between the two mirrors fails on
 * both sides. For the phone -> host types the envelope the phone actually sends is compared whole.
 */
class RoutineWireFixtureTest {

    private fun readSlot(type: String, slot: Any?): JSONObject? =
        when (type) {
            RoutineMessageTypes.ROUTINES_SYNC -> RoutineJson.readRoutinesSync(slot)?.let(RoutineJson::write)
            RoutineMessageTypes.ROUTINE_SYNC_RESULT -> RoutineJson.readSyncResult(slot)?.let(RoutineJson::write)
            RoutineMessageTypes.ROUTINE_STEP_REQUEST -> RoutineJson.readStepRequest(slot)?.let(RoutineJson::write)
            RoutineMessageTypes.ROUTINE_STEP_RESULT -> RoutineJson.readStepResult(slot)?.let(RoutineJson::write)
            RoutineMessageTypes.ROUTINE_NOTIFY -> RoutineJson.readNotify(slot)?.let(RoutineJson::write)
            RoutineMessageTypes.ROUTINE_NOTIFY_ACK -> RoutineJson.readNotifyAck(slot)?.let(RoutineJson::write)
            RoutineMessageTypes.ROUTINE_RUN_REPORT -> RoutineJson.readRunReport(slot)?.let(RoutineJson::write)
            RoutineMessageTypes.ROUTINE_CANCEL -> RoutineJson.readCancel(slot)?.let(RoutineJson::write)
            RoutineMessageTypes.ROUTINE_RUN_REQUEST -> RoutineJson.readRunRequest(slot)?.let(RoutineJson::write)
            else -> error("no reader for $type")
        }

    @Test
    fun everyRoutineMessageTypeHasAFixture() {
        assertEquals(
            listOf(
                RoutineMessageTypes.ROUTINE_CANCEL, RoutineMessageTypes.ROUTINE_NOTIFY,
                RoutineMessageTypes.ROUTINE_NOTIFY_ACK, RoutineMessageTypes.ROUTINE_RUN_REPORT,
                RoutineMessageTypes.ROUTINE_RUN_REQUEST, RoutineMessageTypes.ROUTINE_STEP_REQUEST,
                RoutineMessageTypes.ROUTINE_STEP_RESULT, RoutineMessageTypes.ROUTINE_SYNC_RESULT,
                RoutineMessageTypes.ROUTINES_SYNC,
            ).sorted(),
            RoutineFixtures.wire.map { it.type }.sorted(),
        )
    }

    @Test
    fun everyFixtureReadsAndReEmitsAsTheSameTree() {
        val failures =
            RoutineFixtures.wire.mapNotNull { (file, type, slot) ->
                val envelope = RoutineFixtures.json(file)
                assertEquals(type, envelope.getString("type"))
                val expected = envelope.getJSONObject(slot)
                val emitted = readSlot(type, expected)
                when {
                    emitted == null -> "$file: the Kotlin reader refused the payload"
                    !RoutineFixtures.treeEquals(expected, emitted) -> "$file: emitted\n$emitted\nexpected\n$expected"
                    else -> null
                }
            }
        assertTrue(failures.joinToString("\n\n"), failures.isEmpty())
    }

    @Test
    fun thePhoneToHostEnvelopesAreExactlyTheFixtures() {
        fun check(file: String, built: String) {
            assertTrue("$file: built\n$built", RoutineFixtures.treeEquals(RoutineFixtures.json(file), JSONObject(built)))
        }

        val sync = RoutineJson.readRoutinesSync(RoutineFixtures.json("wire.routines_sync.json").get("routinesSync"))!!
        check("wire.routines_sync.json", RoutineOutbound.routinesSync(sync))

        val step = RoutineJson.readStepRequest(RoutineFixtures.json("wire.routine_step_request.json").get("routineStepRequest"))!!
        check("wire.routine_step_request.json", RoutineOutbound.stepRequest(step))

        check(
            "wire.routine_notify_ack.json",
            RoutineOutbound.notifyAck(
                RoutineNotifyAckPayload(listOf("e2b7c3d4-5a6f-4b8c-9d0e-1f2a3b4c5d6e", "f3c8d4e5-6b7a-4c9d-8e1f-2a3b4c5d6e7f")),
            ),
        )
        check(
            "wire.routine_cancel.json",
            RoutineOutbound.cancel(RoutineCancelPayload("0d4c9a51-7f3b-4e2a-9c61-3b5e8d2f1a70", RoutineCancelReasons.USER)),
        )
        check(
            "wire.routine_run_request.json",
            RoutineOutbound.runRequest(
                RoutineRunRequestPayload(
                    runId = "0d4c9a51-7f3b-4e2a-9c61-3b5e8d2f1a70",
                    routineId = "5a9d0f21-1c3e-4b7a-8e2f-6c4d3b2a1f00",
                    testRun = true,
                    source = RoutineRunSources.MANUAL_APP,
                ),
            ),
        )
    }

    @Test
    fun theSyncFixtureCarriesTypedRoutines() {
        val sync = RoutineJson.readRoutinesSync(RoutineFixtures.json("wire.routines_sync.json").get("routinesSync"))
        assertNotNull(sync)
        assertEquals(7L, sync!!.revision)
        assertEquals(2, sync.routines!!.size)
        assertEquals(85.5, sync.routines!![1].trigger!!.threshold!!, 0.0)
        assertTrue(sync.routines!!.all { RoutineValidator.validateRoutine(it).isValid })
    }

    @Test
    fun aNullListElementRejectsThePayloadAsInCSharp() {
        val cases = RoutineFixtures.rejected
        assertTrue(cases.size >= 5)
        for ((file, type, slot) in cases) {
            val envelope = RoutineFixtures.json(file)
            assertEquals(null, readSlot(type, envelope.get(slot)))
            // routine_notify_ack is phone -> host, so the inbound decoder ignores it by design.
            if (type != RoutineMessageTypes.ROUTINE_NOTIFY_ACK) {
                assertEquals(RoutineInboundMessage.Malformed(type), RoutineInbound.parse(envelope.toString()))
            }
        }
    }

    @Test
    fun runRecordsWithEveryNestedKeyReEmitAsTheSameTree() {
        val expected = org.json.JSONArray(RoutineFixtures.text(RoutineFixtures.routineRunsFile))
        val emitted = org.json.JSONArray()
        for (i in 0 until expected.length()) {
            val run = RoutineJson.readRun(expected.get(i))
            assertNotNull("run $i was refused", run)
            emitted.put(RoutineJson.write(run!!))
        }
        assertTrue("emitted\n$emitted", RoutineFixtures.treeEquals(expected, emitted))

        val second = RoutineJson.readRun(expected.get(1))!!
        assertEquals("client-7f3a9c", second.ownerClientId)
        assertEquals("2026-08-27", second.reasonArgs!!.date)
        assertEquals("Home (router 192.168.1.1)", RoutineJson.readRun(expected.get(0))!!.sourceDetail!!.homeLabel)
    }

    @Test
    fun theRunReportKeepsEveryNestedField() {
        val report = RoutineJson.readRunReport(RoutineFixtures.json("wire.routine_run_report.json").get("routineRunReport"))!!
        val run = report.runs!!.single()
        assertEquals(43L, run.seq)
        assertEquals("°C", run.sourceDetail!!.unit)
        assertEquals(listOf("countdown_unseen", "notify_queued"), run.attributes)
        assertEquals(RoutineStepStatuses.CANCELLED, run.steps!![1].status)
        assertEquals(RoutineCancelledBy.PHONE, run.countdown!!.cancelledBy)
    }
}
