package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.RemoteDesktopCapabilityState
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the assumption the media row makes before the host has said anything (RemEx-hulc).
 *
 * THE DEFECT THIS EXISTS FOR, caught in review. `RemoteControlViewModel.supportsInputSimulation`
 * originally seeded its `stateIn` with a hardcoded `false` while the send gate it claimed to mirror
 * reads `RemoteDesktopCapabilityState()`, whose default is `true`. `hostCapabilities` is a
 * `replay = 1` shared flow that does not emit until the first `host_info` arrives, so until then the
 * gate was OPEN while the UI told the user "This PC is not set up to accept key presses" — a claim
 * about their hardware, made before the phone had heard from it.
 *
 * That state is not a flash. It persists for the whole of a cold launch with the PC asleep, which is
 * exactly when this screen gets opened, because Wake PC lives on it.
 *
 * The view model now seeds from this default rather than a literal, so the two cannot disagree. This
 * test guards the other half: flipping the default to `false` would disable the media row on every
 * launch, and the failure would look like a feature that simply does not work rather than like a
 * changed default.
 */
class MediaRowCapabilityDefaultTest {

    @Test
    fun `an unheard-from host is assumed to accept key presses`() {
        // Absence means an OLDER host that should keep working - the opposite of the parse-failure
        // rule, where knowing nothing means refuse. Documented at the declaration and relied on by
        // both the send gate and, now, the media row's enabled state.
        assertTrue(
                "the media row seeds its enabled state from this default; flipping it would " +
                        "disable the row until the first host_info arrives",
                RemoteDesktopCapabilityState().supportsInputSimulation
        )
    }
}
