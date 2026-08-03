package com.clindsay94.remex

import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlinx.coroutines.suspendCancellableCoroutine

/**
 * Runs a blocking call so that cancelling the caller actually ends the wait (RemEx-defb).
 *
 * **A `withTimeout` AROUND A BLOCKING JNI FRAME DOES NOTHING, WHICH IS THE BUG THIS EXISTS FOR.**
 * Coroutine cancellation is cooperative: it sets a flag and takes effect at the next suspension
 * point, and a thread sitting inside a native call has no suspension point until that call returns.
 * So `withTimeout(15_000)` around a 90-second pairing handshake reported its timeout after ninety
 * seconds, not fifteen — the code said one thing and the user experienced another.
 *
 * TWO HALVES, AND BOTH ARE NEEDED.
 *
 * [block] runs on its own thread, so the caller can stop waiting for it. That alone gives an honest
 * timeout, and alone it is not enough: the abandoned call keeps running, and the pairing natives hold
 * a process-wide lock for their whole budget, so the user's next attempt would queue behind the one
 * they just gave up on. That reads as "retry is broken", which is worse than the slow wait it
 * replaced.
 *
 * [cancel] is what closes that. It runs the moment cancellation is REQUESTED — not when the frame
 * returns — so the abandoned work is told to stop rather than merely ignored.
 *
 * A plain thread rather than a dispatcher, deliberately: the work must OUTLIVE the coroutine that
 * asked for it, and handing it to a shared pool would tie up a worker for the remainder of a budget
 * nobody is waiting on. There are only ever three of these in a pairing, all user-initiated.
 *
 * @param name identifies the thread and the failure log; not user-facing.
 * @param cancel asks the underlying work to stop. Must not block, and must be safe to call when
 * there is nothing running — cancellation can arrive after the work has already finished.
 * @param onCancelFailure reports a [cancel] that threw. Separate so the caller decides how to log.
 */
internal suspend fun <T> abandonable(
    name: String,
    cancel: () -> Unit,
    onCancelFailure: (Throwable) -> Unit = {},
    block: () -> T,
): T =
    suspendCancellableCoroutine { continuation ->
        // REGISTERED BEFORE THE THREAD STARTS, AND THE ORDER IS LOAD-BEARING. On an
        // already-cancelled continuation this handler fires synchronously, right here — so [cancel]
        // can run before [block] has begun, and the callee must be able to cope with being told to
        // stop something that has not started. The pairing side relies on exactly that: it records
        // the cancel and the attempt starts already-abandoned instead of running a full budget with
        // nobody waiting. Swap these two statements and that path silently stops working.
        continuation.invokeOnCancellation {
            // Never let a failure to cancel become the caller's exception: the caller has already
            // given up and moved on, and this runs on whichever thread requested cancellation.
            runCatching(cancel).onFailure(onCancelFailure)
        }

        Thread({
            val outcome = runCatching(block)
            outcome.fold(
                // isActive guards a continuation that was already cancelled — resuming one is a
                // no-op in kotlinx, but the check keeps the intent visible: a result nobody is
                // waiting for is dropped, not delivered late.
                onSuccess = { if (continuation.isActive) continuation.resume(it) },
                onFailure = { if (continuation.isActive) continuation.resumeWithException(it) },
            )
        }, "remex-abandonable-$name").start()
    }
