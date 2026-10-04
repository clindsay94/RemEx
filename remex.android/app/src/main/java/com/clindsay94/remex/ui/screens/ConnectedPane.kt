package com.clindsay94.remex.ui.screens

/** What a screen that needs the PC shows in place of (or around) its content. */
enum class ConnectedPaneState {
    /** No connection: offline banner, and only what works without one stays usable. */
    Disconnected,

    /**
     * Connected, but the PC no longer recognises this phone and refuses everything
     * ([com.clindsay94.remex.RemexClientManager.needsPairing]): the screen shows
     * [NeedsPairingContent] instead of controls that would all fail.
     */
    NeedsPairing,

    /** Connected and recognised. */
    Ready,
}

/**
 * The one rule behind the Desktop tab, Sensors and Commands (3.0 comb, needs-pairing-gaps,
 * commands-no-offline). Processes and Files already followed it on their own; these three ignored
 * the pairing state, and Commands had no offline state at all - Lock tapped offline came back as the
 * native layer's untranslated "Failed: Client is not connected."
 */
object ConnectedPane {
    fun state(isConnected: Boolean, needsPairing: Boolean): ConnectedPaneState =
        when {
            !isConnected -> ConnectedPaneState.Disconnected
            needsPairing -> ConnectedPaneState.NeedsPairing
            else -> ConnectedPaneState.Ready
        }

    /**
     * Whether a Commands card can be used right now. Offline, only Wake: it is the one command that
     * goes to the network rather than the PC, and the one that matters most while the PC is off.
     */
    fun isCommandAvailable(action: String, isConnected: Boolean): Boolean =
        isConnected || action == WAKE_ACTION

    const val WAKE_ACTION = "WakeOnLan"
}
