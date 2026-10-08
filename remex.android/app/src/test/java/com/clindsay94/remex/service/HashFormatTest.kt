package com.clindsay94.remex.service

import java.security.MessageDigest
import java.util.Base64
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The Kotlin twin's vectors. They are the same strings `FileReadRangeAndHashFormatTests` uses in
 * remex.core.tests, so a hash shown on the phone and one shown on the PC can be compared by eye.
 */
class HashFormatTest {
    private companion object {
        // SHA-256("abc"), the FIPS 180-2 test vector.
        const val ABC_HEX = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        const val ABC_BASE64 = "ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0="
        // SHA-256("").
        const val EMPTY_HEX = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    }

    @Test
    fun theVectorsAreWhatTheyClaimToBe() {
        val abc = MessageDigest.getInstance("SHA-256").digest("abc".toByteArray())
        assertEquals(ABC_HEX, HashFormat.hex(abc))
        assertEquals(ABC_BASE64, Base64.getEncoder().encodeToString(abc))
        assertEquals(EMPTY_HEX, HashFormat.hex(MessageDigest.getInstance("SHA-256").digest(ByteArray(0))))
    }

    @Test
    fun toHex_turnsTheWireBase64IntoLowercaseHex() {
        assertEquals(ABC_HEX, HashFormat.toHex(ABC_BASE64))
    }

    @Test
    fun toHex_ofSomethingThatIsNotASha256_isNull() {
        for (input in listOf(null, "", "not base64 at all", "aGVsbG8=")) {
            assertNull("'$input'", HashFormat.toHex(input))
        }
    }

    @Test
    fun everyFormOfAHash_normalizesToTheSameDigest_andMatchesTheOthers() {
        val forms = listOf(
            ABC_HEX,
            ABC_HEX.uppercase(),
            "  $ABC_HEX\t",
            (0 until 8).joinToString(" ") { ABC_HEX.substring(it * 8, it * 8 + 8) },
            (0 until 32).joinToString(":") { ABC_HEX.substring(it * 2, it * 2 + 2) },
            ABC_BASE64,
        )
        val expected = MessageDigest.getInstance("SHA-256").digest("abc".toByteArray())
        for (form in forms) {
            assertArrayEquals("'$form'", expected, HashFormat.normalize(form))
            assertTrue("'$form'", HashFormat.matches(form, ABC_BASE64))
            assertTrue("'$form'", HashFormat.matches(ABC_HEX, form))
        }
    }

    @Test
    fun somethingThatIsNotASha256_doesNotNormalize_andNeverMatches() {
        val notHashes = listOf(
            "",
            "ba7816bf",
            ABC_HEX + "00",
            "zz7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            "md5:900150983cd24fb0d6963f7d28e17f72",
        )
        for (input in notHashes) {
            assertNull("'$input'", HashFormat.normalize(input))
            assertFalse("'$input'", HashFormat.matches(input, input))
        }
    }

    @Test
    fun twoDifferentHashes_doNotMatch() {
        assertFalse(HashFormat.matches(ABC_HEX, EMPTY_HEX))
    }
}
