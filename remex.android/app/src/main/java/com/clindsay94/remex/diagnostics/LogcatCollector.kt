package com.clindsay94.remex.diagnostics

import android.os.Process
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.io.BufferedReader

/**
 * Collects the one input [DiagnosticsBundle] cannot gather for itself: this process's recent log
 * lines (RemEx-0iww).
 *
 * BOUNDED AND NON-HANGING IS THE WHOLE DESIGN. An app that blocks collecting its own logs in order
 * to report a hang is its own bug report, so every axis has a ceiling: `logcat -d` dumps and exits
 * rather than following, `--pid` narrows the dump to this process, [MaxLines] and [MaxCharacters]
 * cap what is kept, and [TimeoutMillis] caps the wait. If any of those trips, collection returns a
 * SHORTER bundle with a note saying so — never no bundle and never a stuck screen, because a
 * diagnostics feature that fails silently is worse than one that says "the log step timed out",
 * which is itself a diagnostic.
 *
 * TRUNCATION CLIPS WHOLE LINES, and that is a security property rather than a tidiness one. See
 * [clipToWholeLines].
 *
 * Only the impure [collect] touches the process; the trimming rules live in [clipToWholeLines] so
 * they can be tested exhaustively off-device, which matters because the failure they prevent is
 * invisible in the output.
 */
object LogcatCollector {

    /**
     * How many of the most recent lines to ask logcat for.
     *
     * Five hundred is roughly one connection attempt plus its failure, which is the span a bug
     * report needs. It is passed to `-t` so the kernel-side ring buffer does the cutting; the
     * client-side cap in [clipToWholeLines] is the belt to that braces, since `-t` counts lines and
     * says nothing about how long they are.
     */
    const val MaxLines: Int = 500

    /**
     * A ceiling on the collected text, in characters.
     *
     * 500 lines of ordinary logcat is well under this. The cap exists for the run that is not
     * ordinary — a stack trace storm, or a line carrying a serialized payload — because that run is
     * exactly when someone reaches for this feature, and a bundle no mail client will attach is a
     * bundle nobody reads.
     */
    const val MaxCharacters: Int = 128 * 1024

    /** How long to wait for the dump before giving up on it and reporting the rest. */
    const val TimeoutMillis: Long = 4_000

    /** Written into the bundle in place of the lines that were dropped to satisfy the caps. */
    internal const val ElidedMarker: String = "... (older log lines dropped to stay within the size limit)"

    internal const val TimedOut: String =
            "(collecting the log timed out after ${TimeoutMillis}ms - the rest of this bundle is still accurate)"

    internal const val Unavailable: String = "(the log could not be read on this device)"

    /**
     * Runs `logcat -d --pid <this process>` and returns the trimmed result, ready to hand to
     * [DiagnosticsBundle.Environment.logcat].
     *
     * Never throws and never blocks indefinitely: an unreadable log, a device that refuses to exec,
     * and a dump that overruns [TimeoutMillis] all come back as a short parenthetical instead.
     *
     * `--pid` needs API 24 and this app's minSdk is 34, so no version guard is required. It is not
     * merely a filter for readability: an unprivileged app is already limited to its own entries,
     * and asking for exactly that keeps the two agreeing rather than relying on the platform's
     * default staying where it is.
     */
    suspend fun collect(
            pid: Int = Process.myPid(),
            maxLines: Int = MaxLines,
            maxCharacters: Int = MaxCharacters,
            timeoutMillis: Long = TimeoutMillis,
    ): String = withContext(Dispatchers.IO) {
        val process =
                try {
                    ProcessBuilder("logcat", "-d", "-t", maxLines.toString(), "--pid", pid.toString())
                            // Merged so that logcat's own complaints land in the bundle rather than
                            // in a stream nobody drains - an undrained stderr pipe is one of the
                            // ways this step could block.
                            .redirectErrorStream(true)
                            .start()
                } catch (e: Exception) {
                    return@withContext Unavailable
                }

        // The read runs as a child so the timeout below can abandon it. It cannot THROW into the
        // parent scope - a failed async that nobody awaits still cancels its parent - so a read
        // failure comes back as the Unavailable note instead. The partial window is discarded with
        // it, deliberately: a stream that died mid-dump has no guarantee its last line is whole,
        // and a half line is exactly what clipToWholeLines exists to keep out of the bundle.
        val reader =
                async(Dispatchers.IO) {
                    try {
                        process.inputStream.bufferedReader().use {
                            readCapped(it, maxLines, maxCharacters)
                        }
                    } catch (e: CancellationException) {
                        throw e
                    } catch (e: Exception) {
                        Unavailable
                    }
                }

        try {
            withTimeoutOrNull(timeoutMillis) { reader.await() } ?: TimedOut
        } finally {
            // ORDER MATTERS. Coroutine cancellation cannot interrupt a thread parked in a blocking
            // read() on a pipe; killing the process closes that pipe and is what lets the reader
            // finish, so destroy comes first and cancel second. Reversed, this function would wait
            // for the very read it just gave up on - the hang the timeout exists to prevent, moved
            // one frame outwards, since withContext does not return until its children do.
            process.destroy()
            // logcat is never written to, so its stdin pipe is only ever a descriptor to give back.
            runCatching { process.outputStream.close() }
            reader.cancel()
        }
    }

    /**
     * Drains the dump into a bounded sliding window over the NEWEST lines.
     *
     * Reading a prefix and stopping at the cap would be simpler and would keep the wrong half:
     * `logcat -d` prints oldest first, and the fault being reported is normally the last thing that
     * happened. Draining also lets the process exit on its own — a pipe nobody empties fills at
     * 64KB and parks logcat there forever, which the timeout would then have to paper over.
     */
    private fun readCapped(reader: BufferedReader, maxLines: Int, maxCharacters: Int): String {
        val window = ArrayDeque<String>()
        var characters = 0
        // Memory bound only, deliberately looser than the caps: the exact trimming is
        // clipToWholeLines' job and it needs whole lines to do it on.
        val readCeiling = maxCharacters.toLong() * 2
        while (true) {
            val line = reader.readLine() ?: break
            window.addLast(line)
            characters += line.length + 1
            while (window.size > maxLines || (window.size > 1 && characters > readCeiling)) {
                characters -= window.removeFirst().length + 1
            }
        }
        return clipToWholeLines(window.joinToString("\n"), maxLines, maxCharacters)
    }

    /**
     * Trims [raw] to the newest lines that fit inside both caps, CUTTING ONLY AT LINE BOUNDARIES.
     *
     * THIS IS A SECURITY RULE, not a formatting preference, and it is the one way this collection
     * step can silently defeat the scrubber it feeds. [DiagnosticsScrubber] redacts base64 runs of
     * 40 characters or more; the PAIR-1 reconnect secret is 44. A cut by character count that lands
     * inside such a run leaves two shorter runs, neither of which matches the rule, and the secret
     * then travels in plain text through a bundle whose entire promise is that it cannot. Nothing
     * downstream can detect that, because what arrives looks exactly like ordinary log text.
     *
     * A single line longer than [maxCharacters] is therefore DROPPED WHOLE rather than shortened.
     * Losing one line is the cheap failure; the alternative is the expensive one.
     *
     * Trimming keeps the NEWEST lines, since the fault being reported is normally the last thing
     * that happened, and replaces what it dropped with [ElidedMarker] so the reader can tell a
     * trimmed log from a short one.
     *
     * ONE EXCEPTION TO THE CHARACTER CAP, stated because a test asserts it: when every line has been
     * dropped, the marker is returned on its own even if the marker is itself longer than
     * [maxCharacters]. Unreachable in production — the marker is under 60 characters and the cap is
     * 128KB — and the alternative, returning an empty string, would silently turn "everything was
     * dropped" into "there was nothing to report", which is the one thing a diagnostics tool must
     * never do. The cap loses to honesty in the only case where they conflict.
     */
    internal fun clipToWholeLines(raw: String, maxLines: Int, maxCharacters: Int): String {
        if (raw.isEmpty()) return ""

        val all = raw.split('\n')
        val kept = ArrayDeque<String>()
        var dropped = false

        for (index in all.indices.reversed()) {
            val line = all[index]
            // +1 for the newline this line needs once anything precedes it.
            val cost = line.length + if (kept.isEmpty()) 0 else 1
            if (kept.size >= maxLines || renderedLength(kept) + cost > maxCharacters) {
                dropped = true
                break
            }
            kept.addFirst(line)
        }

        if (!dropped) return raw

        // The marker REPLACES dropped lines rather than joining the kept ones, so its presence
        // cannot push the result back over a cap. Displacing exactly one line is not enough: a
        // marker longer than the line it displaces has to displace another, and the loop is what
        // makes that true for every input rather than for the ones that happen to be short.
        kept.addFirst(ElidedMarker)
        while (kept.size > 1 && (kept.size > maxLines || renderedLength(kept) > maxCharacters)) {
            kept.removeAt(1) // the oldest REAL line; the marker stays at the front.
        }
        return kept.joinToString("\n")
    }

    /** Length of `joinToString("\n")` without building the string. */
    private fun renderedLength(lines: Collection<String>): Int =
            if (lines.isEmpty()) 0 else lines.sumOf { it.length } + lines.size - 1
}
