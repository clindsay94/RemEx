package com.clindsay94.remex.ui.theme

import androidx.compose.material3.ColorScheme
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import org.json.JSONObject
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.Locale

/**
 * Spec § 5: the app's own scheme construction reproduces mcu-vectors.json for every role Compose
 * exposes, so a refactor of Theme.kt cannot drift from the library the PC was ported from.
 */
class ThemeParityTest {
    /** Every ColorScheme role that has an M3 name — 48 — paired with the vector's role name. */
    private fun composeRoles(s: ColorScheme): List<Pair<String, Color>> = listOf(
        "primary" to s.primary, "onPrimary" to s.onPrimary, "primaryContainer" to s.primaryContainer, "onPrimaryContainer" to s.onPrimaryContainer,
        "inversePrimary" to s.inversePrimary, "secondary" to s.secondary, "onSecondary" to s.onSecondary, "secondaryContainer" to s.secondaryContainer,
        "onSecondaryContainer" to s.onSecondaryContainer, "tertiary" to s.tertiary, "onTertiary" to s.onTertiary, "tertiaryContainer" to s.tertiaryContainer,
        "onTertiaryContainer" to s.onTertiaryContainer, "background" to s.background, "onBackground" to s.onBackground, "surface" to s.surface,
        "onSurface" to s.onSurface, "surfaceVariant" to s.surfaceVariant, "onSurfaceVariant" to s.onSurfaceVariant, "surfaceTint" to s.surfaceTint,
        "inverseSurface" to s.inverseSurface, "inverseOnSurface" to s.inverseOnSurface, "error" to s.error, "onError" to s.onError,
        "errorContainer" to s.errorContainer, "onErrorContainer" to s.onErrorContainer, "outline" to s.outline, "outlineVariant" to s.outlineVariant,
        "scrim" to s.scrim, "surfaceBright" to s.surfaceBright, "surfaceDim" to s.surfaceDim, "surfaceContainer" to s.surfaceContainer,
        "surfaceContainerHigh" to s.surfaceContainerHigh, "surfaceContainerHighest" to s.surfaceContainerHighest, "surfaceContainerLow" to s.surfaceContainerLow,
        "surfaceContainerLowest" to s.surfaceContainerLowest, "primaryFixed" to s.primaryFixed, "primaryFixedDim" to s.primaryFixedDim,
        "onPrimaryFixed" to s.onPrimaryFixed, "onPrimaryFixedVariant" to s.onPrimaryFixedVariant, "secondaryFixed" to s.secondaryFixed,
        "secondaryFixedDim" to s.secondaryFixedDim, "onSecondaryFixed" to s.onSecondaryFixed, "onSecondaryFixedVariant" to s.onSecondaryFixedVariant,
        "tertiaryFixed" to s.tertiaryFixed, "tertiaryFixedDim" to s.tertiaryFixedDim, "onTertiaryFixed" to s.onTertiaryFixed,
        "onTertiaryFixedVariant" to s.onTertiaryFixedVariant,
    )

    @Test
    fun `colorSchemeFromSeed reproduces the vector for every Compose role, every tuple`() {
        val doc = JSONObject(McuVectors.resourceText())
        val roles = doc.getJSONArray("roles")
        val index = (0 until roles.length()).associate { roles.getString(it) to it }
        val vectors = doc.getJSONArray("vectors")
        val failures = mutableListOf<String>()
        for (i in 0 until vectors.length()) {
            val v = vectors.getJSONObject(i)
            val scheme = colorSchemeFromSeed(
                Color(McuVectors.argbOf(v.getString("seed"))), v.getBoolean("dark"), v.getString("variant"), v.getDouble("contrast"))
            val argb = v.getJSONArray("argb")
            for ((role, actual) in composeRoles(scheme)) {
                val expected = argb.getString(index.getValue(role))
                val got = "#%08X".format(Locale.ROOT, actual.toArgb())
                if (got != expected) failures += "${v.getString("seed")} ${v.getString("variant")} dark=${v.getBoolean("dark")} contrast=${v.getDouble("contrast")} $role: expected $expected, got $got"
            }
        }
        assertTrue("${failures.size} mismatches, first 20:\n" + failures.take(20).joinToString("\n"), failures.isEmpty())
    }

    @Test
    fun `composeRoles names 48 distinct vector roles`() {
        val names = composeRoles(colorSchemeFromSeed(Color(0xFF6750A4.toInt()), darkTheme = false)).map { it.first }
        assertTrue(names.size == 48 && names.toSet().size == 48)
        val known = McuVectors.ROLES.map { it.first }.toSet()
        assertTrue(names.filterNot { it in known }.toString(), names.all { it in known })
    }
}
