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
import android.util.Log
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.launch

private const val TAG = "SplashExit"

/** M3-ish emphasized-decelerate "short" duration; the exit phase replaces (not adds to) the
 * platform's own default splash-icon fade, so this is not extra time on top of a normal launch. */
private const val SplashExitCrossfadeMs = 220L

/** Budget for the ONE thing the exit phase can genuinely add over the platform's default splash:
 * the wait for the first personalization value. Past this, a brand-default recolour is used
 * instead of holding the splash open indefinitely (RemEx-alwfa.1 review, HIGH-1). */
private const val SplashPersonalizationTimeoutMs = 250L

/** Safety net for [MainActivity.paintSeedSplashExit]'s crossfade: if `withEndAction` never fires
 * (window torn down mid-animation, etc.) the system splash must still come off eventually rather
 * than sitting over content forever (RemEx-alwfa.1 review, HIGH-3). */
private const val SplashExitFallbackRemovalMs = SplashExitCrossfadeMs * 2 + 200

/** What the root theme renders with until DataStore's first personalization emission lands. */
private val DefaultPersonalization = SettingsManager.PersonalizationPreferences()

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
        var awaitFinished = false
        splashScreen.setKeepOnScreenCondition { !awaitFinished }
        lifecycleScope.launch {
            var loggedFailure = false
            resolvedPrefs = awaitFirstOrNullWithTimeout(
                splashPersonalizationViewModel.personalization.filterNotNull(),
                SplashPersonalizationTimeoutMs
            ) { e ->
                loggedFailure = true
                Log.w(TAG, "personalization failed before the splash exit; using the brand default", e)
            }
            if (resolvedPrefs == null && !loggedFailure) {
                Log.w(TAG, "personalization not ready within ${SplashPersonalizationTimeoutMs}ms; using the brand default")
            }
            awaitFinished = true
        }
        splashScreen.setOnExitAnimationListener { provider -> paintSeedSplashExit(provider, resolvedPrefs) }

        setContent {
            val personalizationViewModel: PersonalizationViewModel = viewModel()
            val personalization by personalizationViewModel.personalization.collectAsStateWithLifecycle()

            // Perf audit P3-11: ONE RemExTheme call site for both the pre-load and loaded states.
            // The first composition runs before DataStore's first emission lands (personalization
            // is still null), and the old if/else put AppNavigation under two different call sites,
            // so that first emission tore the whole navigation tree down and rebuilt it from
            // scratch. With a single call site the arrival of real prefs is an ordinary
            // recomposition with new theme values; the tree and its state survive. The defaults
            // (PersonalizationPreferences()) are exactly RemExTheme's own parameter defaults, which
            // is what the old fallback branch rendered.
            val prefs = personalization ?: DefaultPersonalization
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
        // provider.remove() exactly once, from whichever path gets there first (normal end action,
        // the fallback timer, or the catch below) — never zero (splash stuck forever) and never
        // twice (RemEx-alwfa.1 review, HIGH-3).
        val removed = java.util.concurrent.atomic.AtomicBoolean(false)
        fun removeSplashOnce() {
            if (removed.compareAndSet(false, true)) {
                runCatching { provider.remove() }
            }
        }

        // Hoisted so the catch below can also tear down a partially-added exitView (RemEx-alwfa.1
        // review round 2, LOW): a throw AFTER addView must not leave the opaque overlay stuck on
        // top of content just because it removed the system splash successfully.
        var decor: ViewGroup? = null
        var exitView: SplashExitView? = null

        try {
            val darkTheme = when (prefs?.themeMode?.lowercase()) {
                "dark" -> true
                "light" -> false
                else -> (resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) ==
                    Configuration.UI_MODE_NIGHT_YES
            }
            val scheme = prefs?.let { buildSplashScheme(this, it.toThemeSnapshot(), darkTheme) }
            val palette = SplashPaletteResolver.resolveOrFallback(scheme, darkTheme)

            val splashRoot = provider.view
            val view = SplashExitView(this, palette).apply {
                layoutParams = ViewGroup.LayoutParams(splashRoot.width, splashRoot.height)
            }
            exitView = view
            val group = window.decorView as ViewGroup
            decor = group
            group.addView(view, group.indexOfChild(splashRoot) + 1)

            val animatorScale =
                Settings.Global.getFloat(contentResolver, Settings.Global.ANIMATOR_DURATION_SCALE, 1f)
            when (splashExitTransitionFor(animatorScale)) {
                SplashExitTransition.CUT -> {
                    runCatching { group.removeView(view) }
                    removeSplashOnce()
                }
                SplashExitTransition.CROSSFADE -> {
                    val fallback = Runnable {
                        runCatching { group.removeView(view) }
                        removeSplashOnce()
                    }
                    view.postDelayed(fallback, SplashExitFallbackRemovalMs)

                    view.alpha = 0f
                    view.animate()
                        .alpha(1f)
                        .setDuration(SplashExitCrossfadeMs)
                        .withEndAction {
                            splashRoot.animate()
                                .alpha(0f)
                                .setDuration(SplashExitCrossfadeMs)
                                .withEndAction {
                                    view.removeCallbacks(fallback)
                                    runCatching { group.removeView(view) }
                                    removeSplashOnce()
                                }
                                .start()
                        }
                        .start()
                }
            }
        } catch (t: Throwable) {
            // Never leave the system splash stuck over content because painting the recoloured
            // exit phase itself failed (RemEx-alwfa.1 review, HIGH-3) — and if a partially added
            // exitView survived the throw, it must come off too, or an opaque overlay is left
            // sitting on top of content even though the system splash itself was removed
            // (RemEx-alwfa.1 review round 2, LOW).
            Log.w(TAG, "seed splash exit failed; removing the system splash directly", t)
            runCatching { decor?.removeView(exitView) }
            removeSplashOnce()
        }
    }
}
