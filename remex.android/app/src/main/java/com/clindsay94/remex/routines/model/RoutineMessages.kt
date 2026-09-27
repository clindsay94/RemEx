package com.clindsay94.remex.routines.model

import org.json.JSONObject

/**
 * One decoded `routine_*` envelope from the PC (routines spec §7.1-§7.3, §7.7, RemEx-pp0rt.3).
 *
 * [RoutineInbound.parse] never throws: the JNI callback that delivers these has nothing above it to
 * catch an exception.
 */
sealed interface RoutineInboundMessage {
    data class SyncResult(val payload: RoutineSyncResultPayload) : RoutineInboundMessage

    data class StepResult(val payload: RoutineStepResultPayload) : RoutineInboundMessage

    data class Notify(val payload: RoutineNotifyPayload) : RoutineInboundMessage

    data class RunReport(val payload: RoutineRunReportPayload) : RoutineInboundMessage

    /**
     * A `routine_*` type this build does not consume - a newer host's addition, or a phone -> host
     * type echoed by mistake. Ignored by design: the prefix forward exists so new types arrive
     * without a router change, and arriving must never crash an older consumer.
     */
    data class Ignored(val type: String?) : RoutineInboundMessage

    /** A known type whose payload could not be read. Logged by the consumer, never thrown. */
    data class Malformed(val type: String) : RoutineInboundMessage
}

/** Decodes host -> phone routine envelopes delivered by `RemexCallback.onRoutineMessage`. */
object RoutineInbound {
    fun parse(json: String?): RoutineInboundMessage {
        val envelope =
            runCatching { JSONObject(json ?: return RoutineInboundMessage.Ignored(null)) }.getOrNull()
                ?: return RoutineInboundMessage.Ignored(null)
        val type = envelope.opt("type") as? String ?: return RoutineInboundMessage.Ignored(null)

        return when (type) {
            RoutineMessageTypes.ROUTINE_SYNC_RESULT ->
                RoutineJson.readSyncResult(envelope.opt("routineSyncResult"))
                    ?.let { RoutineInboundMessage.SyncResult(it) }
            RoutineMessageTypes.ROUTINE_STEP_RESULT ->
                RoutineJson.readStepResult(envelope.opt("routineStepResult"))
                    ?.let { RoutineInboundMessage.StepResult(it) }
            RoutineMessageTypes.ROUTINE_NOTIFY ->
                RoutineJson.readNotify(envelope.opt("routineNotify"))?.let { RoutineInboundMessage.Notify(it) }
            RoutineMessageTypes.ROUTINE_RUN_REPORT ->
                RoutineJson.readRunReport(envelope.opt("routineRunReport"))
                    ?.let { RoutineInboundMessage.RunReport(it) }
            else -> return RoutineInboundMessage.Ignored(type)
        } ?: RoutineInboundMessage.Malformed(type)
    }
}

/**
 * Builds the phone -> host routine envelopes, sent with `RemexCoreClient.SendMessage`: the
 * `{"type": ..., "<slot>": {...}}` shape `ThemeSync.buildEnvelope` uses, with no `protocolVersion`
 * (additive types, no bump, §7.6). The `wire.*.json` fixtures pin the output against what C# reads.
 */
object RoutineOutbound {
    fun routinesSync(payload: RoutinesSyncPayload): String =
        envelope(RoutineMessageTypes.ROUTINES_SYNC, "routinesSync", RoutineJson.write(payload))

    fun stepRequest(payload: RoutineStepRequestPayload): String =
        envelope(RoutineMessageTypes.ROUTINE_STEP_REQUEST, "routineStepRequest", RoutineJson.write(payload))

    fun notifyAck(payload: RoutineNotifyAckPayload): String =
        envelope(RoutineMessageTypes.ROUTINE_NOTIFY_ACK, "routineNotifyAck", RoutineJson.write(payload))

    fun cancel(payload: RoutineCancelPayload): String =
        envelope(RoutineMessageTypes.ROUTINE_CANCEL, "routineCancel", RoutineJson.write(payload))

    fun runRequest(payload: RoutineRunRequestPayload): String =
        envelope(RoutineMessageTypes.ROUTINE_RUN_REQUEST, "routineRunRequest", RoutineJson.write(payload))

    private fun envelope(type: String, slot: String, payload: JSONObject): String =
        JSONObject().put("type", type).put(slot, payload).toString()
}
