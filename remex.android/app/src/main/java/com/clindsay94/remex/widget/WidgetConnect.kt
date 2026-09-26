package com.clindsay94.remex.widget

import android.content.Context
import com.clindsay94.remex.R
import com.clindsay94.remex.RemexClientManager
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withTimeoutOrNull

/**
 * How long a widget tap waits for its one-shot connect (live-check A8). Glance delivers the tap
 * through a broadcast held open with goAsync(), which the system kills at about ten seconds; this
 * leaves half of it for the toasts and the hand-off to [sendWidgetCommand].
 */
internal const val WIDGET_CONNECT_TIMEOUT_MS = 5_000L

/**
 * The decision a widget tap waits on while its one-shot connect runs, kept pure so it is testable.
 *
 * Returns true once the host has acked the reconnect proof, false once the attempt is over without a
 * connection, and null while it is still in flight. AUTHENTICATED, NOT CONNECTED: the socket opens
 * before the host has checked the proof, and a command sent in that gap is refused as unpaired with
 * nothing in the log (RemEx-0vpw5).
 */
internal object WidgetConnectWait {
    fun verdict(authenticated: Boolean, connected: Boolean, connecting: Boolean): Boolean? = when {
        authenticated -> true
        !connected && !connecting -> false
        else -> null
    }
}

/**
 * Makes sure a Remote Control or App Launcher tap has a connection to send on (live-check A8).
 *
 * These taps used to toast "not connected" whenever the app was backgrounded or killed, because the
 * background reconnect loop is parked for them ([com.clindsay94.remex.ReconnectGate], perf audit
 * P0-11: only a hardware widget on a lit screen keeps it running). Rather than reopen that loop for
 * every widget - a permanent battery cost for a button pressed now and then - the tap itself asks
 * for ONE attempt to the saved, already-trusted PC and waits a bounded time for it.
 *
 * An already-open connection is used as before, without waiting. A PC that was never paired is not
 * paired here; the attempt ends at once and the caller's "not connected" toast stands.
 */
internal suspend fun ensureWidgetConnection(context: Context): Boolean {
    val manager = RemexClientManager
    // Only an AUTHENTICATED session may short-circuit: a socket that is open but whose proof is not
    // acked yet refuses gated sends silently (RemEx-0vpw5), so that state falls through to the wait.
    if (manager.isAuthenticated.value) return true
    if (!manager.isConnected.value && !manager.startOneShotConnect(context)) return false
    widgetToast(context, context.getString(R.string.widget_toast_connecting))
    return withTimeoutOrNull(WIDGET_CONNECT_TIMEOUT_MS) {
        combine(manager.isAuthenticated, manager.isConnected, manager.isConnecting) { auth, connected, connecting ->
            WidgetConnectWait.verdict(auth, connected, connecting)
        }.filterNotNull().first()
    } ?: false
}
