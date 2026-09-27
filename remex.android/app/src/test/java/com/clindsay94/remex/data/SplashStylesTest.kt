package com.clindsay94.remex.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * The Live Handshake default and the one-time move off the previous default (RemEx-8g6n0),
 * mirroring the PC's schema-4 RemexCommand -> Cosmic Zoom precedent.
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
    fun `the previous default moves to Live Handshake exactly once`() {
        assertEquals(SplashStyles.LiveHandshake, SplashStyles.effective("RemexCommand", migrated = false))
        assertEquals(SplashStyles.LiveHandshake, SplashStyles.migratedValue("RemexCommand", migrated = false))
        // After the move, choosing the old style again sticks.
        assertEquals(SplashStyles.RemexCommand, SplashStyles.effective("RemexCommand", migrated = true))
        assertNull(SplashStyles.migratedValue("RemexCommand", migrated = true))
    }

    @Test
    fun `a style someone picked is never migrated`() {
        for (picked in listOf("CosmicZoom", "Pong", "LiveHandshake")) {
            assertEquals(picked, SplashStyles.effective(picked, migrated = false))
            assertEquals(picked, SplashStyles.effective(picked, migrated = true))
            assertNull(SplashStyles.migratedValue(picked, migrated = false))
        }
    }
}
