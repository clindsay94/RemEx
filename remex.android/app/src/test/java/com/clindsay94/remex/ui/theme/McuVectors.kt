package com.clindsay94.remex.ui.theme

import android.annotation.SuppressLint
import com.google.android.material.color.utilities.DynamicColor
import com.google.android.material.color.utilities.DynamicScheme
import com.google.android.material.color.utilities.Hct
import com.google.android.material.color.utilities.MaterialDynamicColors
import com.google.android.material.color.utilities.SchemeContent
import com.google.android.material.color.utilities.SchemeExpressive
import com.google.android.material.color.utilities.SchemeFidelity
import com.google.android.material.color.utilities.SchemeFruitSalad
import com.google.android.material.color.utilities.SchemeMonochrome
import com.google.android.material.color.utilities.SchemeNeutral
import com.google.android.material.color.utilities.SchemeRainbow
import com.google.android.material.color.utilities.SchemeTonalSpot
import com.google.android.material.color.utilities.SchemeVibrant
import java.util.Locale

/**
 * The parity oracle (RemEx-4kv0g.6, spec § 5). Everything here calls GOOGLE'S classes directly —
 * never Theme.kt — so the file describes the library, and Theme.kt is measured against it.
 */
@SuppressLint("RestrictedApi") // Material colour science, no public equivalent - see lint.xml (RemEx-cljx)
object McuVectors {
    val SEEDS = listOf("#6750A4", "#386A20", "#B3261E", "#0061A4", "#7D5260", "#FFFFFF", "#000000", "#808080")
    val VARIANTS = listOf("tonal_spot", "vibrant", "expressive", "neutral", "monochrome", "fidelity", "content", "rainbow", "fruit_salad")
    val CONTRASTS = listOf(-1.0, -0.5, 0.0, 0.5, 1.0)
    val COMMITTED_COPIES = listOf(
        "remex.android/app/src/test/resources/mcu-vectors.json",
        "remex.core.tests/Fixtures/mcu-vectors.json",
    )

    /**
     * MaterialDynamicColors' 63 roles, in its declaration order. The PC's MaterialRoles.RoleNames mirrors this list.
     * Every role's rendered ARGB is always FF alpha EXCEPT `controlHighlight`, a fixed-opacity ripple/overlay token
     * by Google's own definition (#1F000000 light, #33FFFFFF dark) — kept in the grid with its real alpha, not FF.
     */
    val ROLES: List<Pair<String, (MaterialDynamicColors) -> DynamicColor>> = listOf(
        "primaryPaletteKeyColor" to { it.primaryPaletteKeyColor() },
        "secondaryPaletteKeyColor" to { it.secondaryPaletteKeyColor() },
        "tertiaryPaletteKeyColor" to { it.tertiaryPaletteKeyColor() },
        "neutralPaletteKeyColor" to { it.neutralPaletteKeyColor() },
        "neutralVariantPaletteKeyColor" to { it.neutralVariantPaletteKeyColor() },
        "background" to { it.background() },
        "onBackground" to { it.onBackground() },
        "surface" to { it.surface() },
        "surfaceDim" to { it.surfaceDim() },
        "surfaceBright" to { it.surfaceBright() },
        "surfaceContainerLowest" to { it.surfaceContainerLowest() },
        "surfaceContainerLow" to { it.surfaceContainerLow() },
        "surfaceContainer" to { it.surfaceContainer() },
        "surfaceContainerHigh" to { it.surfaceContainerHigh() },
        "surfaceContainerHighest" to { it.surfaceContainerHighest() },
        "onSurface" to { it.onSurface() },
        "surfaceVariant" to { it.surfaceVariant() },
        "onSurfaceVariant" to { it.onSurfaceVariant() },
        "inverseSurface" to { it.inverseSurface() },
        "inverseOnSurface" to { it.inverseOnSurface() },
        "outline" to { it.outline() },
        "outlineVariant" to { it.outlineVariant() },
        "shadow" to { it.shadow() },
        "scrim" to { it.scrim() },
        "surfaceTint" to { it.surfaceTint() },
        "primary" to { it.primary() },
        "onPrimary" to { it.onPrimary() },
        "primaryContainer" to { it.primaryContainer() },
        "onPrimaryContainer" to { it.onPrimaryContainer() },
        "inversePrimary" to { it.inversePrimary() },
        "secondary" to { it.secondary() },
        "onSecondary" to { it.onSecondary() },
        "secondaryContainer" to { it.secondaryContainer() },
        "onSecondaryContainer" to { it.onSecondaryContainer() },
        "tertiary" to { it.tertiary() },
        "onTertiary" to { it.onTertiary() },
        "tertiaryContainer" to { it.tertiaryContainer() },
        "onTertiaryContainer" to { it.onTertiaryContainer() },
        "error" to { it.error() },
        "onError" to { it.onError() },
        "errorContainer" to { it.errorContainer() },
        "onErrorContainer" to { it.onErrorContainer() },
        "primaryFixed" to { it.primaryFixed() },
        "primaryFixedDim" to { it.primaryFixedDim() },
        "onPrimaryFixed" to { it.onPrimaryFixed() },
        "onPrimaryFixedVariant" to { it.onPrimaryFixedVariant() },
        "secondaryFixed" to { it.secondaryFixed() },
        "secondaryFixedDim" to { it.secondaryFixedDim() },
        "onSecondaryFixed" to { it.onSecondaryFixed() },
        "onSecondaryFixedVariant" to { it.onSecondaryFixedVariant() },
        "tertiaryFixed" to { it.tertiaryFixed() },
        "tertiaryFixedDim" to { it.tertiaryFixedDim() },
        "onTertiaryFixed" to { it.onTertiaryFixed() },
        "onTertiaryFixedVariant" to { it.onTertiaryFixedVariant() },
        "controlActivated" to { it.controlActivated() },
        "controlNormal" to { it.controlNormal() },
        "controlHighlight" to { it.controlHighlight() },
        "textPrimaryInverse" to { it.textPrimaryInverse() },
        "textSecondaryAndTertiaryInverse" to { it.textSecondaryAndTertiaryInverse() },
        "textPrimaryInverseDisableOnly" to { it.textPrimaryInverseDisableOnly() },
        "textSecondaryAndTertiaryInverseDisabled" to { it.textSecondaryAndTertiaryInverseDisabled() },
        "textHintInverse" to { it.textHintInverse() },
    )

    fun argbOf(hex: String): Int = ("FF" + hex.removePrefix("#")).toLong(16).toInt()

    fun schemeFor(seedHex: String, variant: String, dark: Boolean, contrast: Double): DynamicScheme {
        val hct = Hct.fromInt(argbOf(seedHex))
        return when (variant) {
            "tonal_spot" -> SchemeTonalSpot(hct, dark, contrast)
            "vibrant" -> SchemeVibrant(hct, dark, contrast)
            "expressive" -> SchemeExpressive(hct, dark, contrast)
            "neutral" -> SchemeNeutral(hct, dark, contrast)
            "monochrome" -> SchemeMonochrome(hct, dark, contrast)
            "fidelity" -> SchemeFidelity(hct, dark, contrast)
            "content" -> SchemeContent(hct, dark, contrast)
            "rainbow" -> SchemeRainbow(hct, dark, contrast)
            "fruit_salad" -> SchemeFruitSalad(hct, dark, contrast)
            else -> error("not a variant this grid knows: $variant")
        }
    }

    /** Deterministic by construction: hand-built text, fixed order, fixed formats. No JSONObject (HashMap order). */
    fun render(): String {
        val m3 = MaterialDynamicColors()
        val sb = StringBuilder()
        sb.append("{\n")
        sb.append("  \"generator\": \"com.google.android.material:material:1.14.0 MaterialDynamicColors, written by GenerateMcuVectorsTest\",\n")
        sb.append("  \"roles\": [").append(ROLES.joinToString(", ") { "\"${it.first}\"" }).append("],\n")
        sb.append("  \"seeds\": [").append(SEEDS.joinToString(", ") { "\"$it\"" }).append("],\n")
        sb.append("  \"variants\": [").append(VARIANTS.joinToString(", ") { "\"$it\"" }).append("],\n")
        sb.append("  \"contrasts\": [").append(CONTRASTS.joinToString(", ")).append("],\n")
        sb.append("  \"vectors\": [\n")
        var first = true
        for (seed in SEEDS) for (variant in VARIANTS) for (dark in listOf(false, true)) for (contrast in CONTRASTS) {
            val scheme = schemeFor(seed, variant, dark, contrast)
            val argbs = ROLES.joinToString(", ") { "\"#%08X\"".format(Locale.ROOT, it.second(m3).getArgb(scheme)) }
            if (!first) sb.append(",\n")
            first = false
            sb.append("    {\"seed\": \"$seed\", \"variant\": \"$variant\", \"dark\": $dark, \"contrast\": $contrast, \"argb\": [$argbs]}")
        }
        sb.append("\n  ]\n}\n")
        return sb.toString()
    }

    fun resourceText(): String =
        McuVectors::class.java.classLoader!!.getResourceAsStream("mcu-vectors.json")!!.readBytes().toString(Charsets.UTF_8)
}
