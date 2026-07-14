package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.PairingViewModel
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Unit tests for [PairingViewModel.parseFetchedPin] (RemEx-1t0b): the pure parser for the native
 * FetchPairingPin result string. Only a well-formed "OK:<6-digit-pin>|<expiry>" yields a PIN;
 * "UNSUPPORTED", "ERROR: ...", blank, wrong-length, and non-numeric all yield null. Runs on the
 * JVM — no Android/Compose/native layer. This is the ASI-compliant replacement for the deleted
 * trust-all HTTPS fetch, so its correctness gates whether the PIN ever auto-fills.
 */
class PairingPinParseTest {

    private val vm = PairingViewModel()

    @Test
    fun validSixDigitPin_isParsed() {
        assertEquals("123456", vm.parseFetchedPin("OK:123456|1770000000000"))
    }

    @Test
    fun validPin_withoutExpirySegment_stillParses() {
        // substringBefore("|") returns the whole remainder when there is no '|'.
        assertEquals("654321", vm.parseFetchedPin("OK:654321"))
    }

    @Test
    fun unsupported_yieldsNull() {
        assertNull(vm.parseFetchedPin("UNSUPPORTED"))
    }

    @Test
    fun errorForms_yieldNull() {
        assertNull(vm.parseFetchedPin("ERROR: PIN fetch timed out"))
        assertNull(vm.parseFetchedPin("ERROR: No active pairing session"))
    }

    @Test
    fun nullOrBlank_yieldsNull() {
        assertNull(vm.parseFetchedPin(null))
        assertNull(vm.parseFetchedPin(""))
    }

    @Test
    fun wrongLengthPin_yieldsNull() {
        assertNull(vm.parseFetchedPin("OK:12345|x"))    // 5 digits
        assertNull(vm.parseFetchedPin("OK:1234567|x"))  // 7 digits
    }

    @Test
    fun nonNumericPin_yieldsNull() {
        assertNull(vm.parseFetchedPin("OK:12ab56|x"))
        assertNull(vm.parseFetchedPin("OK:abcdef|x"))
    }

    @Test
    fun missingOrWrongCasePrefix_yieldsNull() {
        assertNull(vm.parseFetchedPin("123456"))         // no OK: prefix
        assertNull(vm.parseFetchedPin("ok:123456|x"))    // prefix is case-sensitive
    }
}
