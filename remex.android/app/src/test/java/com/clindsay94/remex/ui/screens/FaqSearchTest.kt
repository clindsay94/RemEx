package com.clindsay94.remex.ui.screens

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class FaqSearchTest {

    private val question = "How do I pair my phone?"
    private val answer = "Open Connection and scan the QR code shown on your PC."

    @Test
    fun `a blank query matches every entry`() {
        assertTrue(faqMatches(question, answer, ""))
        assertTrue(faqMatches(question, answer, "   "))
    }

    @Test
    fun `matching ignores case and surrounding whitespace`() {
        assertTrue(faqMatches(question, answer, "  PAIR "))
        assertTrue(faqMatches(question, answer, "how do i"))
    }

    @Test
    fun `a word that only appears in the answer still matches`() {
        assertTrue(faqMatches(question, answer, "qr code"))
    }

    @Test
    fun `a word in neither the question nor the answer does not match`() {
        assertFalse(faqMatches(question, answer, "firewall"))
    }
}
