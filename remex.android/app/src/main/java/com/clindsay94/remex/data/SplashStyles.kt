package com.clindsay94.remex.data

/**
 * The splash styles the app can play, and the one-time move of existing users onto the new
 * default (RemEx-8g6n0).
 *
 * Live Handshake is the RemEx 3.0 default. Users still on the PREVIOUS default ([PreviousDefault],
 * "RemexCommand") are moved to it once; anyone who picked another style keeps it. This mirrors the
 * PC's schema-4 precedent (docs/CHANGELOG.md, RemEx-8twk0: "a stored RemexCommand splash to
 * Cosmic Zoom, once"). Someone who deliberately chose RemexCommand is indistinguishable in storage
 * from someone who never touched the picker (every personalization save writes the style), so they
 * move too — exactly once, after which choosing it again sticks.
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
    const val PreviousDefault = RemexCommand

    /** Picker order: the default first. */
    val All: List<String> = listOf(LiveHandshake, RemexCommand, CosmicZoom, Pong)

    /** The style that plays for a [stored] value, given whether the one-time move has run. */
    fun effective(stored: String?, migrated: Boolean): String = when {
        stored.isNullOrBlank() -> Default
        !migrated && stored == PreviousDefault -> Default
        else -> stored
    }

    /** The value the one-time move writes, or null when [stored] must be left alone. */
    fun migratedValue(stored: String?, migrated: Boolean): String? =
        if (!migrated && stored == PreviousDefault) Default else null
}
