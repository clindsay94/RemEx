package com.clindsay94.remex.ui

import com.clindsay94.remex.ui.screens.AppEntry
import com.clindsay94.remex.ui.screens.filterLauncherApps
import com.clindsay94.remex.ui.screens.launchSucceeded
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** What the Apps screen tells you after a tap, and its search filter. */
class AppLauncherLaunchTest {

    @Test
    fun aSuccessReply_isASuccess() {
        assertTrue(launchSucceeded("""{"success":true}"""))
    }

    @Test
    fun aRefusalMissingBlankOrGarbledReply_isNotASuccess() {
        assertFalse(launchSucceeded("""{"success":false,"message":"x"}"""))
        assertFalse(launchSucceeded("""{"message":"no success field"}"""))
        assertFalse(launchSucceeded(""))
        assertFalse(launchSucceeded("   "))
        assertFalse(launchSucceeded(null))
        assertFalse(launchSucceeded("not json"))
    }

    private val apps = listOf(
        AppEntry("Calculator", "calc.exe"),
        AppEntry("Notepad", "notepad.exe"),
        AppEntry("Google Chrome", "chrome.exe"),
    )

    @Test
    fun aBlankQuery_returnsEveryApp() {
        assertEquals(apps, filterLauncherApps(apps, ""))
        assertEquals(apps, filterLauncherApps(apps, "   "))
    }

    @Test
    fun matching_ignoresCase() {
        assertEquals(listOf(apps[2]), filterLauncherApps(apps, "CHROME"))
    }

    @Test
    fun theQueryIsTrimmed() {
        assertEquals(listOf(apps[1]), filterLauncherApps(apps, "  note "))
    }

    @Test
    fun aMiss_returnsNothing() {
        assertTrue(filterLauncherApps(apps, "zzz").isEmpty())
    }
}
