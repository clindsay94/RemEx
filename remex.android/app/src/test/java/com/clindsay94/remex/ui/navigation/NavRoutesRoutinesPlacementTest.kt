package com.clindsay94.remex.ui.navigation

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test

/**
 * Routines is the FIRST More destination and never a fifth primary (routines spec 1.2, R-UX-01;
 * RemEx-pp0rt.6). The bar holds four primaries plus More (Home, Desktop, Apps, Control since
 * RemEx-wqo7a.2), and navItems' order drives pager indices.
 */
class NavRoutesRoutinesPlacementTest {
    @Test
    fun `Routines is first in moreItems`() {
        assertEquals(Screen.Routines, moreItems.first())
    }

    @Test
    fun `Routines is not a primary tab and the four primaries are unchanged`() {
        assertFalse(navItems.any { it == Screen.Routines })
        assertEquals(listOf(Screen.Home, Screen.Desktop, Screen.AppLauncher, Screen.Control), navItems)
    }

    @Test
    fun `Routines appears exactly once`() {
        assertEquals(1, moreItems.count { it == Screen.Routines })
    }
}
