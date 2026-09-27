package com.clindsay94.remex.routines.model

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test

/**
 * `onRoutineMessage` decoding (routines spec §7.7, §13.3 `RoutineMessageRoutingTest`, RemEx-pp0rt.3):
 * dispatch by type, unknown `routine_*` types ignored rather than crashing, and malformed payloads
 * reported rather than thrown - the JNI callback has nothing above it to catch an exception.
 */
class RoutineMessageRoutingTest {

    private fun fixtureEnvelope(type: String) = RoutineFixtures.text("wire.$type.json")

    @Test
    fun eachHostToPhoneTypeDecodesToItsMessage() {
        assertTrue(RoutineInbound.parse(fixtureEnvelope("routine_sync_result")) is RoutineInboundMessage.SyncResult)
        assertTrue(RoutineInbound.parse(fixtureEnvelope("routine_step_result")) is RoutineInboundMessage.StepResult)
        assertTrue(RoutineInbound.parse(fixtureEnvelope("routine_notify")) is RoutineInboundMessage.Notify)
        assertTrue(RoutineInbound.parse(fixtureEnvelope("routine_run_report")) is RoutineInboundMessage.RunReport)
    }

    @Test
    fun theDecodedPayloadIsTheFixturesPayload() {
        val result = RoutineInbound.parse(fixtureEnvelope("routine_step_result")) as RoutineInboundMessage.StepResult
        assertEquals(RoutineStepOutcomes.CANCELLED, result.payload.outcome)
        assertEquals(RoutineReasonCodes.CANCELLED_ON_PHONE, result.payload.reasonCode)
        assertEquals(2, result.payload.stepIndex)
    }

    @Test
    fun anUnknownRoutineTypeIsIgnoredNotACrash() {
        assertEquals(
            RoutineInboundMessage.Ignored("routine_from_the_future"),
            RoutineInbound.parse("""{"type":"routine_from_the_future","routineFromTheFuture":{"x":1}}"""),
        )
    }

    @Test
    fun aPhoneToHostTypeArrivingHereIsIgnored() {
        assertEquals(
            RoutineInboundMessage.Ignored(RoutineMessageTypes.ROUTINE_CANCEL),
            RoutineInbound.parse(fixtureEnvelope("routine_cancel")),
        )
    }

    @Test
    fun garbageNeverThrows() {
        assertEquals(RoutineInboundMessage.Ignored(null), RoutineInbound.parse(null))
        assertEquals(RoutineInboundMessage.Ignored(null), RoutineInbound.parse("not json"))
        assertEquals(RoutineInboundMessage.Ignored(null), RoutineInbound.parse("""{"type":5}"""))
        assertEquals(
            RoutineInboundMessage.Malformed(RoutineMessageTypes.ROUTINE_STEP_RESULT),
            RoutineInbound.parse("""{"type":"routine_step_result","routineStepResult":{"stepIndex":"two"}}"""),
        )
        assertEquals(
            RoutineInboundMessage.Malformed(RoutineMessageTypes.ROUTINE_NOTIFY),
            RoutineInbound.parse("""{"type":"routine_notify"}"""),
        )
    }

    @Test
    fun everyHostToPhoneTypeCarriesTheForwardedPrefix() {
        // The native router forwards by this prefix (AndroidNativeExports.OnNativeMessageReceived); a
        // host -> phone type outside it would be dropped before it ever reached onRoutineMessage.
        listOf(
            RoutineMessageTypes.ROUTINE_SYNC_RESULT,
            RoutineMessageTypes.ROUTINE_STEP_RESULT,
            RoutineMessageTypes.ROUTINE_NOTIFY,
            RoutineMessageTypes.ROUTINE_RUN_REPORT,
        ).forEach { assertTrue(it, it.startsWith(RoutineMessageTypes.HOST_TO_PHONE_PREFIX)) }
    }

    @Test
    fun theNativeRouterStillForwardsTheRoutinePrefix() {
        // The C# HostToClientRoutingTests is the authoritative guard; this reads the same router from
        // the Kotlin side so a phone-only run notices too. Skipped without the repo root.
        val root = System.getProperty("remex.repoRoot")?.let(::File)
        val router = root?.let { File(it, "remex.core/Native/AndroidNativeExports.cs") }
        assumeTrue(router != null && router.isFile)
        val source = router!!.readText()
        assertTrue(source.contains("StartsWith(\"routine_\", StringComparison.Ordinal)"))
        assertTrue(source.contains("\"onRoutineMessage\""))
    }
}
