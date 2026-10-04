package com.clindsay94.remex.ui.screens.sensors

import com.clindsay94.remex.data.SensorAlertDirection
import com.clindsay94.remex.data.SensorAlertRule
import com.clindsay94.remex.data.SensorAlertSeverity
import com.clindsay94.remex.data.SensorAlerts
import com.clindsay94.remex.data.SensorAlertsState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The "Alert me..." sheet's decisions (RemEx-pp4cm.12): what it starts as, when Save works, why it cannot. */
class SensorAlertEditorLogicTest {

    private fun rule(name: String = "CPU", threshold: Double = 90.0) =
            SensorAlertRule(name, name, "°C", 61.5, threshold, SensorAlertDirection.BELOW, SensorAlertSeverity.CRITICAL)

    private val live = SensorAlertsState(connected = true, supported = true, loaded = true)

    @Test
    fun `an existing rule opens as it is`() {
        val draft = SensorAlertEditorLogic.draftFor(rule(threshold = 72.5), currentValue = 50.0)

        assertEquals(SensorAlertDirection.BELOW, draft.direction)
        assertEquals("72.5", draft.thresholdText)
        assertEquals(SensorAlertSeverity.CRITICAL, draft.severity)
        assertEquals(72.5, draft.threshold!!, 0.0)
    }

    @Test
    fun `a new rule starts above the current reading as a warning`() {
        val draft = SensorAlertEditorLogic.draftFor(null, currentValue = 61.0)

        assertEquals(SensorAlertDirection.ABOVE, draft.direction)
        assertEquals("61", draft.thresholdText)
        assertEquals(SensorAlertSeverity.WARNING, draft.severity)
    }

    @Test
    fun `a new rule for a sensor with no reading starts with an empty box that is not yet an error`() {
        val draft = SensorAlertEditorLogic.draftFor(null, currentValue = null)

        assertEquals("", draft.thresholdText)
        assertFalse(draft.isValid)
        assertFalse("blank is not an error until something wrong is typed", draft.showsError)
    }

    @Test
    fun `a reading the PC would refuse is not offered as a starting threshold`() {
        assertEquals("", SensorAlertEditorLogic.draftFor(null, currentValue = Double.NaN).thresholdText)
        assertEquals("", SensorAlertEditorLogic.draftFor(null, currentValue = 1e15).thresholdText)
    }

    @Test
    fun `the threshold text round trips whatever the phones language`() {
        for (value in listOf(0.0, 85.0, 72.5, -40.0, 1234.5)) {
            assertEquals(value, SensorAlerts.parseThreshold(SensorAlertEditorLogic.thresholdText(value))!!, 0.0)
        }
    }

    @Test
    fun `typing something that is not a number shows the error`() {
        val draft = SensorAlertEditorLogic.draftFor(null, 50.0).copy(thresholdText = "hot")

        assertTrue(draft.showsError)
        assertFalse(draft.isValid)
        assertNull(draft.threshold)
    }

    @Test
    fun `save needs a usable number and a connected PC that supports alerts`() {
        val good = SensorAlertEditorLogic.draftFor(null, 50.0)
        assertTrue(SensorAlertEditorLogic.canSave(good, live, null))
        assertFalse(SensorAlertEditorLogic.canSave(good.copy(thresholdText = ""), live, null))
        assertFalse(SensorAlertEditorLogic.canSave(good, live.copy(connected = false), null))
        assertFalse(SensorAlertEditorLogic.canSave(good, live.copy(supported = false), null))
    }

    @Test
    fun `a new rule cannot be saved at the PCs limit but an edit can`() {
        val full = live.copy(rules = (0 until SensorAlerts.MaxRules).map { rule("S$it") })
        val draft = SensorAlertEditorLogic.draftFor(null, 50.0)

        assertFalse(SensorAlertEditorLogic.canSave(draft, full, null))
        assertTrue(SensorAlertEditorLogic.canSave(draft, full, full.rules.first()))
    }

    @Test
    fun `the sheet says why it cannot save`() {
        assertEquals(SensorAlertBlock.NOT_CONNECTED, SensorAlertEditorLogic.blockedReason(SensorAlertsState(), null))
        assertEquals(SensorAlertBlock.PC_TOO_OLD, SensorAlertEditorLogic.blockedReason(live.copy(supported = false), null))
        assertEquals(
                SensorAlertBlock.TOO_MANY,
                SensorAlertEditorLogic.blockedReason(live.copy(rules = (0 until SensorAlerts.MaxRules).map { rule("S$it") }), null)
        )
        assertNull(SensorAlertEditorLogic.blockedReason(live, null))
        assertNull(
                "an existing rule can always be edited",
                SensorAlertEditorLogic.blockedReason(live.copy(rules = (0 until SensorAlerts.MaxRules).map { rule("S$it") }), rule("S1"))
        )
    }
}
