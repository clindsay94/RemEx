package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.AppLauncherBody
import com.clindsay94.remex.ui.screens.LauncherRefreshState
import com.clindsay94.remex.ui.screens.LauncherRefreshTracker
import com.clindsay94.remex.ui.screens.appLauncherBodyFor
import org.junit.Assert.assertEquals
import org.junit.Test

/** The Apps screen's refresh state and the body it picks (RemEx-wqo7a.6). */
class AppLauncherRefreshTest {

    @Test
    fun anAnsweredRefreshEndsIdle() {
        val tracker = LauncherRefreshTracker()
        val token = tracker.begin()
        assertEquals(LauncherRefreshState.Refreshing, tracker.state.value)
        tracker.onEntriesArrived()
        tracker.onTimedOut(token)
        assertEquals(LauncherRefreshState.Idle, tracker.state.value)
    }

    @Test
    fun anUnansweredRefreshSaysSo() {
        val tracker = LauncherRefreshTracker()
        val token = tracker.begin()
        tracker.onTimedOut(token)
        assertEquals(LauncherRefreshState.NoAnswer, tracker.state.value)
    }

    @Test
    fun anOlderRefreshesTimeout_doesNotCutANewerOneShort() {
        val tracker = LauncherRefreshTracker()
        val first = tracker.begin()
        val second = tracker.begin()
        tracker.onTimedOut(first)
        assertEquals(LauncherRefreshState.Refreshing, tracker.state.value)
        tracker.onTimedOut(second)
        assertEquals(LauncherRefreshState.NoAnswer, tracker.state.value)
    }

    @Test
    fun aLateAppList_clearsTheNoAnswerMessage() {
        val tracker = LauncherRefreshTracker()
        tracker.onTimedOut(tracker.begin())
        tracker.onEntriesArrived()
        assertEquals(LauncherRefreshState.Idle, tracker.state.value)
    }

    @Test
    fun dismissingOnlyClearsNoAnswer() {
        val tracker = LauncherRefreshTracker()
        tracker.begin()
        tracker.dismissNoAnswer()
        assertEquals(LauncherRefreshState.Refreshing, tracker.state.value)
        tracker.onTimedOut(1L)
        tracker.dismissNoAnswer()
        assertEquals(LauncherRefreshState.Idle, tracker.state.value)
    }

    @Test
    fun body_aListThePcSentAlwaysWins() {
        for (connected in listOf(true, false)) {
            for (state in LauncherRefreshState.entries) {
                assertEquals(AppLauncherBody.Apps, appLauncherBodyFor(connected, hasApps = true, refreshState = state))
            }
        }
    }

    @Test
    fun body_withoutAList_explainsWhy() {
        assertEquals(AppLauncherBody.Disconnected, appLauncherBodyFor(false, false, LauncherRefreshState.Idle))
        assertEquals(AppLauncherBody.Loading, appLauncherBodyFor(true, false, LauncherRefreshState.Refreshing))
        assertEquals(AppLauncherBody.NoAnswer, appLauncherBodyFor(true, false, LauncherRefreshState.NoAnswer))
        assertEquals(AppLauncherBody.Empty, appLauncherBodyFor(true, false, LauncherRefreshState.Idle))
    }
}
