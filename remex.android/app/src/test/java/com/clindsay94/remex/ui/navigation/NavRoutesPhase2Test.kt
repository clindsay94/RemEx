package com.clindsay94.remex.ui.navigation

import com.clindsay94.remex.R
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Phase 2 of the Android UI refresh (RemEx-wqo7a.2): five one-word destinations, Home first,
 * Remote Mouse only as Desktop's Trackpad mode, the Sensors canvas in More.
 */
class NavRoutesPhase2Test {

    @Test
    fun `the bar is Home, Desktop, Apps, Control, with Home first`() {
        // Home first matters: the splash and the tutorial land on pager index 0.
        assertEquals(listOf(Screen.Home, Screen.Desktop, Screen.AppLauncher, Screen.Control), navItems)
    }

    @Test
    fun `More holds Routines first, then Sensors, Files, Connection, PC logs, Settings and Help`() {
        assertEquals(
                listOf(
                        Screen.Routines,
                        Screen.Dashboard,
                        Screen.FileTransfer,
                        Screen.Connection,
                        Screen.PcDiagnostics,
                        Screen.Settings,
                        Screen.Faq,
                ),
                moreItems,
        )
    }

    @Test
    fun `no navigation surface offers the stream route or a Remote Mouse destination`() {
        // The stream is reached only through the Desktop tab's Start streaming, and Remote Mouse
        // is only that tab's Trackpad mode. Screen.RemoteDesktop is a plain Screen, so it cannot be
        // put in either list by type; this pins that the Desktop tab is the one way in.
        val surfaces: List<Any> = navItems + moreItems
        assertFalse(surfaces.any { it == Screen.RemoteDesktop })
        assertTrue(Screen.Desktop in navItems)
    }

    @Test
    fun `Control's segments are Commands then Processes, titled by their own screens`() {
        assertEquals(listOf(ControlSegment.Commands, ControlSegment.Processes), ControlSegment.entries)
        assertEquals(R.string.screen_remote_control_title, ControlSegment.Commands.titleRes)
        assertEquals(R.string.screen_task_manager_title, ControlSegment.Processes.titleRes)
    }

    @Test
    fun `Desktop's modes are Stream then Trackpad`() {
        assertEquals(listOf(DesktopMode.Stream, DesktopMode.Trackpad), DesktopMode.entries)
    }

    @Test
    fun `only the Desktop tab in Trackpad mode takes horizontal swipes from the pager`() {
        assertTrue(pagerTrackpadOwnsSwipes(Screen.Desktop, DesktopMode.Trackpad))
        assertFalse(pagerTrackpadOwnsSwipes(Screen.Desktop, DesktopMode.Stream))
        navItems.filter { it != Screen.Desktop }.forEach { other ->
            assertFalse(pagerTrackpadOwnsSwipes(other, DesktopMode.Trackpad))
            assertFalse(pagerTrackpadOwnsSwipes(other, DesktopMode.Stream))
        }
    }

    @Test
    fun `every bar label, More included, is one word in English`() {
        // Unit tests run with the module root (remex.android/app) as the working directory.
        val strings = File("src/main/res/values/strings.xml")
        assertTrue("values/strings.xml not found at ${strings.absolutePath}", strings.exists())
        val xml = strings.readText()
        val namesById = R.string::class.java.fields.associate { it.getInt(null) to it.name }
        val labelIds = navItems.map { it.titleRes } + R.string.nav_more_label
        assertEquals("expected five bar labels", 5, labelIds.size)
        labelIds.forEach { id ->
            val name = namesById.getValue(id)
            val value =
                    Regex("""name="${Regex.escape(name)}">(.*?)</string>""").find(xml)?.groupValues?.get(1)
            assertTrue("no English value for $name", value != null)
            assertFalse("bar label $name is \"$value\", not one word", value!!.trim().contains(' '))
        }
    }
}
