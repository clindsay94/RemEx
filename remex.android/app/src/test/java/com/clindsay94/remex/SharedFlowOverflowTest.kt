package com.clindsay94.remex

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

/**
 * The client flows no longer discard messages while a collector is busy (RemEx-e7npu).
 *
 * **THE BUG WAS A DEFAULT NOBODY CHOSE.** `MutableSharedFlow(replay = 1)` means
 * `extraBufferCapacity = 0` and `onBufferOverflow = SUSPEND`. Every emitter is `tryEmit`, and
 * `tryEmit` on a SUSPEND flow with no room RETURNS FALSE AND DISCARDS THE VALUE — no exception, no
 * log, and a Boolean nobody reads. Capacity is one slot shared with the replay cache, and a collector
 * that is inside its lambda has not yet taken the next value, so a view model parsing JSON is enough
 * to fill it.
 *
 * Precisely: such a collector still leaves room for the NEXT emit, which displaces the replayed
 * value; it is the one after that which is refused. So the old cost was every second value, not every
 * value — which is why the burst below emits five rather than two.
 *
 * **HELD WITH A GATE, NOT A SLEEP.** The collector blocks on a `CompletableDeferred` until the whole
 * burst has been emitted, so the condition that used to drop values is a certainty here rather than a
 * race. `Dispatchers.Unconfined` makes the subscription take effect before the first emit, which
 * matters: a flow with no subscriber buffers nothing and would pass whatever the settings.
 *
 * These run against the real singleton, whose `replay = 1` values outlive a test, so every assertion
 * filters on a per-test prefix and the replay caches are reset afterwards.
 *
 * **TWO OF ELEVEN FLOWS ARE COVERED HERE, AS REPRESENTATIVES OF THE TWO CLASSES.** The rest are not,
 * and reverting any of them to the bare `MutableSharedFlow(replay = 1)` leaves this suite green —
 * including `desktopCursorBinary`, the highest-rate flow on the object, and `desktopCursorShape`,
 * whose collector holds its slot across an off-main decode.
 */
class SharedFlowOverflowTest {

    /** Fails loudly rather than hanging if the values never arrive. */
    private val timeoutMs = 5_000L

    @After
    fun clearReplayCaches() {
        // The singleton outlives the test. Without this, `desktopErrors` keeps a fake stream error in
        // its replay cache for the rest of the JVM run, and the next test that collects it would get
        // one delivered as its first value with nothing pointing back here.
        RemexClientManager.resetStreamReplayCachesForTests()
    }

    @Test
    fun `a burst of desktop errors survives a busy collector`() = runBlocking {
        // **THE ONE THAT MATTERS MOST.** Every other flow here carries a snapshot, where losing an
        // older value is harmless because the newer one says everything it said. These are distinct
        // events: each is the only account of a separate failure. RemEx-iaxc put the "the PC is
        // discarding your input" advisory on this flow specifically to break a silence, and dropping
        // it would put that silence back.
        val tag = "burst-errors"
        val seen = mutableListOf<String>()
        val hold = CompletableDeferred<Unit>()
        val gotAll = CompletableDeferred<Unit>()

        val job = launch(Dispatchers.Unconfined) {
            RemexClientManager.desktopErrors.collect { value ->
                if (value.startsWith(tag)) {
                    seen.add(value)
                    if (seen.size == 1) hold.await()
                    if (seen.size == 5) gotAll.complete(Unit)
                }
            }
        }

        repeat(5) { RemexClientManager.onDesktopError("$tag-$it") }
        hold.complete(Unit)
        withTimeout(timeoutMs) { gotAll.await() }
        job.cancel()

        assertEquals(
                "a busy collector must not cost an error message",
                listOf("$tag-0", "$tag-1", "$tag-2", "$tag-3", "$tag-4"),
                seen
        )
    }

    @Test
    fun `the newest telemetry reaches a busy collector`() = runBlocking {
        // Latest-wins, and the assertion says exactly that rather than pretending nothing is dropped.
        // A snapshot flow SHOULD discard superseded readings — the fix is that `tryEmit` can no longer
        // FAIL, so the newest reading always gets through instead of being silently refused whenever
        // the collector happened to be mid-parse. Asserting "all five arrive" here would be asserting
        // a guarantee this flow deliberately does not make.
        val tag = "tele"
        val seen = mutableListOf<String>()
        val hold = CompletableDeferred<Unit>()
        val sawNewest = CompletableDeferred<Unit>()

        val job = launch(Dispatchers.Unconfined) {
            RemexClientManager.telemetry.collect { value ->
                if (value.startsWith(tag)) {
                    seen.add(value)
                    if (seen.size == 1) hold.await()
                    if (value == "$tag-4") sawNewest.complete(Unit)
                }
            }
        }

        repeat(5) { RemexClientManager.onTelemetryUpdate("$tag-$it") }
        hold.complete(Unit)
        try {
            withTimeout(timeoutMs) { sawNewest.await() }
        } catch (e: TimeoutCancellationException) {
            // Caught so the failure NAMES the property. An uncaught timeout here reports
            // "TimeoutCancellationException" and says nothing about telemetry, and the assertTrue
            // that followed could never fire because the await had already proven its condition.
            job.cancel()
            fail("the newest reading never reached a busy collector; saw $seen")
        }
        job.cancel()
    }

    @Test
    fun `the newest error survives even when the error buffer overflows`() = runBlocking {
        // **THE BEAD'S OWN BUG, INVERTED, ON THE FLOW IT SAYS MATTERS MOST.** DROP_LATEST would keep
        // this flow buffered and still pass the burst test above, which never fills the buffer - while
        // discarding the NEWEST error once the queue is full. For the only channel that explains a
        // failure, the newest is the one worth keeping, so this overflows it deliberately and asserts
        // which end is sacrificed.
        val tag = "overflow-errors"
        val seen = mutableListOf<String>()
        val hold = CompletableDeferred<Unit>()
        val sawNewest = CompletableDeferred<Unit>()
        val total = 14

        val job = launch(Dispatchers.Unconfined) {
            RemexClientManager.desktopErrors.collect { value ->
                if (value.startsWith(tag)) {
                    seen.add(value)
                    if (seen.size == 1) hold.await()
                    if (value == "$tag-${total - 1}") sawNewest.complete(Unit)
                }
            }
        }

        repeat(total) { RemexClientManager.onDesktopError("$tag-$it") }
        hold.complete(Unit)
        try {
            withTimeout(timeoutMs) { sawNewest.await() }
        } catch (e: TimeoutCancellationException) {
            job.cancel()
            fail("the newest error was dropped when the buffer overflowed; saw $seen")
        }
        job.cancel()

        // Losing the oldest is the accepted cost; losing the newest is the bug.
        assertTrue("more than the buffer was emitted, so something must be lost", seen.size < total)
    }
}
