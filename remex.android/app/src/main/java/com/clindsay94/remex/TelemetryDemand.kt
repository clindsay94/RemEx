package com.clindsay94.remex

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * Who on screen is reading the PC's live telemetry right now (Leanness K8).
 *
 * The host pushes a full sensor envelope every second (~74 KB/s) and samples to fill it. Pausing it
 * only in the background left it streaming whenever the app was open, even on a tab that never reads
 * it. Each surface that does read it takes a lease under its own key while it is visible:
 * - the Sensors canvas,
 * - phone Home while it shows pinned sensors (or its pin sheet is open),
 * - the Routines editor and templates, whose sensor pickers list the PC's sensors.
 *
 * A placed hardware widget is the fourth reader, but it is a property of the home screen rather than
 * of anything composed here, so [TelemetryBackgroundGate.reconcile] takes it as its own input.
 *
 * Keyed rather than counted: a doubled release, or a release whose acquire never ran, cannot drive a
 * count negative and leave the stream paused (or running) for the rest of the process. Plain Kotlin
 * so the counting is unit-testable; [TelemetryDemand] is the process-wide instance.
 */
internal class TelemetryLeases {
    private val keys = mutableSetOf<String>()
    private val _wanted = MutableStateFlow(false)

    /** True while at least one lease is held. */
    val wanted: StateFlow<Boolean> = _wanted.asStateFlow()

    @Synchronized
    fun acquire(key: String) {
        keys += key
        _wanted.value = keys.isNotEmpty()
    }

    @Synchronized
    fun release(key: String) {
        keys -= key
        _wanted.value = keys.isNotEmpty()
    }

    @get:Synchronized
    val holders: Set<String> get() = keys.toSet()
}

/** The process-wide [TelemetryLeases] and the lease keys its holders use. */
internal object TelemetryDemand {
    val leases = TelemetryLeases()

    const val SENSORS_CANVAS = "sensors_canvas"
    const val HOME_PINNED = "home_pinned"
    const val HOME_PIN_SHEET = "home_pin_sheet"
    const val ROUTINE_EDITOR = "routine_editor"
    const val ROUTINE_TEMPLATES = "routine_templates"
}
