package com.clindsay94.remex.ui.screens

import org.json.JSONObject

/**
 * Serializes [PointerSampleData] (and lists thereof) to the JSON wire format expected by the host's
 * `DesktopPointerBatch` message type.
 *
 * Wire format mirrors `Remex.Core.Models.DesktopPointerSample` (C# record). The outer envelope
 * matches `Remex.Core.Messages.RemexMessage` with `type = "desktop_pointer_batch"`.
 *
 * This object is stateless and has no Android framework dependency, making it trivially
 * unit-testable on the JVM.
 */
object RemoteDesktopPointerSerializer {

    /**
     * Wraps one or more [PointerSampleData] items into the `desktop_pointer_batch` JSON envelope
     * that `RemexCoreClient.SendDesktopPointerBatch` expects.
     *
     * @param samples Primary samples to include (each may carry coalesced history).
     * @param streamMappingId Optional stream identifier from `DesktopStreamDescriptor`.
     * @return A JSON string ready to pass to the JNI bridge.
     */
    fun toBatchJson(
            samples: List<PointerSampleData>,
            streamMappingId: String? = null,
    ): String {
        val out = StringBuilder(ESTIMATED_BYTES_PER_SAMPLE * (samples.size + 1))
        out.append('{')
        if (streamMappingId != null) {
            out.append("\"streamMappingId\":")
            appendQuoted(out, streamMappingId)
            out.append(',')
        }
        out.append("\"samples\":[")
        samples.forEachIndexed { index, sample ->
            if (index > 0) out.append(',')
            appendSample(out, sample)
        }
        out.append("]}")
        return out.toString()
    }

    /**
     * One sample as a [JSONObject], for tests.
     *
     * Production does not use this — the batch is built straight into a [StringBuilder] (RemEx-ugvo).
     * It is kept, and deliberately implemented by parsing the REAL writer's output, so the existing
     * schema tests keep working AND now exercise the code that actually ships. Re-implementing the
     * schema here to serve them would have recreated the two-copies-that-must-agree problem this
     * codebase keeps getting bitten by.
     */
    fun sampleToJson(sample: PointerSampleData): JSONObject =
            JSONObject(StringBuilder(ESTIMATED_BYTES_PER_SAMPLE).also { appendSample(it, sample) }.toString())

    /**
     * Bytes reserved per FLAT sample so the builder rarely regrows. Measured against the schema
     * below, with the floats at their widened-double worst case.
     *
     * It deliberately does not try to account for coalesced history, which is unbounded in
     * principle: a batch carrying history will take a few StringBuilder doublings, which is an
     * amortized array copy and still far cheaper than the per-sample object churn this replaced.
     */
    private const val ESTIMATED_BYTES_PER_SAMPLE = 320

    /**
     * Writes one sample, and its coalesced history, in the `DesktopPointerSample` wire schema.
     */
    private fun appendSample(out: StringBuilder, sample: PointerSampleData) {
        out.append("{\"protocolVersion\":1")
        out.append(",\"timestamp\":").append(sample.timestamp)
        out.append(",\"pointerId\":").append(sample.pointerId)
        out.append(",\"deviceKind\":")
        appendQuoted(out, sample.deviceKind.wireValue)
        out.append(",\"toolKind\":")
        appendQuoted(out, sample.toolKind.wireValue)
        out.append(",\"phase\":")
        appendQuoted(out, sample.phase.wireValue)
        appendNumber(out, ",\"logicalX\":", sample.logicalX)
        appendNumber(out, ",\"logicalY\":", sample.logicalY)
        appendNumber(out, ",\"dx\":", sample.dx)
        appendNumber(out, ",\"dy\":", sample.dy)
        appendNumber(out, ",\"pressure\":", sample.pressure)
        sample.hoverDistance?.let { appendNumber(out, ",\"hoverDistance\":", it) }
        sample.tiltX?.let { appendNumber(out, ",\"tiltX\":", it) }
        sample.tiltY?.let { appendNumber(out, ",\"tiltY\":", it) }
        sample.orientation?.let { appendNumber(out, ",\"orientation\":", it) }
        out.append(",\"buttonMask\":").append(sample.buttonMask)

        val history = sample.coalescedHistory
        if (!history.isNullOrEmpty()) {
            out.append(",\"coalescedHistory\":[")
            history.forEachIndexed { index, child ->
                if (index > 0) out.append(',')
                appendSample(out, child)
            }
            out.append(']')
        }
        out.append('}')
    }

    /**
     * Appends `label` then the value as a JSON number.
     *
     * NaN and the infinities are written as `0`, because they are not representable in JSON at all —
     * `JSONObject.put` threw a `JSONException` on them, which would have failed the whole batch. A
     * dropped-to-zero axis on one sample is a far better outcome than losing a stroke, and Android
     * can produce them: a driver reporting no tilt or pressure surfaces as NaN.
     *
     * `Float.toString` is locale-independent, unlike anything built on `String.format`, so this is
     * safe under a comma-decimal locale. That is not a hypothetical — it is the classic way a
     * hand-rolled JSON writer produces `1,5` and breaks the host's parser for some users only.
     */
    private fun appendNumber(out: StringBuilder, label: String, value: Float) {
        out.append(label)
        if (value.isNaN() || value.isInfinite()) out.append('0') else out.append(value.toDouble())
    }

    /** Appends a JSON string literal, escaping what RFC 8259 requires. */
    private fun appendQuoted(out: StringBuilder, value: String) {
        out.append('"')
        for (ch in value) {
            when (ch) {
                '\"' -> out.append("\\\"")
                '\\' -> out.append("\\\\")
                '\n' -> out.append("\\n")
                '\r' -> out.append("\\r")
                '\t' -> out.append("\\t")
                '\b' -> out.append("\\b")
                '\u000C' -> out.append("\\f")
                else ->
                        if (ch < ' ') {
                            // toString(16), not String.format("%04x") — the latter reads the default
                            // Locale, which is the same class of bug this writer is careful to avoid
                            // for numbers, and Android lint flags it under lintVitalRelease.
                            out.append("\\u").append(ch.code.toString(16).padStart(4, '0'))
                        } else {
                            out.append(ch)
                        }
            }
        }
        out.append('"')
    }
}
