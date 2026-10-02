package com.clindsay94.remex.ui.screens.sensors

import com.clindsay94.remex.ui.screens.HomeCardState
import com.clindsay94.remex.ui.screens.HomeCardType
import com.clindsay94.remex.ui.screens.TelemetryDisplayMode
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The Sensors grid's edit-mode state machine (RemEx-wqo7a.8): layout changes only in edit mode, one
 * undo step per change (a whole drag included), remove undoable, and the Add-card button and card
 * controls shown only in edit mode.
 */
class SensorLayoutEditorTest {

    private fun card(id: String) = HomeCardState(id = id, title = id, type = HomeCardType.TELEMETRY, sensorId = id)

    private val commits = ArrayList<SensorLayout>()
    private val editor =
            SensorLayoutEditor(
                    SensorLayout(listOf(card("a"), card("b"), card("c"), card("d")), setOf("a", "b", "c", "d")),
                    onCommitted = { commits += it },
            )

    private fun order() = editor.layout.value.visibleCards.map { it.id }

    @Test
    fun `the Add-card button and the card controls show only in edit mode`() {
        val normal = SensorsChrome.forMode(editMode = false)
        assertFalse(normal.showAddButton)
        assertFalse(normal.showCardControls)
        assertFalse(normal.showEditActions)
        assertTrue(normal.showViewPicker)

        val edit = SensorsChrome.forMode(editMode = true)
        assertTrue(edit.showAddButton)
        assertTrue(edit.showCardControls)
        assertTrue(edit.showEditActions)
        assertFalse(edit.showViewPicker)
    }

    @Test
    fun `enter and leave edit mode`() {
        assertFalse(editor.editMode.value)
        editor.enterEditMode()
        assertTrue(editor.editMode.value)
        editor.exitEditMode()
        assertFalse(editor.editMode.value)
    }

    @Test
    fun `outside edit mode nothing rearranges the grid`() {
        assertFalse(editor.beginDrag("a"))
        assertFalse(editor.moveCard("a", 2))
        assertFalse(editor.cycleSpan("a"))
        assertFalse(editor.removeCard("a"))
        assertFalse(editor.clearAll())
        assertFalse(editor.setCardShown("e", true) { card("e") })
        assertEquals(listOf("a", "b", "c", "d"), order())
        assertTrue(commits.isEmpty())
        assertFalse(editor.canUndo.value)
    }

    @Test
    fun `a card's own view can change outside edit mode`() {
        assertTrue(editor.updateCard("a") { it.copy(displayMode = TelemetryDisplayMode.LINE) })
        assertEquals(TelemetryDisplayMode.LINE, editor.layout.value.cards.first().displayMode)
        assertEquals(1, commits.size)
    }

    @Test
    fun `a drag across several cells is one undo step and one save`() {
        editor.enterEditMode()
        assertTrue(editor.beginDrag("a"))
        assertEquals("a", editor.draggingId.value)
        editor.dragTo(1)
        editor.dragTo(2)
        editor.dragTo(3)
        assertTrue("nothing saved mid-drag", commits.isEmpty())
        editor.endDrag()
        assertNull(editor.draggingId.value)
        assertEquals(listOf("b", "c", "d", "a"), order())
        assertEquals(1, commits.size)

        assertTrue(editor.undo())
        assertEquals(listOf("a", "b", "c", "d"), order())
        assertFalse(editor.canUndo.value)
        assertTrue(editor.redo())
        assertEquals(listOf("b", "c", "d", "a"), order())
    }

    @Test
    fun `a long press that only opened edit mode leaves no undo step`() {
        editor.enterEditMode()
        assertTrue(editor.beginDrag("b"))
        editor.endDrag()
        assertFalse(editor.canUndo.value)
        assertTrue(commits.isEmpty())
    }

    @Test
    fun `resize cycles 1x1 2x1 2x2 and back`() {
        editor.enterEditMode()
        editor.cycleSpan("a")
        assertEquals(CardSpan.TWO_BY_ONE, editor.layout.value.cards.first { it.id == "a" }.span)
        editor.cycleSpan("a")
        assertEquals(CardSpan.TWO_BY_TWO, editor.layout.value.cards.first { it.id == "a" }.span)
        editor.cycleSpan("a")
        assertEquals(CardSpan.ONE_BY_ONE, editor.layout.value.cards.first { it.id == "a" }.span)
        assertEquals(3, commits.size)
    }

    @Test
    fun `remove is undoable and keeps the card's settings`() {
        editor.enterEditMode()
        editor.cycleSpan("b")
        assertTrue(editor.removeCard("b"))
        assertEquals(listOf("a", "c", "d"), order())
        assertTrue(editor.undo())
        assertEquals(listOf("a", "b", "c", "d"), order())
        assertEquals(CardSpan.TWO_BY_ONE, editor.layout.value.cards.first { it.id == "b" }.span)
    }

    @Test
    fun `a card added back goes to the end of the grid`() {
        editor.enterEditMode()
        editor.removeCard("a")
        assertTrue(editor.setCardShown("a", true) { error("a still exists, it must not be rebuilt") })
        assertEquals(listOf("b", "c", "d", "a"), order())
        assertTrue(editor.setCardShown("e", true) { card("e") })
        assertEquals(listOf("b", "c", "d", "a", "e"), order())
    }

    @Test
    fun `moving keeps hidden cards out of the visible order`() {
        editor.enterEditMode()
        editor.removeCard("b")
        assertTrue(editor.moveCard("d", 0))
        assertEquals(listOf("d", "a", "c"), order())
        assertTrue("the hidden card is kept", editor.layout.value.cards.any { it.id == "b" })
    }

    @Test
    fun `leaving edit mode mid-drag drops the card and saves`() {
        editor.enterEditMode()
        editor.beginDrag("a")
        editor.dragTo(2)
        editor.exitEditMode()
        assertNull(editor.draggingId.value)
        assertFalse(editor.editMode.value)
        assertEquals(listOf("b", "c", "a", "d"), order())
        assertEquals(1, commits.size)
    }

    @Test
    fun `clear all is undoable`() {
        editor.enterEditMode()
        assertTrue(editor.clearAll())
        assertTrue(order().isEmpty())
        editor.undo()
        assertEquals(listOf("a", "b", "c", "d"), order())
    }
}
