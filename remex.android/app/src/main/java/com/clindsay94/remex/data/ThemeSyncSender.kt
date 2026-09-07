package com.clindsay94.remex.data

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.FlowPreview
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.launch

/** Default per RemEx-y06a0.1's wire contract: "debounce a burst of changes to one message per ~500 ms". */
const val ThemeSyncDebounceMs = 500L

/**
 * Sends `theme_sync` once after a connection is established, and again on every theme-flow
 * change — debounced, never on a timer (RemEx-y06a0.1).
 *
 * Constructor-injected [isConnected], [resolveSeed], [send] and [clock] rather than reaching for
 * [com.clindsay94.remex.RemexClientManager] / [ThemeSyncSeedResolver] / a `Context` /
 * `System.currentTimeMillis()` directly, so this is testable with `runTest` and a fake sender —
 * no real sockets, no Robolectric — exactly as [MediaSeekReconciler] stays JVM-only by taking no
 * Android types. [ThemeSnapshot] carries no coroutine or Android dependency either.
 *
 * A coroutine-based debounce (`Flow.debounce`), NOT a `Timer` or a `delay`-loop: the wire contract
 * is explicit that this must never run on a periodic timer, and `debounce` only ever fires in
 * reaction to an actual change reaching [onThemeChanged].
 */
@OptIn(FlowPreview::class)
class ThemeSyncSender(
        private val scope: CoroutineScope,
        private val isConnected: () -> Boolean,
        private val resolveSeed: (ThemeSnapshot) -> String,
        private val send: (String) -> Unit,
        private val clock: () -> Long = System::currentTimeMillis,
        debounceMs: Long = ThemeSyncDebounceMs
) {
        // A StateFlow, not a SharedFlow — the same shape as PersonalizationViewModel's
        // `_pendingSave` (RemEx-9429). Conflation is exactly "collapse a burst" for free: a rapid
        // run of onThemeChanged calls just keeps overwriting `.value`, and there is no buffer-
        // capacity accounting to get wrong the way there would be with a SharedFlow's tryEmit.
        private val pendingChange = MutableStateFlow<ThemeSnapshot?>(null)

        init {
                scope.launch {
                        pendingChange.filterNotNull().debounce(debounceMs).collectLatest { snapshot ->
                                if (isConnected()) deliver(snapshot)
                        }
                }
        }

        /**
         * Call once the handshake actually completes, with the theme as it stands right now — NOT
         * cached from before the connection existed, so a device that changed its palette while
         * disconnected announces the current one rather than a stale one from the last session.
         *
         * Sent immediately, bypassing the debounce: this is the "next connect sends the latest"
         * half of the contract, not a change to collapse against a burst.
         */
        fun onConnected(snapshot: ThemeSnapshot) {
                if (isConnected()) deliver(snapshot)
        }

        /** Call on every SettingsManager theme-flow emission, connected or not (see class doc). */
        fun onThemeChanged(snapshot: ThemeSnapshot) {
                pendingChange.value = snapshot
        }

        private fun deliver(snapshot: ThemeSnapshot) {
                val seed = resolveSeed(snapshot)
                send(ThemeSync.buildEnvelope(snapshot, seed, clock()))
        }
}
