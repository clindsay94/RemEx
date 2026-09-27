package com.clindsay94.remex.data

/**
 * The splash styles the app can play, and the one-time move of every existing user onto the new
 * default (RemEx-8g6n0).
 *
 * Live Handshake is the RemEx 3.0 default. Connor decided (2026-09-26, small user base) that
 * everyone upgrading to 3.0 is moved to it ONCE, whatever style they had stored — not only those
 * on the previous default, which is where the PC's schema-4 precedent drew the line
 * (docs/CHANGELOG.md, RemEx-8twk0). The move is marked by a flag; a style picked after it (every
 * personalization save sets the flag too) sticks, including going back to an old favourite.
 *
 * Pure so the rule is provable off-device; [SettingsManager] applies it on read (so the very first
 * launch after the update already plays the new default, with no race against the write) and
 * persists it via [SettingsManager.migrateSplashStyleDefault].
 */
object SplashStyles {
    const val LiveHandshake = "LiveHandshake"
    const val RemexCommand = "RemexCommand"
    const val CosmicZoom = "CosmicZoom"
    const val Pong = "Pong"

    const val Default = LiveHandshake

    /** Picker order: the default first. */
    val All: List<String> = listOf(LiveHandshake, RemexCommand, CosmicZoom, Pong)

    /** The style that plays for a [stored] value, given whether the one-time move has run. */
    fun effective(stored: String?, migrated: Boolean): String = when {
        stored.isNullOrBlank() -> Default
        !migrated -> Default
        else -> stored
    }

    /** The value the one-time move writes, or null when [stored] must be left alone. */
    fun migratedValue(stored: String?, migrated: Boolean): String? =
        if (!migrated && !stored.isNullOrBlank() && stored != Default) Default else null
}
