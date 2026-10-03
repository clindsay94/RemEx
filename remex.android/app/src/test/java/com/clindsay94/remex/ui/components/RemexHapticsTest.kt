package com.clindsay94.remex.ui.components

import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import com.clindsay94.remex.ui.screens.ContrastDetents
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** The 3.0 haptic map and its firing rules (RemEx-wqo7a.8). */
class RemexHapticsTest {

    @Test
    fun `every event maps to the M3 haptic type the pass chose`() {
        val expected: Map<RemexHapticEvent, HapticFeedbackType?> =
            mapOf(
                // Plain taps are silent: haptics mark meaning, not every touch.
                RemexHapticEvent.Press to null,
                RemexHapticEvent.Select to HapticFeedbackType.SegmentTick,
                RemexHapticEvent.ToggleOn to HapticFeedbackType.ToggleOn,
                RemexHapticEvent.ToggleOff to HapticFeedbackType.ToggleOff,
                RemexHapticEvent.Detent to HapticFeedbackType.SegmentTick,
                RemexHapticEvent.Step to HapticFeedbackType.SegmentFrequentTick,
                RemexHapticEvent.Release to HapticFeedbackType.GestureEnd,
                RemexHapticEvent.CommandSent to HapticFeedbackType.ContextClick,
                RemexHapticEvent.Confirm to HapticFeedbackType.Confirm,
                RemexHapticEvent.Reject to HapticFeedbackType.Reject,
                RemexHapticEvent.LongPress to HapticFeedbackType.LongPress,
                RemexHapticEvent.Refresh to HapticFeedbackType.GestureThresholdActivate,
            )
        // Anti-vacuity: a new event must be added here too, or this fails.
        assertEquals(RemexHapticEvent.entries.toSet(), expected.keys)
        for ((event, type) in expected) {
            assertEquals("$event", type, RemexHaptics.typeFor(event))
        }
    }

    @Test
    fun `outcomes and toggles feel different from each other`() {
        assertNotEquals(RemexHaptics.typeFor(RemexHapticEvent.Confirm), RemexHaptics.typeFor(RemexHapticEvent.Reject))
        assertNotEquals(RemexHaptics.typeFor(RemexHapticEvent.ToggleOn), RemexHaptics.typeFor(RemexHapticEvent.ToggleOff))
        // A step comes often, so it is lighter than a detent.
        assertNotEquals(RemexHaptics.typeFor(RemexHapticEvent.Step), RemexHaptics.typeFor(RemexHapticEvent.Detent))
    }

    @Test
    fun `toggle picks on or off from the new state`() {
        assertEquals(RemexHapticEvent.ToggleOn, RemexHaptics.toggle(true))
        assertEquals(RemexHapticEvent.ToggleOff, RemexHaptics.toggle(false))
    }

    @Test
    fun `contrast slider ticks once as it lands on each detent`() {
        val detents = ContrastDetents.DETENTS
        // Dragging into the 0 detent's radius snaps to 0: one tick.
        assertTrue(RemexHaptics.landedOnDetent(0.3f, ContrastDetents.snap(0.03f), detents))
        // Holding on the detent: the slider reports 0 again, no second tick.
        assertFalse(RemexHaptics.landedOnDetent(0f, ContrastDetents.snap(-0.02f), detents))
        // Between detents nothing ticks.
        assertFalse(RemexHaptics.landedOnDetent(0.3f, ContrastDetents.snap(0.4f), detents))
        // Both extremes tick.
        assertTrue(RemexHaptics.landedOnDetent(0.8f, ContrastDetents.snap(0.97f), detents))
        assertTrue(RemexHaptics.landedOnDetent(-0.8f, ContrastDetents.snap(-0.99f), detents))
        // Leaving a detent is not landing on one.
        assertFalse(RemexHaptics.landedOnDetent(1f, ContrastDetents.snap(0.9f), detents))
    }

    @Test
    fun `a stepped slider ticks only when the step changes`() {
        assertTrue(RemexHaptics.steppedTo(1.0f, 1.25f))
        assertFalse(RemexHaptics.steppedTo(1.25f, 1.25f))
    }

    @Test
    fun `only a new error buzzes`() {
        assertTrue(RemexHaptics.failureAppeared(null, "Wrong PIN"))
        assertTrue(RemexHaptics.failureAppeared("Wrong PIN", "Timed out"))
        // The same error again (recomposition, rotation) and a cleared error stay silent.
        assertFalse(RemexHaptics.failureAppeared("Wrong PIN", "Wrong PIN"))
        assertFalse(RemexHaptics.failureAppeared("Wrong PIN", null))
        assertFalse(RemexHaptics.failureAppeared(null, null))
    }
}
