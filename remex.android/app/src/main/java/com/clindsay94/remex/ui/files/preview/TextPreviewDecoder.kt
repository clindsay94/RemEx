package com.clindsay94.remex.ui.files.preview

import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets

/**
 * Turns the raw bytes of a text preview into a string (file browser redesign, 2026-10-08). Twin of the PC's
 * `TextPreviewDecoder`, with the same edge cases: a byte-order mark, a read that stopped in the middle of a
 * multi-byte character, and a tail that started in the middle of a line.
 */
object TextPreviewDecoder {
    /** How many leading bytes are checked for NUL when deciding whether a file is binary. */
    const val SNIFF_BYTES = 8 * 1024

    /**
     * True when [bytes] look binary: a NUL in the first [SNIFF_BYTES]. UTF-16 text has NULs too, so a UTF-16
     * byte-order mark is checked first and wins.
     */
    fun looksBinary(bytes: ByteArray, length: Int = bytes.size): Boolean {
        if (startsWithUtf16Bom(bytes, length)) return false
        val head = minOf(length, SNIFF_BYTES)
        for (i in 0 until head) if (bytes[i] == 0.toByte()) return true
        return false
    }

    /**
     * Decodes [bytes]: UTF-8 (with or without a BOM) or UTF-16 by its BOM, invalid sequences shown as U+FFFD
     * rather than refused.
     *
     * @param startsMidFile the bytes are a tail, not the start of the file: the first, probably partial, line is
     *   dropped, along with any continuation bytes of a character that began before the read.
     * @param endsMidFile the file goes on past these bytes: a multi-byte character cut by the end of the read is
     *   dropped instead of becoming U+FFFD (the next read starts with it whole).
     */
    fun decode(bytes: ByteArray, startsMidFile: Boolean = false, endsMidFile: Boolean = false): String {
        var start = 0
        var end = bytes.size
        if (!startsMidFile) {
            if (startsWith(bytes, 0xFF, 0xFE)) return String(bytes, 2, bytes.size - 2, StandardCharsets.UTF_16LE)
            if (startsWith(bytes, 0xFE, 0xFF)) return String(bytes, 2, bytes.size - 2, StandardCharsets.UTF_16BE)
            if (startsWith(bytes, 0xEF, 0xBB, 0xBF)) start = 3
        }
        if (endsMidFile) end = start + completeUtf8Length(bytes, start, end)
        if (startsMidFile) {
            val newline = indexOf(bytes, '\n'.code.toByte(), start, end)
            start = if (newline >= 0) newline + 1 else end
        }
        return utf8(bytes, start, end)
    }

    /**
     * The length of `bytes[from, to)` without a trailing, incomplete UTF-8 sequence. Looks back at most three
     * bytes, which is as far as a sequence can be cut.
     */
    fun completeUtf8Length(bytes: ByteArray, from: Int = 0, to: Int = bytes.size): Int {
        val n = to - from
        var back = 1
        while (back <= 3 && back <= n) {
            val b = bytes[to - back].toInt() and 0xFF
            if (b and 0b1100_0000 == 0b1000_0000) {
                back++
                continue // a continuation byte: keep looking for the lead
            }
            val need = when {
                b and 0b1000_0000 == 0 -> 1
                b and 0b1110_0000 == 0b1100_0000 -> 2
                b and 0b1111_0000 == 0b1110_0000 -> 3
                b and 0b1111_1000 == 0b1111_0000 -> 4
                else -> 1 // not a valid lead: leave it for the decoder to replace
            }
            return if (back < need) n - back else n
        }
        return n
    }

    /**
     * UTF-8 with every invalid sequence replaced by U+FFFD, like .NET's `Encoding.UTF8`. Java's `String(bytes,
     * UTF_8)` already replaces, but says nothing about how; the explicit decoder makes the rule visible.
     */
    internal fun utf8(bytes: ByteArray, from: Int, to: Int): String =
        StandardCharsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPLACE)
            .onUnmappableCharacter(CodingErrorAction.REPLACE)
            .decode(ByteBuffer.wrap(bytes, from, to - from))
            .toString()

    private fun startsWithUtf16Bom(bytes: ByteArray, length: Int): Boolean =
        length >= 2 && (startsWith(bytes, 0xFF, 0xFE) || startsWith(bytes, 0xFE, 0xFF))

    private fun startsWith(bytes: ByteArray, vararg prefix: Int): Boolean =
        bytes.size >= prefix.size && prefix.indices.all { (bytes[it].toInt() and 0xFF) == prefix[it] }

    internal fun indexOf(bytes: ByteArray, value: Byte, from: Int, to: Int): Int {
        for (i in from until to) if (bytes[i] == value) return i
        return -1
    }
}
