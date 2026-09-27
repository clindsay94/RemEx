package com.clindsay94.remex.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * The Live Handshake default and the one-time move of every upgrading user onto it (RemEx-8g6n0,
 * Connor's 2026-09-26 decision: whatever they had).
 */
class SplashStylesTest {

    @Test
    fun `Live Handshake is the default and listed first`() {
        assertEquals("LiveHandshake", SplashStyles.Default)
        assertEquals(SplashStyles.Default, SplashStyles.All.first())
        assertEquals(SplashStyles.Default, SettingsManager.PersonalizationPreferences().splashStyle)
        assertEquals(
            listOf("LiveHandshake", "RemexCommand", "CosmicZoom", "Pong"),
            SplashStyles.All,
        )
    }

    @Test
    fun `a fresh install plays the new default`() {
        assertEquals(SplashStyles.LiveHandshake, SplashStyles.effective(null, migrated = false))
        assertEquals(SplashStyles.LiveHandshake, SplashStyles.effective("", migrated = true))
        assertNull(SplashStyles.migratedValue(null, migrated = false))
    }

    @Test
    fun `every stored style moves to Live Handshake once, whatever it was`() {
        for (stored in listOf("RemexCommand", "CosmicZoom", "Pong")) {
            assertEquals(stored, SplashStyles.LiveHandshake, SplashStyles.effective(stored, migrated = false))
            assertEquals(stored, SplashStyles.LiveHandshake, SplashStyles.migratedValue(stored, migrated = false))
        }
        // Already on it: nothing to write.
        assertEquals(SplashStyles.LiveHandshake, SplashStyles.effective("LiveHandshake", migrated = false))
        assertNull(SplashStyles.migratedValue("LiveHandshake", migrated = false))
    }

    @Test
    fun `after the move, a picked style sticks`() {
        for (picked in listOf("RemexCommand", "CosmicZoom", "Pong", "LiveHandshake")) {
            assertEquals(picked, SplashStyles.effective(picked, migrated = true))
            assertNull(SplashStyles.migratedValue(picked, migrated = true))
        }
    }
}
