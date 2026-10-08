package com.clindsay94.remex.service

import java.security.MessageDigest
import java.util.Base64

/**
 * How a SHA-256 is shown to a person and how a pasted one is read back (2026-10-08 redesign).
 *
 * The Kotlin twin of `Remex.Core.Helpers.HashFormat`. **The wire stays Base64; only the screen changes.**
 * People compare hashes against `sha256sum`, `Get-FileHash` and download pages, which print hex, so the screen
 * shows 64 lowercase hex characters. A pasted hash is accepted as hex in either case (separators ignored) or as
 * the 44-character Base64 RemEx uses on the wire; anything else is "not a SHA-256", never a silent mismatch.
 * Both platforms run the same vectors (`HashFormatTest` here, `HashFormatTests` in remex.core.tests).
 */
object HashFormat {
    /** Bytes in a SHA-256 digest. */
    const val SHA256_BYTES = 32

    /** The lowercase hex form of a Base64 SHA-256, or null when [base64] is not one. */
    fun toHex(base64: String?): String? = decodeBase64(base64)?.let(::hex)

    /** The lowercase hex form of raw digest bytes. */
    fun hex(digest: ByteArray): String {
        val out = StringBuilder(digest.size * 2)
        for (b in digest) {
            val v = b.toInt() and 0xFF
            out.append(HEX[v ushr 4]).append(HEX[v and 0x0F])
        }
        return out.toString()
    }

    /**
     * Reads a hash someone typed or pasted: the 32 digest bytes when it is 64 hex digits (any case, with
     * spaces, colons or dashes ignored) or a 44-character Base64 SHA-256; otherwise null.
     */
    fun normalize(input: String?): ByteArray? {
        if (input.isNullOrBlank()) return null
        val trimmed = input.trim()
        val digits = StringBuilder(SHA256_BYTES * 2)
        var allHex = true
        for (c in trimmed) {
            if (c == ' ' || c == ':' || c == '-' || c == '\t') continue
            if (!isHexDigit(c) || digits.length == SHA256_BYTES * 2) {
                allHex = false
                break
            }
            digits.append(c)
        }
        if (allHex && digits.length == SHA256_BYTES * 2) {
            return ByteArray(SHA256_BYTES) { i ->
                ((Character.digit(digits[i * 2], 16) shl 4) or Character.digit(digits[i * 2 + 1], 16)).toByte()
            }
        }
        return decodeBase64(trimmed)
    }

    /**
     * True when two hashes, each in any form [normalize] accepts, name the same digest. False when they differ
     * OR when either is not a SHA-256: call [normalize] first to tell those apart on screen.
     */
    fun matches(a: String?, b: String?): Boolean {
        val left = normalize(a) ?: return false
        val right = normalize(b) ?: return false
        return MessageDigest.isEqual(left, right)
    }

    private fun decodeBase64(base64: String?): ByteArray? {
        if (base64 == null || base64.length != 44) return null
        val bytes = runCatching { Base64.getDecoder().decode(base64) }.getOrNull() ?: return null
        return bytes.takeIf { it.size == SHA256_BYTES }
    }

    private fun isHexDigit(c: Char): Boolean = c in '0'..'9' || c in 'a'..'f' || c in 'A'..'F'

    private val HEX = "0123456789abcdef".toCharArray()
}
