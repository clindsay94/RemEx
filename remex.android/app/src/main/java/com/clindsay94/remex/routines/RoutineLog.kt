package com.clindsay94.remex.routines

import android.util.Log

/**
 * The ONE way routine code writes to logcat (routines spec §9 T11, RemEx-pp0rt.5).
 *
 * Routines carry things that must never reach a log: NFC tokens, the shortcut key, home-network
 * facts, notification bodies the user typed, and hardware addresses. A log line is effectively
 * public (bug reports, `adb logcat`, the diagnostics bundle), so the redaction happens HERE rather
 * than being remembered at each call site:
 *
 * - [mac] keeps the vendor half only (`0A:1B:2C:**:**:**`).
 * - [name] truncates a routine name to 16 characters.
 * - [id] shortens a routine or run id to its first 8 characters: enough to correlate lines, not a
 *   stable identifier worth harvesting.
 * - [host] shortens a host identity to 4 hex characters.
 * - A throwable is logged with its STACK but never its MESSAGE ([RedactedThrowable]): org.json and
 *   Tink put the offending input into exception messages, and that input is exactly the routine
 *   text this wrapper exists to keep out.
 *
 * `RoutineLogRedactionTest` pins each rule and scans the routines package for any direct
 * `android.util.Log` use outside this file.
 */
object RoutineLog {
    const val TAG = "RemexRoutines"

    /** Where lines go. A seam for the JVM tests, which have no logcat. */
    fun interface Sink {
        fun write(priority: Int, message: String, error: Throwable?)
    }

    private val logcat =
        Sink { priority, message, error ->
            when (priority) {
                Log.ERROR -> Log.e(TAG, message, error)
                Log.WARN -> Log.w(TAG, message, error)
                Log.INFO -> Log.i(TAG, message, error)
                else -> Log.d(TAG, message, error)
            }
        }

    @Volatile internal var sink: Sink = logcat

    internal fun resetSinkForTests() {
        sink = logcat
    }

    fun d(message: String) = sink.write(Log.DEBUG, message, null)

    fun i(message: String) = sink.write(Log.INFO, message, null)

    fun w(message: String, error: Throwable? = null) = sink.write(Log.WARN, withType(message, error), error?.let(::RedactedThrowable))

    fun e(message: String, error: Throwable? = null) = sink.write(Log.ERROR, withType(message, error), error?.let(::RedactedThrowable))

    /** `0A:1B:2C:**:**:**`; anything that is not a 6-octet MAC collapses to a fixed mask. */
    fun mac(mac: String?): String {
        val octets = mac?.split(':', '-').orEmpty()
        if (octets.size != 6) return "**:**:**:**:**:**"
        return octets.take(3).joinToString(":") { it.uppercase() } + ":**:**:**"
    }

    /** A routine name cut to 16 characters, quoted so an empty name is visible as such. */
    fun name(name: String?): String {
        val value = name.orEmpty()
        return "\"" + (if (value.length > NAME_CHARS) value.take(NAME_CHARS) + "…" else value) + "\""
    }

    fun id(id: String?): String = id?.take(ID_CHARS) ?: "-"

    fun host(hostIdentity: String?): String = hostIdentity?.take(HOST_CHARS)?.let { "$it…" } ?: "-"

    private fun withType(message: String, error: Throwable?): String =
        if (error == null) message else "$message (${error.javaClass.name})"

    private const val NAME_CHARS = 16
    private const val ID_CHARS = 8
    private const val HOST_CHARS = 4
}

/**
 * A copy of [original]'s stack trace and type with the message removed, recursively through the
 * cause chain. What logcat prints for it is the type and the frames, which is what a bug report
 * needs, and nothing from the input that failed to parse or decrypt.
 */
internal class RedactedThrowable(original: Throwable) :
    Throwable(original.javaClass.name, original.cause?.let(::RedactedThrowable)) {
    init {
        stackTrace = original.stackTrace
    }

    override fun toString(): String = message.orEmpty()
}
