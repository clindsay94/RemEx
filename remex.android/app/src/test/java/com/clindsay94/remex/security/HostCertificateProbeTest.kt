package com.clindsay94.remex.security

import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.fail
import org.junit.Test
import org.mockito.kotlin.mock

/**
 * Pins the two properties the certificate probe is trusted for (RemEx-vnps): that it never trusts
 * anything, and that the pin it reports is spelled the same way the pin store spells one.
 */
class HostCertificateProbeTest {

    // Base64 SHA-256 of the empty input and of "abc" - published vectors, so these assert the
    // ENCODING rather than restating the implementation. A swap to a URL-safe alphabet, to an
    // unpadded encoder, or to a line-wrapping one changes every character here.
    private val emptyDigest = "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU="
    private val abcDigest = "ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0="

    @Test
    fun `a pin is base64 SHA-256 in exactly the spelling the pin store uses`() {
        // THE POINT: the dialog runs the probe's answer and the stored pin through
        // SpkiFingerprint.isSameCertificate. A different alphabet or a stray newline makes one
        // certificate read as two, and the user is told their PC changed when nothing did.
        assertEquals(emptyDigest, HostCertificateProbe.pinOf(ByteArray(0)))
        assertEquals(abcDigest, HostCertificateProbe.pinOf("abc".toByteArray(Charsets.US_ASCII)))
    }

    @Test
    fun `a probed pin compares equal to the same pin from the store`() {
        // The two halves of the dialog arrive from different code paths. If they did not compare
        // equal for one certificate, the Unchanged state would be unreachable and every SSL error
        // would offer to discard the user's pin.
        val probed = HostCertificateProbe.pinOf("abc".toByteArray(Charsets.US_ASCII))
        assert(SpkiFingerprint.isSameCertificate(probed, abcDigest))
        assert(SpkiFingerprint.isSameCertificate(probed, "sha256/$abcDigest"))
    }

    @Test
    fun `different keys never share a pin`() {
        val one = HostCertificateProbe.pinOf(byteArrayOf(1, 2, 3))
        val other = HostCertificateProbe.pinOf(byteArrayOf(1, 2, 4))
        assert(one != other)
        assert(!SpkiFingerprint.isSameCertificate(one, other))
    }

    @Test
    fun `the probe's trust manager refuses a server certificate it was just handed`() {
        // THE TEST THIS CLASS EXISTS FOR. The probe opens TLS to an address whose certificate has
        // already failed the pin check. If checkServerTrusted could return normally, that would be
        // a second TLS path into the app with no pin enforcement on it - exactly the hole pinning
        // closes. There must be no input for which this returns.
        val trustManager = HostCertificateProbe.CapturingTrustManager()
        val leaf = mock<X509Certificate>()

        try {
            trustManager.checkServerTrusted(arrayOf(leaf), "ECDHE_ECDSA")
            fail("the probe trusted a certificate; it must never trust one")
        } catch (expected: CertificateException) {
            // Correct: looked, then refused.
        }
    }

    @Test
    fun `it captures the leaf before refusing it, which is the only reason it is useful`() {
        val trustManager = HostCertificateProbe.CapturingTrustManager()
        val leaf = mock<X509Certificate>()
        val intermediate = mock<X509Certificate>()

        try {
            trustManager.checkServerTrusted(arrayOf(leaf, intermediate), "ECDHE_RSA")
        } catch (expected: CertificateException) {
            // The refusal is the happy path here.
        }

        // The LEAF, not the issuer: the pin is over the host's own public key.
        assertSame(leaf, trustManager.observed)
    }

    @Test
    fun `an empty or absent chain refuses too, and captures nothing`() {
        // A host that completes no chain must leave the presented side unknown rather than
        // half-set. "Unknown" and "unchanged" lead the user to opposite decisions.
        for (chain in listOf<Array<X509Certificate>?>(null, emptyArray())) {
            val trustManager = HostCertificateProbe.CapturingTrustManager()
            try {
                trustManager.checkServerTrusted(chain, "ECDHE_RSA")
                fail("no chain must still be a refusal")
            } catch (expected: CertificateException) {
                // Expected.
            }
            assertNull(trustManager.observed)
        }
    }

    @Test
    fun `it offers no issuers and verifies no clients`() {
        // Nothing should be able to press this trust manager into ordinary service.
        val trustManager = HostCertificateProbe.CapturingTrustManager()
        assertArrayEquals(emptyArray<X509Certificate>(), trustManager.acceptedIssuers)
        trustManager.checkClientTrusted(arrayOf(mock<X509Certificate>()), "RSA")
    }
}
