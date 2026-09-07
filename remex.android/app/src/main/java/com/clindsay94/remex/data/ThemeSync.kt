package com.clindsay94.remex.data

import org.json.JSONObject

/**
 * The phone's palette at one instant, as [SettingsManager.PersonalizationPreferences] carries it —
 * only the fields the `theme_sync` wire message needs (RemEx-y06a0.1, blocker of RemEx-sudp8).
 *
 * No Android types, so this and everything in [ThemeSync] below are JVM-testable without
 * Robolectric, matching [MediaSeekReconciler]'s split.
 */
data class ThemeSnapshot(
        val themeMode: String,
        val themePalette: String,
        val themeStyle: String,
        val themeSeedColor: String,
        val themeSeedChroma: Float,
        val themeContrast: Float,
        val dynamicColor: Boolean
)

/** The slice of [SettingsManager.PersonalizationPreferences] that `theme_sync` cares about. */
fun SettingsManager.PersonalizationPreferences.toThemeSnapshot(): ThemeSnapshot =
        ThemeSnapshot(
                themeMode = themeMode,
                themePalette = themePalette,
                themeStyle = themeStyle,
                themeSeedColor = themeSeedColor,
                themeSeedChroma = themeSeedChroma,
                themeContrast = themeContrast,
                dynamicColor = dynamicColor
        )

/**
 * Builds the `theme_sync` envelope the phone sends over the existing authenticated control
 * channel (RemEx-y06a0.1) — the client-to-host sibling of `file_consent_response` and
 * `media_seek`: `{"type": "theme_sync", "themeSync": {...}}`, via
 * [com.clindsay94.remex.RemexCoreClient.SendMessage], the same JSONObject-then-`.toString()` shape
 * [com.clindsay94.remex.service.FileConsentManager] uses for `file_consent_response`.
 *
 * No `protocolVersion` field, matching `file_transfer_end` and every other purely-additive
 * client-to-host type here: the wire contract (RemEx-y06a0.1 / RemEx-sudp8) calls this an optional
 * addition with no protocol bump, and an older host simply does not recognise the type.
 */
object ThemeSync {

        const val MessageType = "theme_sync"

        /**
         * Whether the phone is actually painting from the wallpaper-derived dynamic scheme right
         * now, per [com.clindsay94.remex.ui.theme.RemExTheme]'s own branch order: a custom palette
         * always wins over the dynamic-color toggle, so "dynamic" is never true while
         * [ThemeSnapshot.themePalette] is `"custom"` even if [ThemeSnapshot.dynamicColor] is still
         * left on from a previous choice.
         */
        fun isDynamicActive(snapshot: ThemeSnapshot): Boolean =
                snapshot.themePalette != "custom" && snapshot.dynamicColor

        /**
         * [resolvedSeedHex] must already be `#RRGGBB` — the caller resolves it (dynamic scheme
         * primary, or the stored seed as a fallback) via [ThemeSyncSeedResolver] before calling
         * this. [snapshot]'s `style` and `mode` travel VERBATIM, exactly as
         * [SettingsManager.THEME_STYLE_KEY] / [SettingsManager.THEME_MODE_KEY] store them
         * (`tonal_spot`, `system`, …) — this function does not translate them, so an unrecognised
         * value is a PC-side mapping decision, not one made here.
         */
        fun buildEnvelope(snapshot: ThemeSnapshot, resolvedSeedHex: String, sentAtUnixMs: Long): String {
                val payload =
                        JSONObject()
                                .put("seed", resolvedSeedHex)
                                .put("style", snapshot.themeStyle)
                                .put("mode", snapshot.themeMode)
                                .put("contrast", snapshot.themeContrast.toDouble())
                                .put("dynamic", isDynamicActive(snapshot))
                                .put("sentAtUnixMs", sentAtUnixMs)
                return JSONObject().put("type", MessageType).put("themeSync", payload).toString()
        }

        /** `#RRGGBB`, upper-case, from an ARGB color int — alpha dropped, never sent over the wire. */
        fun toHexRgb(argb: Int): String =
                String.format(java.util.Locale.ROOT, "#%06X", argb and 0xFFFFFF)
}
