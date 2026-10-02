package com.clindsay94.remex.ui.screens

import androidx.compose.runtime.Composable
import androidx.lifecycle.compose.LifecycleStartEffect
import com.clindsay94.remex.TelemetryDemand

/**
 * Holds the [TelemetryDemand] lease named [key] while [active] and this composable is started
 * (Leanness K8). A lifecycle observer rather than a plain DisposableEffect so the lease is also let go
 * on ON_STOP, when Compose has paused the frame clock and no recomposition would run to release it.
 */
@Composable
internal fun TelemetryLeaseEffect(key: String, active: Boolean = true) {
    LifecycleStartEffect(key, active) {
        if (active) TelemetryDemand.leases.acquire(key) else TelemetryDemand.leases.release(key)
        onStopOrDispose { TelemetryDemand.leases.release(key) }
    }
}
