package com.clindsay94.remex.security

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.mockito.kotlin.doReturn
import org.mockito.kotlin.mock

/**
 * The phone half of the PIN auto-fetch gate (RemEx-fd7e). The host no longer relays the live
 * pairing PIN to a loopback caller, so the phone must not ask for it over loopback either: the two
 * sides must agree (docs/REGRESSION-GUARDS.md, "TransportTrust - PIN auto-fetch gate"), or the
 * auto-fill quietly fails on one end. The Tailscale path is asserted alongside so the guard cannot
 * be over-tightened into refusing the one transport that legitimately still auto-fills.
 */
class TransportTrustPinAutoFetchTest {

    private fun contextWithVpn(active: Boolean): Context {
        val network = mock<Network>()
        val caps = mock<NetworkCapabilities> {
            on { hasTransport(NetworkCapabilities.TRANSPORT_VPN) } doReturn active
        }
        val cm = mock<ConnectivityManager> {
            on { activeNetwork } doReturn network
            on { getNetworkCapabilities(network) } doReturn caps
        }
        return mock {
            on { getSystemService(Context.CONNECTIVITY_SERVICE) } doReturn cm
        }
    }

    @Test
    fun `loopback never auto-fetches the pin, with or without a tunnel`() {
        for (vpn in listOf(false, true)) {
            val context = contextWithVpn(vpn)
            for (host in listOf("127.0.0.1", "127.0.0.2", "localhost", "LOCALHOST", "::1")) {
                assertFalse("$host (vpn=$vpn) must not auto-fetch", TransportTrust.canAutoFetchPin(context, host))
            }
        }
    }

    @Test
    fun `loopback stays exempt from LAN permission prompts`() {
        // Dropping loopback from the PIN gate must not drag it back into the LAN-permission path;
        // that is a separate question with a separate answer.
        assertFalse(TransportTrust.requiresLocalNetworkAccess("127.0.0.1"))
        assertFalse(TransportTrust.requiresLocalNetworkAccess("localhost"))
    }

    @Test
    fun `tailscale with a live tunnel still auto-fetches`() {
        val context = contextWithVpn(true)
        assertTrue(TransportTrust.canAutoFetchPin(context, "100.64.0.5"))
        assertTrue(TransportTrust.canAutoFetchPin(context, "fd7a:115c:a1e0::5"))
        assertTrue(TransportTrust.canAutoFetchPin(context, "desktop.tailnet-abc.ts.net"))
    }

    @Test
    fun `tailscale without a live tunnel does not`() {
        val context = contextWithVpn(false)
        assertFalse(TransportTrust.canAutoFetchPin(context, "100.64.0.5"))
        assertFalse(TransportTrust.canAutoFetchPin(context, "desktop.tailnet-abc.ts.net"))
    }

    @Test
    fun `plain lan never auto-fetches`() {
        assertFalse(TransportTrust.canAutoFetchPin(contextWithVpn(true), "192.168.1.20"))
    }
}
