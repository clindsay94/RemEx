package com.clindsay94.remex.ui.components

import com.clindsay94.remex.service.TransferState
import com.clindsay94.remex.service.TransferState.Active
import com.clindsay94.remex.service.TransferState.Cancelled
import com.clindsay94.remex.service.TransferState.Done
import com.clindsay94.remex.service.TransferState.Failed
import com.clindsay94.remex.service.TransferState.Paused
import com.clindsay94.remex.service.TransferState.Queued
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** One haptic per finished batch of transfers, never one per file (RemEx-wqo7a.8). */
class TransferOutcomeHapticsTest {

    /** Feeds [steps] through the tracker from [start] and returns every event it fired, in order. */
    private fun run(start: Map<String, TransferState>, vararg steps: Map<String, TransferState>): List<RemexHapticEvent> {
        var tracker = TransferOutcomeHaptics.baseline(start)
        val fired = mutableListOf<RemexHapticEvent>()
        for (states in steps) {
            val (next, event) = TransferOutcomeHaptics.next(tracker, states)
            tracker = next
            event?.let(fired::add)
        }
        return fired
    }

    @Test
    fun `a batch that all lands confirms once, when the last file lands`() {
        val fired =
            run(
                mapOf("a" to Queued, "b" to Queued, "c" to Queued),
                mapOf("a" to Active, "b" to Queued, "c" to Queued),
                mapOf("a" to Done, "b" to Active, "c" to Queued),
                mapOf("a" to Done, "b" to Done, "c" to Active),
                mapOf("a" to Done, "b" to Done, "c" to Done),
            )
        assertEquals(listOf(RemexHapticEvent.Confirm), fired)
    }

    @Test
    fun `any failure in the batch makes its end a reject`() {
        val fired =
            run(
                mapOf("a" to Active, "b" to Queued),
                mapOf("a" to Failed, "b" to Active),
                mapOf("a" to Failed, "b" to Done),
            )
        assertEquals(listOf(RemexHapticEvent.Reject), fired)
    }

    @Test
    fun `a batch the user cancelled ends silently`() {
        val fired = run(mapOf("a" to Active), mapOf("a" to Cancelled))
        assertEquals(emptyList<RemexHapticEvent>(), fired)
    }

    @Test
    fun `pausing the rest of a batch is not its end`() {
        val fired =
            run(
                mapOf("a" to Active, "b" to Queued),
                mapOf("a" to Done, "b" to Paused),
                // Resumed later and finished: that is the end.
                mapOf("a" to Done, "b" to Active),
                mapOf("a" to Done, "b" to Done),
            )
        assertEquals(listOf(RemexHapticEvent.Confirm), fired)
    }

    @Test
    fun `what finished while the app was away stays silent on return`() {
        // The baseline is taken on resume, so rows already Done or Failed never count.
        val (_, event) =
            TransferOutcomeHaptics.next(
                TransferOutcomeHaptics.baseline(mapOf("a" to Done, "b" to Failed)),
                mapOf("a" to Done, "b" to Failed),
            )
        assertNull(event)
    }

    @Test
    fun `two batches buzz twice, and the second does not inherit the first's failure`() {
        val fired =
            run(
                mapOf("a" to Active),
                mapOf("a" to Failed),
                mapOf("a" to Failed, "b" to Queued),
                mapOf("a" to Failed, "b" to Active),
                mapOf("a" to Failed, "b" to Done),
            )
        assertEquals(listOf(RemexHapticEvent.Reject, RemexHapticEvent.Confirm), fired)
    }

    @Test
    fun `a row cleared from the queue mid-batch does not end it early`() {
        val fired =
            run(
                mapOf("a" to Done, "b" to Active),
                // The finished row is cleared while b is still moving.
                mapOf("b" to Active),
                mapOf("b" to Done),
            )
        assertEquals(listOf(RemexHapticEvent.Confirm), fired)
    }
}
