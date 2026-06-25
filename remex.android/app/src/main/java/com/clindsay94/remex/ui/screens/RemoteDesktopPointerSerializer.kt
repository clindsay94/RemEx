package com.clindsay94.remex.ui.screens

import org.json.JSONArray
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
        return JSONObject().apply {
            if (streamMappingId != null) put("streamMappingId", streamMappingId)
            put(
                    "samples",
                    JSONArray().also { arr ->
                        samples.forEach { arr.put(sampleToJson(it)) }
                    }
            )
        }.toString()
    }

    /**
     * Serializes a single [PointerSampleData] to a [JSONObject] matching the `DesktopPointerSample`
     * wire schema.
     */
    fun sampleToJson(sample: PointerSampleData): JSONObject =
            JSONObject().apply {
                put("protocolVersion", 1)
                put("timestamp", sample.timestamp)
                put("pointerId", sample.pointerId)
                put("deviceKind", sample.deviceKind.wireValue)
                put("toolKind", sample.toolKind.wireValue)
                put("phase", sample.phase.wireValue)
                put("logicalX", sample.logicalX.toDouble())
                put("logicalY", sample.logicalY.toDouble())
                put("dx", sample.dx.toDouble())
                put("dy", sample.dy.toDouble())
                put("pressure", sample.pressure.toDouble())
                sample.hoverDistance?.let { put("hoverDistance", it.toDouble()) }
                sample.tiltX?.let { put("tiltX", it.toDouble()) }
                sample.tiltY?.let { put("tiltY", it.toDouble()) }
                sample.orientation?.let { put("orientation", it.toDouble()) }
                put("buttonMask", sample.buttonMask)

                val history = sample.coalescedHistory
                if (!history.isNullOrEmpty()) {
                    put(
                            "coalescedHistory",
                            JSONArray().also { arr ->
                                history.forEach { arr.put(sampleToJson(it)) }
                            }
                    )
                }
            }
}
