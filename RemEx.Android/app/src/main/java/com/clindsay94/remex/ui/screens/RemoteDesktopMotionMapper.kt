package com.clindsay94.remex.ui.screens

import android.view.MotionEvent

/**
 * Pure data model for a single pointer sample mapped from an Android [MotionEvent].
 *
 * All coordinates are in the host logical space (0..hostWidth, 0..hostHeight) for absolute samples,
 * or raw deltas for relative samples. Hover events use the same absolute coordinate space.
 *
 * This is a plain data class with no Android framework dependencies beyond [PointerPhase] and
 * [PointerToolKind], making it trivially unit-testable on JVM.
 */
data class PointerSampleData(
        val timestamp: Long,
        val pointerId: Int,
        val phase: PointerPhase,
        val toolKind: PointerToolKind,
        val deviceKind: PointerDeviceKind,
        // Absolute host coordinates. 0f means "not set / use delta".
        val logicalX: Float = 0f,
        val logicalY: Float = 0f,
        // Relative delta. 0f means "not set / use absolute".
        val dx: Float = 0f,
        val dy: Float = 0f,
        val pressure: Float = 0f,
        val hoverDistance: Float? = null,
        val tiltX: Float? = null,
        val tiltY: Float? = null,
        val orientation: Float? = null,
        val buttonMask: Int = 0,
        val coalescedHistory: List<PointerSampleData>? = null,
)

enum class PointerPhase(val wireValue: String) {
    HoverStart("HoverStart"),
    HoverMove("HoverMove"),
    HoverEnd("HoverEnd"),
    ContactStart("ContactStart"),
    ContactMove("ContactMove"),
    ContactEnd("ContactEnd"),
    ButtonPress("ButtonPress"),
    ButtonRelease("ButtonRelease"),
}

enum class PointerToolKind(val wireValue: String) {
    None("None"),
    Pen("Pen"),
    Eraser("Eraser"),
    Mouse("Mouse"),
    Finger("Finger"),
}

enum class PointerDeviceKind(val wireValue: String) {
    Mouse("Mouse"),
    Touch("Touch"),
    Stylus("Stylus"),
    Eraser("Eraser"),
    Unknown("Unknown"),
}

/**
 * Maps Android [MotionEvent] data to one or more [PointerSampleData] objects.
 *
 * This object is stateless; callers must provide the host coordinate transform function
 * [toHostCoords] so this mapper has no dependency on view layout state.
 *
 * All mapping logic is pure and testable without an Android runtime.
 */
object RemoteDesktopMotionMapper {

    /**
     * Maps a raw contact [MotionEvent] (ACTION_DOWN, ACTION_MOVE, ACTION_UP, ACTION_BUTTON_PRESS,
     * ACTION_BUTTON_RELEASE, ACTION_CANCEL) to a list of [PointerSampleData].
     *
     * The returned list includes coalesced historical samples (oldest first) followed by the
     * primary sample.
     *
     * @param event The raw MotionEvent from [android.view.View.onTouchEvent].
     * @param toHostCoords A function that maps (viewX, viewY) → (hostX, hostY). Return null to
     * signal the coordinate is outside the active surface.
     * @return A non-empty list of samples, or an empty list if the event should be ignored.
     */
    fun mapContactEvent(
            event: MotionEvent,
            toHostCoords: (Float, Float) -> Pair<Float, Float>?,
    ): List<PointerSampleData> {
        val actionMasked = event.actionMasked
        val pointerIndex = event.actionIndex

        val toolKind = toolKindFromMotionEvent(event, pointerIndex)
        val deviceKind = deviceKindFromToolKind(toolKind)

        val phase = contactPhaseFromAction(actionMasked) ?: return emptyList()

        val buttonMask = buildButtonMask(event)

        // Build primary sample from current event position
        val primary =
                buildSampleFromEventPoint(
                        event = event,
                        pointerIndex = pointerIndex,
                        phase = phase,
                        toolKind = toolKind,
                        deviceKind = deviceKind,
                        buttonMask = buttonMask,
                        toHostCoords = toHostCoords,
                )
                        ?: return emptyList()

        // For MOVE events, prepend coalesced historical samples (oldest first).
        val history: List<PointerSampleData>? =
                if (actionMasked == MotionEvent.ACTION_MOVE && event.historySize > 0) {
                    val histList = mutableListOf<PointerSampleData>()
                    for (h in 0 until event.historySize) {
                        val host =
                                toHostCoords(
                                        event.getHistoricalX(pointerIndex, h),
                                        event.getHistoricalY(pointerIndex, h),
                                )
                                        ?: continue
                        histList.add(
                                PointerSampleData(
                                        timestamp = event.getHistoricalEventTime(h),
                                        pointerId = event.getPointerId(pointerIndex),
                                        phase = PointerPhase.ContactMove,
                                        toolKind = toolKind,
                                        deviceKind = deviceKind,
                                        logicalX = host.first,
                                        logicalY = host.second,
                                        pressure = event.getHistoricalPressure(pointerIndex, h),
                                        tiltX =
                                                event.getHistoricalAxisValueIfPresent(
                                                        MotionEvent.AXIS_TILT,
                                                        pointerIndex,
                                                        h
                                                ),
                                        buttonMask = buttonMask,
                                )
                        )
                    }
                    histList.ifEmpty { null }
                } else null

        return if (history != null) {
            listOf(primary.copy(coalescedHistory = history))
        } else {
            listOf(primary)
        }
    }

    /**
     * Maps a hover [MotionEvent] (ACTION_HOVER_ENTER, ACTION_HOVER_MOVE, ACTION_HOVER_EXIT) from
     * [android.view.View.onGenericMotionEvent] to a single [PointerSampleData].
     *
     * @return A sample, or null if the event type is unrecognised or coordinate mapping fails.
     */
    fun mapHoverEvent(
            event: MotionEvent,
            toHostCoords: (Float, Float) -> Pair<Float, Float>?,
    ): PointerSampleData? {
        val phase =
                when (event.actionMasked) {
                    MotionEvent.ACTION_HOVER_ENTER -> PointerPhase.HoverStart
                    MotionEvent.ACTION_HOVER_MOVE -> PointerPhase.HoverMove
                    MotionEvent.ACTION_HOVER_EXIT -> PointerPhase.HoverEnd
                    else -> return null
                }

        val toolKind = toolKindFromMotionEvent(event, 0)
        val deviceKind = deviceKindFromToolKind(toolKind)
        val host = toHostCoords(event.x, event.y) ?: return null

        return PointerSampleData(
                timestamp = event.eventTime,
                pointerId = event.getPointerId(0),
                phase = phase,
                toolKind = toolKind,
                deviceKind = deviceKind,
                logicalX = host.first,
                logicalY = host.second,
                pressure = event.pressure,
                hoverDistance = event.getAxisValueIfPresent(MotionEvent.AXIS_DISTANCE),
                tiltX = event.getAxisValueIfPresent(MotionEvent.AXIS_TILT),
                orientation = event.getAxisValueIfPresent(MotionEvent.AXIS_ORIENTATION),
                buttonMask = buildButtonMask(event),
        )
    }

    // ── Internal helpers ───────────────────────────────────────────────────────

    private fun buildSampleFromEventPoint(
            event: MotionEvent,
            pointerIndex: Int,
            phase: PointerPhase,
            toolKind: PointerToolKind,
            deviceKind: PointerDeviceKind,
            buttonMask: Int,
            toHostCoords: (Float, Float) -> Pair<Float, Float>?,
    ): PointerSampleData? {
        val host =
                toHostCoords(
                        event.getX(pointerIndex),
                        event.getY(pointerIndex),
                )
                        ?: return null

        return PointerSampleData(
                timestamp = event.eventTime,
                pointerId = event.getPointerId(pointerIndex),
                phase = phase,
                toolKind = toolKind,
                deviceKind = deviceKind,
                logicalX = host.first,
                logicalY = host.second,
                pressure = event.getPressure(pointerIndex),
                hoverDistance =
                        event.getAxisValueIfPresent(MotionEvent.AXIS_DISTANCE, pointerIndex),
                tiltX = event.getAxisValueIfPresent(MotionEvent.AXIS_TILT, pointerIndex),
                tiltY = null, // Android reports combined tilt via AXIS_TILT; decompose via
                // orientation
                orientation =
                        event.getAxisValueIfPresent(MotionEvent.AXIS_ORIENTATION, pointerIndex),
                buttonMask = buttonMask,
        )
    }

    private fun contactPhaseFromAction(actionMasked: Int): PointerPhase? =
            phaseFromAction(actionMasked)

    /**
     * Maps a raw action constant (e.g. [MotionEvent.ACTION_DOWN]) to the protocol [PointerPhase],
     * or null if the action is unrecognised.
     *
     * Exposed as `internal` so JVM unit tests can validate phase mapping without requiring an
     * actual [MotionEvent] mock.
     */
    internal fun phaseFromAction(actionMasked: Int): PointerPhase? =
            when (actionMasked) {
                MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN ->
                        PointerPhase.ContactStart
                MotionEvent.ACTION_MOVE -> PointerPhase.ContactMove
                MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP, MotionEvent.ACTION_CANCEL ->
                        PointerPhase.ContactEnd
                MotionEvent.ACTION_BUTTON_PRESS -> PointerPhase.ButtonPress
                MotionEvent.ACTION_BUTTON_RELEASE -> PointerPhase.ButtonRelease
                else -> null
            }

    /**
     * Maps a raw tool-type constant (e.g. [MotionEvent.TOOL_TYPE_STYLUS]) to a [PointerToolKind].
     *
     * Exposed as `internal` for JVM unit testing.
     */
    internal fun toolKindFromInt(toolType: Int): PointerToolKind =
            when (toolType) {
                MotionEvent.TOOL_TYPE_STYLUS -> PointerToolKind.Pen
                MotionEvent.TOOL_TYPE_ERASER -> PointerToolKind.Eraser
                MotionEvent.TOOL_TYPE_MOUSE -> PointerToolKind.Mouse
                MotionEvent.TOOL_TYPE_FINGER -> PointerToolKind.Finger
                else -> PointerToolKind.None
            }

    internal fun toolKindFromMotionEvent(event: MotionEvent, pointerIndex: Int): PointerToolKind =
            toolKindFromInt(event.getToolType(pointerIndex))

    internal fun deviceKindFromToolKind(toolKind: PointerToolKind): PointerDeviceKind =
            when (toolKind) {
                PointerToolKind.Pen -> PointerDeviceKind.Stylus
                PointerToolKind.Eraser -> PointerDeviceKind.Eraser
                PointerToolKind.Mouse -> PointerDeviceKind.Mouse
                PointerToolKind.Finger -> PointerDeviceKind.Touch
                PointerToolKind.None -> PointerDeviceKind.Unknown
            }

    /**
     * Converts a raw [MotionEvent.buttonState] integer to the protocol button mask.
     *
     * Bit layout: Bit 0 = primary (tip / left button) Bit 1 = barrel primary (BTN_STYLUS) Bit 2 =
     * barrel secondary (BTN_STYLUS2) Bit 3 = secondary (right) button
     *
     * Exposed as `internal` so JVM unit tests can validate bitmask without mocking [MotionEvent].
     */
    internal fun buttonMaskFromState(buttonState: Int): Int {
        var mask = 0
        if (buttonState and MotionEvent.BUTTON_PRIMARY != 0) mask = mask or 0x01
        if (buttonState and MotionEvent.BUTTON_STYLUS_PRIMARY != 0) mask = mask or 0x02
        if (buttonState and MotionEvent.BUTTON_STYLUS_SECONDARY != 0) mask = mask or 0x04
        if (buttonState and MotionEvent.BUTTON_SECONDARY != 0) mask = mask or 0x08
        return mask
    }

    internal fun buildButtonMask(event: MotionEvent): Int = buttonMaskFromState(event.buttonState)
}

// ── MotionEvent extension helpers ─────────────────────────────────────────────

/** Returns the value of [axis] for [pointerIndex] if the device reports it, else null. */
private fun MotionEvent.getAxisValueIfPresent(axis: Int, pointerIndex: Int = 0): Float? {
    val v = getAxisValue(axis, pointerIndex)
    return if (v != 0f) v else null
}

/** Historical variant — returns the value if non-zero, else null. */
private fun MotionEvent.getHistoricalAxisValueIfPresent(
        axis: Int,
        pointerIndex: Int,
        historyPos: Int,
): Float? {
    val v = getHistoricalAxisValue(axis, pointerIndex, historyPos)
    return if (v != 0f) v else null
}
