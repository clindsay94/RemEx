package com.clindsay94.remex

import android.Manifest
import com.clindsay94.remex.ui.screens.ConnectPermissions
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Every connect path asks for the same permissions (3.0 comb, qr-home-no-perms; RemEx-pp4cm.3).
 * The rules used to live in ConnectionScreen alone, so Home's Connect and a scanned QR code skipped
 * them and failed on Android 17 with nothing asked.
 */
class ConnectPermissionsTest {
    private val lan = ConnectPermissions.ACCESS_LOCAL_NETWORK

    @Test
    fun `a lan connect on android 17 asks for local network access`() {
        val perms = ConnectPermissions.forConnect(needsLan = true, sdkInt = 37)
        assertTrue(lan in perms)
        assertTrue(Manifest.permission.NEARBY_WIFI_DEVICES in perms)
        assertTrue(Manifest.permission.POST_NOTIFICATIONS in perms)
    }

    @Test
    fun `a vpn or loopback connect asks for notifications only`() {
        assertEquals(
            listOf(Manifest.permission.POST_NOTIFICATIONS),
            ConnectPermissions.forConnect(needsLan = false, sdkInt = 37))
    }

    @Test
    fun `the local network permission is not asked for before it exists`() {
        assertFalse(lan in ConnectPermissions.forConnect(needsLan = true, sdkInt = 35))
        assertFalse(lan in ConnectPermissions.forDiscovery(sdkInt = 35))
        assertTrue(lan in ConnectPermissions.forDiscovery(sdkInt = 36))
    }

    @Test
    fun `refusing local network access blocks a lan attempt only where it is enforced`() {
        val denied = mapOf(lan to false)
        assertTrue(ConnectPermissions.isLocalNetworkRefused(denied, needsLan = true, sdkInt = 37))
        assertFalse(ConnectPermissions.isLocalNetworkRefused(denied, needsLan = true, sdkInt = 36))
        // A Tailscale connect goes ahead on a phone that declined local-network access.
        assertFalse(ConnectPermissions.isLocalNetworkRefused(denied, needsLan = false, sdkInt = 37))
    }

    @Test
    fun `refusing notifications alone never blocks the connection`() {
        val results = mapOf(Manifest.permission.POST_NOTIFICATIONS to false, lan to true)
        assertFalse(ConnectPermissions.isLocalNetworkRefused(results, needsLan = true, sdkInt = 37))
    }
}
