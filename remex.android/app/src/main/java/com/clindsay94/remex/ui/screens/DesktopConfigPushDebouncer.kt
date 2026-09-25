package com.clindsay94.remex.ui.screens

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Coalesces remote-desktop stream-setting changes into as few `desktop_config` sends as possible
 * (perf P1-2, ac-22).
 *
 * **EVERY `desktop_config` COSTS A FULL ENCODER REBUILD ON THE HOST.** The quality/fps/scale sliders
 * call into the ViewModel on every drag tick, and each tick used to push its own `desktop_config`. A
 * one-second drag was dozens of back-to-back encoder teardowns on the PC — a severe stream stall for
 * the whole gesture. The host's 5s keyframe cooldown (REGRESSION-GUARDS "Keyframe throttle cooldown")
 * does not help here: it throttles keyframe-driven reinits, while a config change is a legitimate
 * rebuild that always goes through.
 *
 * Leading edge plus trailing debounce:
 * - A change arriving after at least [quietMs] of quiet is sent **immediately**, so a single tap or a
 *   slow, deliberate step is not held back.
 * - A change arriving inside [quietMs] of the previous one is deferred; each further change restarts
 *   the wait, so a continuous drag sends nothing in the middle.
 * - Once changes stop for [quietMs], one trailing send goes out. [send] reads the CURRENT config when
 *   it runs, so the host always ends up on the value the user let go of, never an intermediate tick.
 *
 * A drag therefore costs at most two rebuilds (its first tick and its settled value) instead of one
 * per tick. The clock and scope are injected so this can be tested on the JVM under virtual time.
 *
 * Not thread-safe: call [onChange] from one thread (the ViewModel calls it on Main).
 */
internal class DesktopConfigPushDebouncer(
        private val scope: CoroutineScope,
        private val nowMs: () -> Long,
        private val quietMs: Long = DEFAULT_QUIET_MS,
        private val send: () -> Unit
) {
    /** Null until the first change, so the very first change is never delayed. */
    private var lastChangeAtMs: Long? = null

    /** Null until the first send, so a send is never held back waiting for a send that never happened. */
    private var lastSentAtMs: Long? = null

    private var trailingSend: Job? = null

    /** Records one settings change and either sends now or (re)schedules the trailing send. */
    fun onChange() {
        val now = nowMs()
        val previousChange = lastChangeAtMs
        val previousSend = lastSentAtMs
        lastChangeAtMs = now

        // Any pending trailing send is superseded: either this change goes out now (and carries the
        // latest config with it), or the wait restarts from this change.
        trailingSend?.cancel()
        trailingSend = null

        // Both gaps must clear quietMs: a change quiet-period alone lets a pause-then-resume drag (the
        // resuming tick lands quietMs after the trailing send that just fired) send twice within
        // milliseconds, since that tick is far from the PREVIOUS change but not from the send it caused.
        val changeQuiet = previousChange == null || now - previousChange >= quietMs
        val sendQuiet = previousSend == null || now - previousSend >= quietMs

        if (changeQuiet && sendQuiet) {
            lastSentAtMs = now
            send()
        } else {
            trailingSend =
                    scope.launch {
                        delay(quietMs)
                        trailingSend = null
                        lastSentAtMs = nowMs()
                        send()
                    }
        }
    }

    internal companion object {
        /** The 200ms floor from the perf audit: below human "did it apply?" latency, above tick rate. */
        const val DEFAULT_QUIET_MS = 200L
    }
}
