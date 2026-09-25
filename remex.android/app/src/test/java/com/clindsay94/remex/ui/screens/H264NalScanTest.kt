package com.clindsay94.remex.ui.screens

import kotlin.random.Random
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Perf audit P3-16: the decoder used to scan every P-frame twice (once for an SPS, once for an IDR
 * slice) and copy every SPS-carrying IDR whole just to index its NALs. The per-frame question is now
 * one [scanNalTypes] pass and the NAL index works in place over the (bytes, offset, length) range.
 *
 * The decoder's deferred-configure and mid-stream-reconfigure guards (docs/REGRESSION-GUARDS.md,
 * "Android — H.264 decoder") depend on these answers being EXACTLY what the old code produced, so
 * the tests below pin that equivalence against a verbatim copy of the old scan, plus the one new
 * hazard the in-place index introduces: reading bytes ahead of the range (the frame envelope header).
 */
class H264NalScanTest {

    private val nalSps = 7
    private val nalPps = 8
    private val nalIdr = 5
    private val nalSlice = 1
    private val nalAud = 9
    private val spsAndIdr = (1 shl nalSps) or (1 shl nalIdr)

    /** The pre-P3-16 H264StreamDecoder.containsNalType, verbatim, as the equivalence oracle. */
    private fun oldContainsNalType(b: ByteArray, offset: Int, length: Int, type: Int): Boolean {
        val n = offset + length
        var i = offset
        while (i + 3 < n) {
            if ((b[i].toInt() and 0xFF) == 0 &&
                (b[i + 1].toInt() and 0xFF) == 0 &&
                (b[i + 2].toInt() and 0xFF) == 1
            ) {
                if ((b[i + 3].toInt() and 0x1F) == type) return true
                i += 3
            } else {
                i++
            }
        }
        return false
    }

    private fun nal(type: Int, payload: Int, fourByte: Boolean = true): ByteArray {
        val sc = if (fourByte) byteArrayOf(0, 0, 0, 1) else byteArrayOf(0, 0, 1)
        // 0x60 = nal_ref_idc 3; payload bytes avoid 0x00 so they never form a start code.
        return sc + byteArrayOf((0x60 or type).toByte()) + ByteArray(payload) { (0x11 + it % 0x70).toByte() }
    }

    /** A 28-byte stand-in for the RDXF envelope header, deliberately containing an SPS-looking code. */
    private val envelopeHeader = byteArrayOf(0x52, 0x44, 0x58, 0x46, 0, 0, 1, 0x67) + ByteArray(19) { 0x33 } + byteArrayOf(0)

    @Test
    fun `a P-frame reports neither SPS nor IDR`() {
        val au = nal(nalAud, 1) + nal(nalSlice, 400, fourByte = false)
        val mask = scanNalTypes(au, 0, au.size, spsAndIdr)
        assertFalse(mask.hasNalType(nalSps))
        assertFalse(mask.hasNalType(nalIdr))
        assertTrue(mask.hasNalType(nalSlice))
    }

    @Test
    fun `an IDR reports SPS and IDR, and the scan stops once both are seen`() {
        val au = nal(nalSps, 12) + nal(nalPps, 4) + nal(nalIdr, 600)
        val mask = scanNalTypes(au, 0, au.size, spsAndIdr)
        assertTrue(mask.hasNalType(nalSps))
        assertTrue(mask.hasNalType(nalIdr))
    }

    @Test
    fun `the scan never reads ahead of the range offset`() {
        // The header contains 00 00 01 67 (an SPS header byte). Scanning the AU's range must not see it.
        val au = nal(nalAud, 1) + nal(nalSlice, 200)
        val frame = envelopeHeader + au
        val mask = scanNalTypes(frame, envelopeHeader.size, au.size, spsAndIdr)
        assertFalse("SPS inside the envelope header leaked into the AU scan", mask.hasNalType(nalSps))
    }

    @Test
    fun `scanNalTypes agrees with the old per-type scan on random ranges`() {
        val rnd = Random(0x3B4)
        repeat(2000) {
            // Bytes drawn mostly from {0, 1, header-ish} so start codes are dense.
            val size = rnd.nextInt(0, 96)
            val b = ByteArray(size) {
                val v = when (rnd.nextInt(6)) {
                    0, 1 -> 0
                    2 -> 1
                    else -> rnd.nextInt(256)
                }
                v.toByte()
            }
            val offset = if (size == 0) 0 else rnd.nextInt(0, size)
            val length = rnd.nextInt(0, size - offset + 1)
            for (stop in intArrayOf(spsAndIdr, -1)) {
                val mask = scanNalTypes(b, offset, length, stop)
                // With stop = spsAndIdr (what the decoder passes) the SPS/IDR answers must match; with
                // an unreachable stop mask (-1, never early-exits) every type's answer must match.
                val types = if (stop == -1) (0..31).toList() else listOf(nalSps, nalIdr)
                for (t in types) {
                    assertEquals(
                        "type $t, offset $offset, length $length, stop $stop",
                        oldContainsNalType(b, offset, length, t),
                        mask.hasNalType(t),
                    )
                }
            }
        }
    }

    @Test
    fun `findNalUnits over a range returns absolute indices and the same csd bytes`() {
        val sps = nal(nalSps, 12)
        val pps = nal(nalPps, 4, fourByte = false)
        val idr = nal(nalIdr, 300)
        val au = sps + pps + idr
        val frame = envelopeHeader + au
        val base = envelopeHeader.size

        val nals = findNalUnits(frame, base, au.size)
        assertEquals(listOf(nalSps, nalPps, nalIdr), nals.map { it.type })

        val spsUnit = nals.first { it.type == nalSps }
        val ppsUnit = nals.first { it.type == nalPps }
        // The envelope header ends in 0x00, right before the SPS's own 4-byte start code. The unit must
        // start AT the range offset, never one byte earlier inside the header.
        assertEquals(base, spsUnit.start)
        assertArrayEquals(sps, frame.copyOfRange(spsUnit.start, spsUnit.end))
        assertArrayEquals(pps, frame.copyOfRange(ppsUnit.start, ppsUnit.end))
        assertEquals(frame.size, nals.last().end)

        // Same answer as the old path: materialize (copy the range out) then index from 0.
        val materialized = frame.copyOfRange(base, base + au.size)
        val old = findNalUnits(materialized, 0, materialized.size)
        assertEquals(old.map { it.type }, nals.map { it.type })
        assertEquals(old.map { it.start + base }, nals.map { it.start })
        assertEquals(old.map { it.end + base }, nals.map { it.end })
    }

    @Test
    fun `a 3-byte start code at the range offset is not widened into the preceding byte`() {
        val au = nal(nalSps, 8, fourByte = false) + nal(nalPps, 4, fourByte = false)
        val frame = byteArrayOf(0x7F, 0) + au // the byte before the range is 0x00
        val nals = findNalUnits(frame, 2, au.size)
        assertEquals(2, nals.first().start)
    }
}
