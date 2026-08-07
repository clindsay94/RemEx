package com.clindsay94.remex.security

import android.util.Log
import java.net.InetSocketAddress
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Reads the SPKI pin of whatever is answering at an address right now, without trusting it
 * (RemEx-vnps).
 *
 * **WHY THIS EXISTS.** The cert-change dialog has to show the user two fingerprints — the one this
 * phone paired with, and the one being presented now — or "your PC's identity changed" is an
 * assertion the user has no way to check. The pinned side comes straight out of [PinnedHostStore].
 * The presented side is computed inside the native client's validation callback and only ever
 * reaches logcat, so the UI cannot see it; the phone observes it here instead.
 *
 * **THIS IS NOT A CONNECTION AND CANNOT BECOME ONE.** [CapturingTrustManager] records the leaf and
 * then throws unconditionally, so the handshake always fails and no session, no bytes and no
 * credentials ever pass. That is deliberate: a probe that could succeed would be a second TLS path
 * into the app with no pin check on it, which is precisely the hole pinning exists to close. The
 * capture happens before the throw, which is the only reason the value is available at all.
 *
 * What comes back is attacker-controlled when there IS an attacker — that is the point. It is the
 * fingerprint of the machine the user would be trusting if they tapped through, shown so they can
 * refuse.
 */
object HostCertificateProbe {

    private const val Tag = "CertProbe"

    /**
     * Budget for the TCP connect and, separately, for each read during the handshake — so the true
     * worst case is roughly twice this, when a host accepts the connection and then stalls.
     *
     * Short on purpose either way: a user is sitting in front of a modal dialog waiting for it. A
     * host that has not answered by then leaves the presented side unknown, which the dialog says
     * out loud — far better than holding it blank while a dead address times out.
     */
    private const val ProbeTimeoutMillis = 3_000

    /**
     * The pin presented at [host]:[port], in the same spelling [PinnedHostStore] stores, or null
     * when nothing usable answered.
     *
     * Never throws: every failure here is "we could not look", which the caller renders as unknown.
     * A probe that could break the dialog would take away the user's ability to cancel, and cancel
     * is the safe action.
     */
    suspend fun observedPin(host: String, port: Int): String? =
        withContext(Dispatchers.IO) {
            val target = host.trim()
            if (target.isEmpty() || port !in 1..65535) return@withContext null

            val capture = CapturingTrustManager()
            try {
                val ssl = SSLContext.getInstance("TLS")
                ssl.init(null, arrayOf<TrustManager>(capture), null)
                (ssl.socketFactory.createSocket() as SSLSocket).use { socket ->
                    socket.soTimeout = ProbeTimeoutMillis
                    socket.connect(InetSocketAddress(target, port), ProbeTimeoutMillis)
                    socket.startHandshake()
                }
            } catch (e: Exception) {
                // Expected on the happy path: the trust manager rejects every certificate, so a
                // reachable host lands here with the leaf already captured. A genuinely unreachable
                // host lands here too, with nothing captured. Both are handled by the null below.
                Log.d(Tag, "Probe handshake ended: ${e.javaClass.simpleName}")
            }

            val encoded = capture.observed?.publicKey?.encoded
            if (encoded == null || encoded.isEmpty()) null else pinOf(encoded)
        }

    /**
     * Base64 SHA-256 of a DER SubjectPublicKeyInfo — the value a pin IS.
     *
     * **THE SPELLING HAS TO MATCH [PinnedHostStore] EXACTLY**, because the dialog compares the two
     * through [SpkiFingerprint.isSameCertificate]; a different alphabet or stray line break would
     * make one certificate read as two and tell the user their PC changed when it did not.
     *
     * `java.util.Base64` rather than the `android.util.Base64` used elsewhere in this package: it is
     * byte-identical for this input (standard alphabet, padded, unwrapped) and it exists off-device,
     * so the format is pinned by a plain JVM test instead of only by running the app. A test asserts
     * the exact output for a known input, so a future swap back cannot change the encoding quietly.
     */
    internal fun pinOf(subjectPublicKeyInfo: ByteArray): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(subjectPublicKeyInfo)
        return java.util.Base64.getEncoder().encodeToString(digest)
    }

    /**
     * Records the server's leaf certificate and then refuses it, always.
     *
     * The refusal is not a formality — it is what keeps this from being a way to open a trusted
     * connection to an unverified host. There is no branch that returns normally from
     * [checkServerTrusted]; a test asserts that, because a `return` accidentally added here would
     * be invisible at every call site and would silently trust anything.
     */
    internal class CapturingTrustManager : X509TrustManager {

        /** The leaf presented, if the handshake got far enough to send one. */
        @Volatile var observed: X509Certificate? = null
            private set

        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
            // Nothing asks this probe to verify a client.
        }

        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
            observed = chain?.firstOrNull()
            throw CertificateException("Certificate probe never trusts a host; it only looks.")
        }

        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
    }
}
