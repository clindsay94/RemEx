package com.clindsay94.remex.ui.components

import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.asPaddingValues
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.runtime.Composable
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/**
 * Bottom content padding a scrolling container needs so its last item can scroll fully clear of
 * whatever floats over its bottom edge: a floating toolbar, the docked mini-player, a FAB.
 * (RemEx-wqo7a.1.)
 *
 * [floatingFootprint] is how far the floating stack reaches up from its anchor: its own height
 * plus any padding that lifts it. [navBarInset] is the system navigation bar's height, and it is
 * a separate argument on purpose: every floating element in this app positions itself with
 * `navigationBarsPadding()`, which lifts it by that inset on top of its footprint. A padding that
 * left the inset out stopped short by exactly the bar's height (48.dp on three-button navigation),
 * and that is how the last command tile, the last launcher tile and a dashboard card's resize
 * handle ended up underneath the chrome. [gap] is the breathing room between the last item and the
 * floating element once it is fully scrolled into view.
 */
fun floatingChromeBottomPadding(floatingFootprint: Dp, navBarInset: Dp, gap: Dp = 16.dp): Dp =
        floatingFootprint + navBarInset.coerceAtLeast(0.dp) + gap.coerceAtLeast(0.dp)

/** The system navigation bar's bottom inset, as the `navigationBarsPadding()` modifier applies it. */
@Composable
fun navigationBarBottomInset(): Dp = WindowInsets.navigationBars.asPaddingValues().calculateBottomPadding()
