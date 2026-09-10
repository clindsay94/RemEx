package com.clindsay94.remex

import kotlinx.coroutines.flow.MutableSharedFlow
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the one-shot contract of [RemexClientManager.consumePairingRequest] (RemEx-zlpm).
 *
 * `pairingRequired` is a `replay = 1` SharedFlow, so every new subscriber is handed the last pairing
 * request. That is deliberate — a composition rebuilt by a widget tap still learns pairing is needed
 * — but it means the navigation site re-subscribing, which it now does on every return to the
 * foreground, would otherwise walk the user to the PIN screen every single time the app resumed.
 * `consumePairingRequest()` is what makes that safe, and this test is what stops a future refactor of
 * the pairing path from quietly removing it.
 *
 * The failure mode being guarded is specific and silent: `resetReplayCache()` on a flow with no
 * replay — `_connectionError`, say — is a legal no-op that compiles, passes lint, and reintroduces
 * the repeat-navigation bug with nothing to report it.
 *
 * **On the reflection.** The emit is unreachable from a JVM test by every ordinary route: `connect()`
 * returns early without the `settingsManager` that only `initialize(Context)` supplies, the emit sits
 * behind `RemexCoreClient.isLibraryLoaded` which is false with no `.so`, and it runs on
 * `managerScope`. The alternative was adding a second test-only seam to a class the project marks
 * high-risk, which is a worse trade than eight lines of reflection confined to a test. It is brittle
 * to renaming `_pairingRequired` — but that breaks LOUDLY with `NoSuchFieldException` rather than
 * passing vacuously, which is the acceptable direction. (The repo's no-reflection rule is scoped to
 * `Remex.Core` for NativeAOT and does not reach Kotlin test code.)
 *
 * [RemexClientManager] is a process singleton, so this test deliberately leaves the replay cache
 * EMPTY when it finishes — consuming is the last thing it does.
 */
class PairingRequestConsumptionTest {

    @Suppress("UNCHECKED_CAST")
    private fun pairingRequiredFlow(): MutableSharedFlow<Pair<String, Int>> =
            RemexClientManager::class
                    .java
                    .getDeclaredField("_pairingRequired")
                    .apply { isAccessible = true }
                    .get(RemexClientManager) as MutableSharedFlow<Pair<String, Int>>

    @Test
    fun `consuming clears the retained request so a later subscriber gets nothing`() {
        val flow = pairingRequiredFlow()
        flow.tryEmit("10.0.0.5" to 5005)

        // Positive control. Without this the assertion below would pass just as well against a flow
        // that never retained anything, which is precisely the vacuous test this replaced.
        assertEquals(
                "pairingRequired must retain the request — replay = 1 is what lets a late subscriber see it.",
                1,
                RemexClientManager.pairingRequired.replayCache.size
        )

        RemexClientManager.consumePairingRequest()

        assertTrue(
                "consumePairingRequest must clear the replay cache. If it targets a flow with no " +
                        "replay the call is a legal no-op, and the navigation site starts " +
                        "re-delivering the same pairing request on every foreground.",
                RemexClientManager.pairingRequired.replayCache.isEmpty()
        )
    }

    @Test
    fun `consuming when there is nothing to consume is harmless`() {
        // The navigation site calls this on a path that may run more than once, and
        // onConnectionStateChanged clears the same cache on every successful connect, so the two can
        // and do both fire. Neither ordering may throw.
        RemexClientManager.consumePairingRequest()
        RemexClientManager.consumePairingRequest()

        assertTrue(RemexClientManager.pairingRequired.replayCache.isEmpty())
    }
}
