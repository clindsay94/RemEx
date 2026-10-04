package com.clindsay94.remex.ui.navigation

import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberUpdatedState
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.filter

/**
 * Tapping the destination you are already on (3.0 comb, no-reselect). It used to do nothing; the
 * platform convention is to go back to the top. The shell emits the destination here, and the screen
 * that is showing it scrolls its list to the top.
 */
object TabReselect {
    private val _events =
        MutableSharedFlow<NavDestination>(extraBufferCapacity = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)

    /** Destinations reselected, for the one on screen to act on. Nothing replays. */
    val events: SharedFlow<NavDestination> = _events.asSharedFlow()

    fun emit(destination: NavDestination) {
        _events.tryEmit(destination)
    }

    /**
     * Whether a click on primary tab [clickedIndex] is a reselect rather than a move: the shell is
     * showing the tabs, that tab is the selected one, and the pager is settled on it with nothing
     * still sliding.
     *
     * A tab that is selected but not yet reached (a far jump on its way, or a slide a drag cancelled
     * part-way) is NOT a reselect: that click has to keep going through the normal tab-scroll path,
     * which finishes the move (RemexMotion.tabScrollPlan / settledTabSync).
     */
    fun isPrimaryReselect(
        clickedIndex: Int,
        selectedIndex: Int,
        isAtPrimary: Boolean,
        settledPage: Int,
        currentPage: Int,
        pageOffsetFraction: Float,
        scrollInFlight: Boolean,
    ): Boolean =
        isAtPrimary &&
            clickedIndex == selectedIndex &&
            settledPage == clickedIndex &&
            currentPage == clickedIndex &&
            pageOffsetFraction == 0f &&
            !scrollInFlight
}

/** Runs [onReselect] each time [destination] is tapped while it is already showing. */
@Composable
fun TabReselectEffect(destination: NavDestination, onReselect: suspend () -> Unit) {
    val action by rememberUpdatedState(onReselect)
    LaunchedEffect(destination) {
        TabReselect.events.filter { it == destination }.collect { action() }
    }
}
