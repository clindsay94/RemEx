package com.clindsay94.remex

import com.clindsay94.remex.ui.screens.PointerDeviceKind
import com.clindsay94.remex.ui.screens.PointerPhase
import com.clindsay94.remex.ui.screens.PointerSampleData
import com.clindsay94.remex.ui.screens.PointerToolKind
import com.clindsay94.remex.ui.screens.RemoteDesktopPointerSerializer
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class RemoteDesktopPointerSerializerTest {

    private fun samplePen(
            phase: PointerPhase = PointerPhase.ContactMove,
            logicalX: Float = 100f,
            logicalY: Float = 200f,
            pressure: Float = 0.8f,
            buttonMask: Int = 0,
            timestamp: Long = 1234L,
            pointerId: Int = 0,
    ) =
            PointerSampleData(
                    timestamp = timestamp,
                    pointerId = pointerId,
                    phase = phase,
                    toolKind = PointerToolKind.Pen,
                    deviceKind = PointerDeviceKind.Stylus,
                    logicalX = logicalX,
                    logicalY = logicalY,
                    pressure = pressure,
                    buttonMask = buttonMask,
            )

    // ── Envelope ──────────────────────────────────────────────────────────────

    @Test
    fun `toBatchJson does not wrap in envelope`() {
        val json = JSONObject(RemoteDesktopPointerSerializer.toBatchJson(listOf(samplePen())))
        assertFalse(json.has("type"))
        assertFalse(json.has("desktopPointerBatch"))
        assertTrue(json.has("samples"))
    }

    @Test
    fun `toBatchJson without streamMappingId omits that field`() {
        val json = JSONObject(RemoteDesktopPointerSerializer.toBatchJson(listOf(samplePen())))
        assertFalse(json.has("streamMappingId"))
    }

    @Test
    fun `toBatchJson with streamMappingId includes that field`() {
        val json =
                JSONObject(
                        RemoteDesktopPointerSerializer.toBatchJson(
                                listOf(samplePen()),
                                streamMappingId = "stream-1"
                        )
                )
        assertEquals("stream-1", json.getString("streamMappingId"))
    }

    // ── Samples array ─────────────────────────────────────────────────────────

    @Test
    fun `toBatchJson with one sample produces samples array of length 1`() {
        val json = JSONObject(RemoteDesktopPointerSerializer.toBatchJson(listOf(samplePen())))
        val samples = json.getJSONArray("samples")
        assertEquals(1, samples.length())
    }

    @Test
    fun `toBatchJson with multiple samples preserves order`() {
        val s1 = samplePen(timestamp = 1000L)
        val s2 = samplePen(timestamp = 2000L)
        val json = JSONObject(RemoteDesktopPointerSerializer.toBatchJson(listOf(s1, s2)))
        val samples = json.getJSONArray("samples")
        assertEquals(2, samples.length())
        assertEquals(1000L, samples.getJSONObject(0).getLong("timestamp"))
        assertEquals(2000L, samples.getJSONObject(1).getLong("timestamp"))
    }

    // ── sampleToJson fields ───────────────────────────────────────────────────

    @Test
    fun `sampleToJson includes protocolVersion 1`() {
        val obj = RemoteDesktopPointerSerializer.sampleToJson(samplePen())
        assertEquals(1, obj.getInt("protocolVersion"))
    }

    @Test
    fun `sampleToJson serializes phase wire value`() {
        val obj =
                RemoteDesktopPointerSerializer.sampleToJson(
                        samplePen(phase = PointerPhase.ContactStart)
                )
        assertEquals("ContactStart", obj.getString("phase"))
    }

    @Test
    fun `sampleToJson serializes toolKind wire value`() {
        val obj = RemoteDesktopPointerSerializer.sampleToJson(samplePen())
        assertEquals("Pen", obj.getString("toolKind"))
    }

    @Test
    fun `sampleToJson serializes deviceKind wire value`() {
        val obj = RemoteDesktopPointerSerializer.sampleToJson(samplePen())
        assertEquals("Stylus", obj.getString("deviceKind"))
    }

    @Test
    fun `sampleToJson serializes logicalX and logicalY`() {
        val obj =
                RemoteDesktopPointerSerializer.sampleToJson(
                        samplePen(logicalX = 960f, logicalY = 540f)
                )
        assertEquals(960.0, obj.getDouble("logicalX"), 0.001)
        assertEquals(540.0, obj.getDouble("logicalY"), 0.001)
    }

    @Test
    fun `sampleToJson serializes pressure`() {
        val obj = RemoteDesktopPointerSerializer.sampleToJson(samplePen(pressure = 0.75f))
        assertEquals(0.75, obj.getDouble("pressure"), 0.001)
    }

    @Test
    fun `sampleToJson serializes buttonMask`() {
        val obj = RemoteDesktopPointerSerializer.sampleToJson(samplePen(buttonMask = 0x02))
        assertEquals(2, obj.getInt("buttonMask"))
    }

    @Test
    fun `sampleToJson serializes timestamp`() {
        val obj = RemoteDesktopPointerSerializer.sampleToJson(samplePen(timestamp = 99999L))
        assertEquals(99999L, obj.getLong("timestamp"))
    }

    @Test
    fun `sampleToJson serializes pointerId`() {
        val obj = RemoteDesktopPointerSerializer.sampleToJson(samplePen(pointerId = 3))
        assertEquals(3, obj.getInt("pointerId"))
    }

    // ── Optional fields ───────────────────────────────────────────────────────

    @Test
    fun `sampleToJson omits hoverDistance when null`() {
        val sample = samplePen().copy(hoverDistance = null)
        val obj = RemoteDesktopPointerSerializer.sampleToJson(sample)
        assertFalse("hoverDistance should be absent when null", obj.has("hoverDistance"))
    }

    @Test
    fun `sampleToJson includes hoverDistance when set`() {
        val sample = samplePen().copy(hoverDistance = 3.5f)
        val obj = RemoteDesktopPointerSerializer.sampleToJson(sample)
        assertEquals(3.5, obj.getDouble("hoverDistance"), 0.001)
    }

    @Test
    fun `sampleToJson omits tiltX when null`() {
        val sample = samplePen().copy(tiltX = null)
        val obj = RemoteDesktopPointerSerializer.sampleToJson(sample)
        assertFalse(obj.has("tiltX"))
    }

    @Test
    fun `sampleToJson includes tiltX when set`() {
        val sample = samplePen().copy(tiltX = 0.3f)
        val obj = RemoteDesktopPointerSerializer.sampleToJson(sample)
        assertEquals(0.3, obj.getDouble("tiltX"), 0.001)
    }

    @Test
    fun `sampleToJson omits orientation when null`() {
        val sample = samplePen().copy(orientation = null)
        val obj = RemoteDesktopPointerSerializer.sampleToJson(sample)
        assertFalse(obj.has("orientation"))
    }

    // ── Coalesced history ─────────────────────────────────────────────────────

    @Test
    fun `sampleToJson omits coalescedHistory when null`() {
        val obj =
                RemoteDesktopPointerSerializer.sampleToJson(
                        samplePen().copy(coalescedHistory = null)
                )
        assertFalse(obj.has("coalescedHistory"))
    }

    @Test
    fun `sampleToJson includes coalescedHistory when present`() {
        val hist = listOf(samplePen(timestamp = 900L), samplePen(timestamp = 950L))
        val obj =
                RemoteDesktopPointerSerializer.sampleToJson(
                        samplePen().copy(coalescedHistory = hist)
                )
        assertTrue(obj.has("coalescedHistory"))
        val arr = obj.getJSONArray("coalescedHistory")
        assertEquals(2, arr.length())
        assertEquals(900L, arr.getJSONObject(0).getLong("timestamp"))
        assertEquals(950L, arr.getJSONObject(1).getLong("timestamp"))
    }

    // ── Eraser variant ────────────────────────────────────────────────────────

    @Test
    fun `eraser sample serializes toolKind Eraser and deviceKind Eraser`() {
        val sample =
                PointerSampleData(
                        timestamp = 1L,
                        pointerId = 0,
                        phase = PointerPhase.ContactMove,
                        toolKind = PointerToolKind.Eraser,
                        deviceKind = PointerDeviceKind.Eraser,
                        logicalX = 10f,
                        logicalY = 20f,
                        pressure = 0.5f,
                )
        val obj = RemoteDesktopPointerSerializer.sampleToJson(sample)
        assertEquals("Eraser", obj.getString("toolKind"))
        assertEquals("Eraser", obj.getString("deviceKind"))
    }

    // ── Hover variant ─────────────────────────────────────────────────────────

    @Test
    fun `hover sample serializes HoverMove phase with zero pressure`() {
        val sample =
                PointerSampleData(
                        timestamp = 5000L,
                        pointerId = 0,
                        phase = PointerPhase.HoverMove,
                        toolKind = PointerToolKind.Pen,
                        deviceKind = PointerDeviceKind.Stylus,
                        logicalX = 400f,
                        logicalY = 300f,
                        pressure = 0f,
                )
        val obj = RemoteDesktopPointerSerializer.sampleToJson(sample)
        assertEquals("HoverMove", obj.getString("phase"))
        assertEquals(0.0, obj.getDouble("pressure"), 0.001)
    }
}
