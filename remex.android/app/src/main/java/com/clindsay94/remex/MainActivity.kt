package com.clindsay94.remex

import android.content.res.Configuration
import android.os.Bundle
import android.provider.Settings
import android.view.ViewGroup
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.core.splashscreen.SplashScreenViewProvider
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clindsay94.remex.data.SettingsManager
import com.clindsay94.remex.data.toThemeSnapshot
import com.clindsay94.remex.ui.components.FileConsentDialogHost
import com.clindsay94.remex.ui.screens.PersonalizationViewModel
import com.clindsay94.remex.ui.navigation.AppNavigation
import com.clindsay94.remex.ui.theme.RemExTheme
import com.clindsay94.remex.ui.theme.SplashExitTransition
import com.clindsay94.remex.ui.theme.SplashPaletteResolver
import com.clindsay94.remex.ui.theme.buildSplashScheme
import com.clindsay94.remex.ui.theme.splashExitTransitionFor
import com.clindsay94.remex.widget.WidgetDataCache
import androidx.core.splashscreen.SplashScreen.Companion.installSplashScreen
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch

/** M3-ish emphasized-decelerate "short" duration; the exit phase replaces (not adds to) the
 * platform's own default splash-icon fade, so this is not extra time on top of a normal launch. */
private const val SplashExitCrossfadeMs = 220L

class MainActivity : ComponentActivity() {

    // Same ViewModelStoreOwner (this Activity) as the `viewModel()` call inside setContent below,
    // so this is the SAME instance, not a second DataStore subscription — obtained early only to
    // start its WhileSubscribed(5000) collection before the splash's exit listener needs a value.
    private val splashPersonalizationViewModel: PersonalizationViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        val splashScreen = installSplashScreen()
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        RemexClientManager.initialize(this)
        WidgetDataCache.startCaching(this)

        // The system splash is painted from the manifest theme before this Activity exists, so it
        // cannot read the stored seed (RemEx-alwfa.1). Keep it on screen only until the first real
        // personalization value lands, then recolour the hand-off to content from that seed.
        var resolvedPrefs: SettingsManager.PersonalizationPreferences? = null
        splashScreen.setKeepOnScreenCondition { resolvedPrefs == null }
        lifecycleScope.launch {
            resolvedPrefs = splashPersonalizationViewModel.personalization.filterNotNull().first()
        }
        splashScreen.setOnExitAnimationListener { provider -> paintSeedSplashExit(provider, resolvedPrefs) }

        setContent {
            val personalizationViewModel: PersonalizationViewModel = viewModel()
            val personalization by personalizationViewModel.personalization.collectAsStateWithLifecycle()
            
            val prefs = personalization
            if (prefs != null) {
                RemExTheme(
                    themeMode = prefs.themeMode,
                    themePalette = prefs.themePalette,
                    themeStyle = prefs.themeStyle,
                    themeSeedColor = prefs.themeSeedColor,
                    themeSeedChroma = prefs.themeSeedChroma,
                    themeContrast = prefs.themeContrast,
                    fontFamilyKey = prefs.fontFamily,
                    fontScale = prefs.fontScale,
                    dynamicColor = prefs.dynamicColor
                ) {
                    AppNavigation()
                    // App-root overlay: mirrors an active file-sharing consent prompt as a dialog
                    // while the app is foregrounded (the notification is the background channel).
                    FileConsentDialogHost()
                }
            } else {
                // Fallback to default theme until prefs are loaded
                RemExTheme {
                    AppNavigation()
                    FileConsentDialogHost()
                }
            }
        }
    }

    /**
     * The seed-coloured exit phase (RemEx-alwfa.1): overlays a [SplashExitView] painted from the
     * device's actual resolved theme, then crossfades the static system splash out from under it
     * and this view out into content — a cut instead, under reduced motion. [prefs] is non-null
     * in the normal case ([onCreate]'s `setKeepOnScreenCondition` waits for it); the fallback
     * below only matters if the exit fires before that first emission somehow lands anyway, so
     * the splash never paints garbage or crashes.
     */
    private fun paintSeedSplashExit(
        provider: SplashScreenViewProvider,
        prefs: SettingsManager.PersonalizationPreferences?
    ) {
        val darkTheme = when (prefs?.themeMode?.lowercase()) {
            "dark" -> true
            "light" -> false
            else -> (resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) ==
                Configuration.UI_MODE_NIGHT_YES
        }

        // Pure palette math only (no I/O): this is the entire "added" cost of the recolour, well
        // under the ~100us it takes to resolve a handful of HCT tones, not the ~100ms budget.
        val t0 = System.nanoTime()
        val scheme = prefs?.let { buildSplashScheme(this, it.toThemeSnapshot(), darkTheme) }
        val palette = SplashPaletteResolver.resolveOrFallback(scheme, darkTheme)
        val resolveMicros = (System.nanoTime() - t0) / 1_000
        android.util.Log.d("SplashExit", "seed palette resolved in ${resolveMicros}us")

        val splashRoot = provider.view
        val exitView = SplashExitView(this, palette).apply {
            layoutParams = ViewGroup.LayoutParams(splashRoot.width, splashRoot.height)
        }
        val decor = window.decorView as ViewGroup
        decor.addView(exitView, decor.indexOfChild(splashRoot) + 1)

        val animatorScale = Settings.Global.getFloat(contentResolver, Settings.Global.ANIMATOR_DURATION_SCALE, 1f)
        when (splashExitTransitionFor(animatorScale)) {
            SplashExitTransition.CUT -> {
                decor.removeView(exitView)
                provider.remove()
            }
            SplashExitTransition.CROSSFADE -> {
                exitView.alpha = 0f
                exitView.animate()
                    .alpha(1f)
                    .setDuration(SplashExitCrossfadeMs)
                    .withEndAction {
                        splashRoot.animate()
                            .alpha(0f)
                            .setDuration(SplashExitCrossfadeMs)
                            .withEndAction {
                                decor.removeView(exitView)
                                provider.remove()
                            }
                            .start()
                    }
                    .start()
            }
        }
    }
}
