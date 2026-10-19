package com.clindsay94.remex.data

import androidx.datastore.preferences.core.preferencesOf
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
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
            listOf("LiveHandshake", "RemexCommand", "CosmicZoom", "Pong", "Random"),
            SplashStyles.All,
        )
        assertEquals(SplashStyles.Playable + "Random", SplashStyles.All)
    }

    @Test
    fun `a concrete style plays as stored`() {
        for (stored in SplashStyles.Playable) {
            assertEquals(stored, SplashStyles.forLaunch(stored, lastPlayed = "Pong"))
            assertEquals(stored, SplashStyles.forLaunch(stored, lastPlayed = null))
        }
    }

    @Test
    fun `Random never repeats the last animation and is always playable`() {
        val rng = kotlin.random.Random(42)
        for (last in SplashStyles.Playable + listOf<String?>(null)) {
            repeat(200) {
                val picked = SplashStyles.forLaunch(SplashStyles.Random, last, rng)
                assertNotEquals(last, picked)
                assertNotEquals(SplashStyles.Random, picked)
                assertTrue(picked, picked in SplashStyles.Playable)
            }
        }
    }

    @Test
    fun `Random survives the one-time move and the last pick is read with the style`() {
        assertEquals("Random", SplashStyles.effective("Random", migrated = true))
        val read = SettingsManager.personalizationFrom(
            preferencesOf(
                SettingsManager.SPLASH_STYLE_KEY to "Random",
                SettingsManager.SPLASH_STYLE_MIGRATED_V3_KEY to true,
                SettingsManager.SPLASH_LAST_RANDOM_KEY to "Pong",
            )
        )
        assertEquals("Random", read.splashStyle)
        assertEquals("Pong", read.lastRandomSplash)
        assertNull(SettingsManager.personalizationFrom(preferencesOf()).lastRandomSplash)
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
