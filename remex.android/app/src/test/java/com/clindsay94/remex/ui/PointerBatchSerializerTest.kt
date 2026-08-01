package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.PointerDeviceKind
import com.clindsay94.remex.ui.screens.PointerPhase
import com.clindsay94.remex.ui.screens.PointerSampleData
import com.clindsay94.remex.ui.screens.PointerToolKind
import com.clindsay94.remex.ui.screens.RemoteDesktopPointerSerializer
import java.util.Locale
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins that the hand-written batch serializer emits the same wire content the `JSONObject` version
 * did (RemEx-ugvo).
 *
 * Building the batch through `JSONObject` allocated one object per sample plus ~15 boxed puts, on an
 * input path that is unthrottled by design for fidelity — thousands of small allocations per second
 * during inking, producing GC pressure exactly when latency matters. A `StringBuilder` is the
 * obvious replacement and the obvious way to break the wire format, so every test here parses the
 * output back and compares it to a reference tree rather than to a literal string: key ORDER is not
 * part of the contract and pinning it would make this brittle for no gain.
 */
class PointerBatchSerializerTest {

    private fun sample(
            pointerId: Int = 1,
            logicalX: Float = 10f,
            hoverDistance: Float? = null,
            tiltX: Float? = null,
            history: List<PointerSampleData>? = null,
    ) = PointerSampleData(
            timestamp = 1234567890L,
            pointerId = pointerId,
            phase = PointerPhase.ContactMove,
            toolKind = PointerToolKind.Pen,
            deviceKind = PointerDeviceKind.Stylus,
            logicalX = logicalX,
            logicalY = 20.5f,
            dx = 0.25f,
            dy = -0.75f,
            pressure = 0.5f,
            hoverDistance = hoverDistance,
            tiltX = tiltX,
            buttonMask = 3,
            coalescedHistory = history,
    )

    /** The shape the old implementation produced, rebuilt here as the reference. */
    private fun reference(sample: PointerSampleData): JSONObject =
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
                sample.coalescedHistory?.takeIf { it.isNotEmpty() }?.let { history ->
                    put("coalescedHistory", JSONArray().also { arr -> history.forEach { arr.put(reference(it)) } })
                }
            }

    private fun assertSameShape(expected: JSONObject, actual: JSONObject) {
        assertEquals(
                "key sets differ — a field was dropped or invented",
                expected.keys().asSequence().toSet(),
                actual.keys().asSequence().toSet())

        for (key in expected.keys()) {
            val e = expected.get(key)
            val a = actual.get(key)
            when (e) {
                is JSONObject -> assertSameShape(e, a as JSONObject)
                is JSONArray -> {
                    a as JSONArray
                    assertEquals("$key length", e.length(), a.length())
                    for (i in 0 until e.length()) {
                        assertSameShape(e.getJSONObject(i), a.getJSONObject(i))
                    }
                }
                is Number -> assertEquals(key, e.toDouble(), (a as Number).toDouble(), 1e-9)
                else -> assertEquals(key, e, a)
            }
        }
    }

    @Test
    fun `a batch matches what the JSONObject implementation produced`() {
        val samples = listOf(
                sample(pointerId = 1),
                sample(pointerId = 2, hoverDistance = 4.5f, tiltX = -12.25f),
                sample(pointerId = 3, history = listOf(sample(pointerId = 3, logicalX = 9f))),
        )

        val parsed = JSONObject(RemoteDesktopPointerSerializer.toBatchJson(samples, "stream-7"))

        assertEquals("stream-7", parsed.getString("streamMappingId"))
        val arr = parsed.getJSONArray("samples")
        assertEquals(3, arr.length())
        samples.forEachIndexed { i, s -> assertSameShape(reference(s), arr.getJSONObject(i)) }
    }

    @Test
    fun `an absent stream mapping id is omitted rather than sent as null`() {
        val parsed = JSONObject(RemoteDesktopPointerSerializer.toBatchJson(listOf(sample())))
        assertTrue("the key must be absent, not null", !parsed.has("streamMappingId"))
    }

    @Test
    fun `optional axes are omitted when absent, exactly as before`() {
        val parsed = JSONObject(RemoteDesktopPointerSerializer.toBatchJson(listOf(sample())))
                .getJSONArray("samples").getJSONObject(0)

        assertTrue(!parsed.has("hoverDistance"))
        assertTrue(!parsed.has("tiltX"))
        assertTrue(!parsed.has("tiltY"))
        assertTrue(!parsed.has("orientation"))
    }

    @Test
    fun `a comma-decimal locale does not corrupt the numbers`() {
        // THE CLASSIC WAY A HAND-ROLLED JSON WRITER BREAKS, and only for some users: anything built
        // on String.format emits "1,5" under a locale like German, and the host's parser rejects the
        // batch. Float.toString is locale-independent; this pins that nothing regresses to a
        // locale-sensitive path.
        val previous = Locale.getDefault()
        try {
            Locale.setDefault(Locale.GERMANY)
            val parsed = JSONObject(RemoteDesktopPointerSerializer.toBatchJson(listOf(sample(logicalX = 1.5f))))
                    .getJSONArray("samples").getJSONObject(0)

            assertEquals(1.5, parsed.getDouble("logicalX"), 1e-9)
        } finally {
            Locale.setDefault(previous)
        }
    }

    @Test
    fun `a non-finite axis does not destroy the whole batch`() {
        // JSONObject.put THREW on NaN, so one bad axis failed the entire stroke. A driver reporting
        // no tilt or pressure can produce it. Zero for that axis, batch intact.
        val parsed = JSONObject(
                RemoteDesktopPointerSerializer.toBatchJson(
                        listOf(sample(logicalX = Float.NaN, tiltX = Float.POSITIVE_INFINITY))))
                .getJSONArray("samples").getJSONObject(0)

        assertEquals(0.0, parsed.getDouble("logicalX"), 1e-9)
        assertEquals(0.0, parsed.getDouble("tiltX"), 1e-9)
    }

    @Test
    fun `a stream mapping id with quotes or backslashes stays parseable`() {
        val nasty = """a"b\c"""
        val parsed = JSONObject(RemoteDesktopPointerSerializer.toBatchJson(listOf(sample()), nasty))
        assertEquals(nasty, parsed.getString("streamMappingId"))
    }

    @Test
    fun `control characters in a stream mapping id are escaped, not emitted raw`() {
        // A raw control character inside a JSON string is invalid per RFC 8259, so emitting one
        // would make the host reject the whole batch. Built with toChar() rather than a literal so
        // the bytes stay visible in a diff — a raw 0x01 pasted into source is unreviewable.
        //
        // 0x01 has no short escape and so is what exercises the four-hex-digit fallback; the tab
        // beside it covers the short-escape branch. Under GERMANY because that fallback must be
        // locale-independent too — the reason it is toString(16) and not String.format("%04x").
        val previous = Locale.getDefault()
        try {
            Locale.setDefault(Locale.GERMANY)
            val nasty = "a" + 1.toChar() + "b" + 9.toChar() + "c"
            val parsed =
                    JSONObject(RemoteDesktopPointerSerializer.toBatchJson(listOf(sample()), nasty))
            assertEquals(nasty, parsed.getString("streamMappingId"))
        } finally {
            Locale.setDefault(previous)
        }
    }
}
