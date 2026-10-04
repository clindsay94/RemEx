package com.clindsay94.remex

import androidx.annotation.StringRes

/** What a failed connection attempt comes down to, as far as the person holding the phone cares. */
enum class ConnectionFailureKind {
    /**
     * The PC presented a certificate this phone does not trust. The connection screens swap in their
     * own "this PC's security identity has changed" message and the repair action for this one.
     */
    CertificateProblem,

    /** Nothing answered in time. */
    TimedOut,

    /** The address could not be reached at all: refused, no route, unknown name. */
    Unreachable,

    /** Anything else. */
    Unknown,
}

/**
 * Turns the native client's failure reason into something a person can read (3.0 comb, raw-errors).
 *
 * The reason [RemexClientManager.onConnectionError] receives is a .NET exception message
 * (`RemexNativeClient` raises `ConnectionFailed(ex.Message)`): English in every locale and written for
 * developers ("No such host is known", "The SSL connection could not be established"). It used to go
 * straight onto the error card. Now it is logged, classified here, and replaced by a localized message.
 *
 * Pure, so the classification is tested on the JVM.
 */
object ConnectionFailures {
    fun classify(raw: String): ConnectionFailureKind {
        val text = raw.lowercase()
        return when {
            // The same three words the Connection screen has always used to spot a pin mismatch.
            CERTIFICATE_WORDS.any { it in text } -> ConnectionFailureKind.CertificateProblem
            TIMEOUT_WORDS.any { it in text } -> ConnectionFailureKind.TimedOut
            UNREACHABLE_WORDS.any { it in text } -> ConnectionFailureKind.Unreachable
            else -> ConnectionFailureKind.Unknown
        }
    }

    /** Whether [text] is about a certificate the phone does not trust. */
    fun isCertificateProblem(text: String): Boolean =
        classify(text) == ConnectionFailureKind.CertificateProblem

    /**
     * The localized message for [kind], or null for a certificate problem.
     *
     * NULL FOR THE CERTIFICATE CASE ON PURPOSE: the screens detect it from the text and show their own
     * message with a repair button, so the text has to survive until it reaches them.
     */
    @StringRes
    fun messageRes(kind: ConnectionFailureKind): Int? =
        when (kind) {
            ConnectionFailureKind.CertificateProblem -> null
            ConnectionFailureKind.TimedOut -> R.string.pairing_error_timeout
            ConnectionFailureKind.Unreachable -> R.string.pairing_error_reach_failed
            ConnectionFailureKind.Unknown -> R.string.connection_error_generic
        }

    private val CERTIFICATE_WORDS = listOf("spki", "certificate", "ssl")
    private val TIMEOUT_WORDS = listOf("timed out", "timeout", "time out")
    private val UNREACHABLE_WORDS =
        listOf(
            "refused",
            "unreachable",
            "no route",
            "no such host",
            "name or service not known",
            "nodename nor servname",
            "host is down",
            "connection reset",
            "network is down",
        )
}
