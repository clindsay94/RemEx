package com.clindsay94.remex.ui.screens

import android.app.Activity
import android.content.pm.ActivityInfo
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.tooling.preview.Preview
import com.clindsay94.remex.ui.theme.RemExTheme

/**
 * Splash orchestrator.
 *
 * Owns the shared screen chrome that is common to every variant: the portrait-orientation
 * lock and the full-screen tap-to-skip gesture. It routes to a self-contained per-variant
 * composable via [splashStyle]; each variant owns its own frame loop, animation state, and
 * exit fade.
 *
 * The variant hands its skip/finish flow off the orchestrator's [skipRequested] flag: a tap
 * flips the flag, the variant runs the original fade-then-finish, then calls back to reset it.
 *
 * NOTE (Task 2, behavior-identical split): the version-label + "tap to skip" hint are kept
 * drawn *inside* each variant's Canvas — exactly where they were in the monolith — because
 * they are driven by that variant's `elapsed` clock, its `isSkipping` flag, and its
 * `skipAlpha` fade. Hoisting them into a standalone orchestrator overlay would require a
 * second clock and would drop them out of the skip fade, i.e. a visible behavior change.
 * The later "skip hint → top-right" task can lift them out then.
 */
@Composable
fun SplashScreen(splashStyle: String = "RemexCommand", onFinished: () -> Unit) {
    val context = LocalContext.current
    DisposableEffect(Unit) {
        val activity = context as? Activity ?: return@DisposableEffect onDispose {}
        val originalOrientation = activity.requestedOrientation
        activity.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        onDispose {
            activity.requestedOrientation = originalOrientation
        }
    }

    var skipRequested by remember { mutableStateOf(false) }
    Box(
        Modifier
            .fillMaxSize()
            .pointerInput(Unit) { detectTapGestures { skipRequested = true } }
    ) {
        when (splashStyle) {
            "CosmicZoom" -> SplashCosmicZoom(onFinished, skipRequested) { skipRequested = false }
            else -> SplashRemexCommand(onFinished, skipRequested) { skipRequested = false }
        }
    }
}

@Preview(showBackground = true)
@Composable
private fun SplashScreenPreview() {
    RemExTheme {
        SplashScreen(onFinished = {})
    }
}
