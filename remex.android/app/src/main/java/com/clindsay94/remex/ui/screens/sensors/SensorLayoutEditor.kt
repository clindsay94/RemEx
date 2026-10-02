package com.clindsay94.remex.ui.screens.sensors

import com.clindsay94.remex.ui.screens.HomeCardState
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * The Sensors grid's cards and which of them are shown. A card that is removed keeps its settings
 * (view, title, size) and only leaves [enabled], so undo and re-adding bring it back as it was.
 * [cards] is in grid order.
 */
data class SensorLayout(val cards: List<HomeCardState>, val enabled: Set<String>) {
    val visibleCards: List<HomeCardState>
        get() = cards.filter { it.id in enabled }
}

/** What the Sensors screen shows around the cards in each mode (RemEx-wqo7a.8). */
data class SensorsChrome(
        /** The Add-card button. Edit mode only: outside it nothing on the screen rearranges cards. */
        val showAddButton: Boolean,
        /** Pin to Home, resize and remove on every card. Edit mode only. */
        val showCardControls: Boolean,
        /** The per-card view picker. Outside edit mode only, so a card has one set of controls at a time. */
        val showViewPicker: Boolean,
        /** Undo, Redo and Done in the top bar. */
        val showEditActions: Boolean,
) {
    companion object {
        fun forMode(editMode: Boolean): SensorsChrome =
                SensorsChrome(
                        showAddButton = editMode,
                        showCardControls = editMode,
                        showViewPicker = !editMode,
                        showEditActions = editMode,
                )
    }
}

/**
 * The Sensors grid's edit-mode state machine (RemEx-wqo7a.8). Pure, so the rules are unit-tested
 * without a device: `DashboardViewModel` is an `AndroidViewModel`, and this module has no Robolectric.
 *
 * NORMAL -> EDIT on [enterEditMode] (a long press on a card, or the menu's Edit); EDIT -> NORMAL on
 * [exitEditMode] (Done, or back). Layout changes that rearrange the grid - move, resize, remove, add -
 * are refused outside edit mode, which is what keeps a stray drag in normal mode from moving a card.
 * A card's own settings (its view and title) can still be changed from its view picker at any time.
 *
 * Every committed change takes ONE undo snapshot first and then calls [onCommitted] so the caller can
 * persist. A drag is one change however many cells it crosses: [beginDrag] remembers where it
 * started, [dragTo] moves without a snapshot or a save, and [endDrag] records the one undo step and
 * saves - or does neither when the card ended where it started.
 */
class SensorLayoutEditor(
        initial: SensorLayout,
        private val onCommitted: (SensorLayout) -> Unit = {},
        private val maxHistory: Int = 30,
) {
    private val _layout = MutableStateFlow(initial)
    val layout: StateFlow<SensorLayout> = _layout.asStateFlow()

    private val _editMode = MutableStateFlow(false)
    val editMode: StateFlow<Boolean> = _editMode.asStateFlow()

    private val _draggingId = MutableStateFlow<String?>(null)
    val draggingId: StateFlow<String?> = _draggingId.asStateFlow()

    private val undoStack = ArrayDeque<SensorLayout>()
    private val redoStack = ArrayDeque<SensorLayout>()

    private val _canUndo = MutableStateFlow(false)
    val canUndo: StateFlow<Boolean> = _canUndo.asStateFlow()
    private val _canRedo = MutableStateFlow(false)
    val canRedo: StateFlow<Boolean> = _canRedo.asStateFlow()

    private val _revision = MutableStateFlow(0L)

    /**
     * Bumped by every change to the layout's history: a commit, a finished drag, undo, redo, load.
     * An Undo offered for one change (the card-removed snackbar) records it and passes it back to
     * [undoIfUnchanged], so it can never undo some later change instead (phase 3 review R5).
     */
    val revision: StateFlow<Long> = _revision.asStateFlow()

    /**
     * Undoes the latest change only if nothing has changed since [revision] was read. Returns false,
     * and changes nothing, when a newer edit has landed: that Undo was offered for a change that is no
     * longer the latest, and plain [undo] would revert the newer one.
     */
    fun undoIfUnchanged(revision: Long): Boolean {
        if (revision != _revision.value) return false
        return undo()
    }

    /** Replaces the layout from storage. Not undoable: there is nothing before a load to go back to. */
    fun load(layout: SensorLayout) {
        _draggingId.value = null
        dragStart = null
        _layout.value = layout
        undoStack.clear()
        redoStack.clear()
        refreshHistoryFlags()
    }

    fun enterEditMode() {
        _editMode.value = true
    }

    /** Leaves edit mode; a drag still in progress is dropped where it is and saved. */
    fun exitEditMode() {
        if (_draggingId.value != null) endDrag()
        _editMode.value = false
    }

    /** The layout as it was when the current drag began; the undo step if the drag changes anything. */
    private var dragStart: SensorLayout? = null

    /** Starts moving [id]. Refused outside edit mode or for a card that is not on the grid. */
    fun beginDrag(id: String): Boolean {
        if (!_editMode.value || _layout.value.visibleCards.none { it.id == id }) return false
        if (_draggingId.value != null) endDrag()
        dragStart = _layout.value
        _draggingId.value = id
        return true
    }

    /** Moves the dragged card to grid position [toIndex] (an index into the visible cards). */
    fun dragTo(toIndex: Int) {
        val id = _draggingId.value ?: return
        _layout.value = moved(_layout.value, id, toIndex) ?: return
    }

    /**
     * Drops the dragged card. A drag that ends where it began (the long press that only opened edit
     * mode, or a card carried out and back) leaves no undo step and writes nothing.
     */
    fun endDrag() {
        if (_draggingId.value == null) return
        _draggingId.value = null
        val start = dragStart
        dragStart = null
        if (start == null || start == _layout.value) return
        undoStack.addLast(start)
        if (undoStack.size > maxHistory) undoStack.removeFirst()
        redoStack.clear()
        refreshHistoryFlags()
        onCommitted(_layout.value)
    }

    /** One undoable move, for callers without a drag (an accessibility action). */
    fun moveCard(id: String, toIndex: Int): Boolean {
        if (!_editMode.value) return false
        val next = moved(_layout.value, id, toIndex) ?: return false
        commit(next)
        return true
    }

    fun setCardSpan(id: String, span: CardSpan): Boolean {
        if (!_editMode.value) return false
        val current = _layout.value
        val card = current.cards.firstOrNull { it.id == id } ?: return false
        if (card.span == span) return false
        commit(current.copy(cards = current.cards.map { if (it.id == id) it.copy(span = span) else it }))
        return true
    }

    /** The resize cycle: 1x1 -> 2x1 -> 2x2 -> 1x1. */
    fun cycleSpan(id: String): Boolean {
        val card = _layout.value.cards.firstOrNull { it.id == id } ?: return false
        return setCardSpan(id, card.span.next())
    }

    fun removeCard(id: String): Boolean {
        if (!_editMode.value) return false
        val current = _layout.value
        if (id !in current.enabled) return false
        commit(current.copy(enabled = current.enabled - id))
        return true
    }

    /** Removes every card (undoable). */
    fun clearAll(): Boolean {
        if (!_editMode.value || _layout.value.enabled.isEmpty()) return false
        commit(_layout.value.copy(enabled = emptySet()))
        return true
    }

    /**
     * Shows or hides [id] from the Add-card list. A card that is shown again goes to the END of the
     * grid, where the user is looking for it, not back into a slot they have since rearranged.
     * [create] builds the card when it has never existed; null (its sensor is not reporting) still
     * marks it shown, so it appears once the sensor does.
     */
    fun setCardShown(id: String, shown: Boolean, create: () -> HomeCardState?): Boolean {
        if (!_editMode.value) return false
        val current = _layout.value
        if (!shown) {
            if (id !in current.enabled) return false
            commit(current.copy(enabled = current.enabled - id))
            return true
        }
        if (id in current.enabled) return false
        val existing = current.cards.firstOrNull { it.id == id } ?: create()
        val cards =
                if (existing == null) current.cards
                else current.cards.filterNot { it.id == id } + existing
        commit(SensorLayout(cards, current.enabled + id))
        return true
    }

    /** Adds [card] if no card has its id, without history: the default cards filling in as sensors report. */
    fun addIfMissing(card: HomeCardState) {
        val current = _layout.value
        if (current.cards.any { it.id == card.id }) return
        _layout.value = current.copy(cards = current.cards + card)
    }

    /** One undoable change to a single card's own settings (view, title, live value). */
    fun updateCard(id: String, transform: (HomeCardState) -> HomeCardState): Boolean {
        val current = _layout.value
        val card = current.cards.firstOrNull { it.id == id } ?: return false
        val updated = transform(card)
        if (updated == card) return false
        commit(current.copy(cards = current.cards.map { if (it.id == id) updated else it }))
        return true
    }

    fun undo(): Boolean {
        if (_draggingId.value != null) endDrag()
        if (undoStack.isEmpty()) return false
        redoStack.addLast(_layout.value)
        _layout.value = undoStack.removeLast()
        refreshHistoryFlags()
        onCommitted(_layout.value)
        return true
    }

    fun redo(): Boolean {
        if (_draggingId.value != null) endDrag()
        if (redoStack.isEmpty()) return false
        undoStack.addLast(_layout.value)
        _layout.value = redoStack.removeLast()
        refreshHistoryFlags()
        onCommitted(_layout.value)
        return true
    }

    private fun commit(next: SensorLayout) {
        snapshot()
        _layout.value = next
        onCommitted(next)
    }

    private fun snapshot() {
        undoStack.addLast(_layout.value)
        if (undoStack.size > maxHistory) undoStack.removeFirst()
        redoStack.clear()
        refreshHistoryFlags()
    }

    private fun refreshHistoryFlags() {
        _canUndo.value = undoStack.isNotEmpty()
        _canRedo.value = redoStack.isNotEmpty()
        // Every path that changes the history comes through here, so this is the one place to count.
        _revision.value = _revision.value + 1
    }

    /**
     * [layout] with visible card [id] moved to visible position [toIndex] (clamped), or null when
     * nothing moves. Hidden cards keep their relative order after the visible ones.
     */
    private fun moved(layout: SensorLayout, id: String, toIndex: Int): SensorLayout? {
        val visible = layout.visibleCards.toMutableList()
        val from = visible.indexOfFirst { it.id == id }
        if (from < 0) return null
        val to = toIndex.coerceIn(0, visible.lastIndex)
        if (from == to) return null
        visible.add(to, visible.removeAt(from))
        val hidden = layout.cards.filterNot { it.id in layout.enabled }
        return layout.copy(cards = visible + hidden)
    }
}
