package com.clindsay94.remex.routines

import java.io.File
import javax.xml.parsers.DocumentBuilderFactory
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.w3c.dom.Element

/**
 * Spec §9 T1, R-SEC-01: no app can start a routine by intent. Parses `src/main/AndroidManifest.xml`
 * (the `DeadSdkGuardTest` way): every routine component is `exported="false"`, except
 * `NfcRoutineActivity`, which tag dispatch requires to be exported and which must therefore carry
 * `android:permission="android.permission.DISPATCH_NFC_MESSAGE"` (only the NFC service holds it).
 * And nothing else in the app handles `remex://`.
 */
class RoutineManifestExportTest {
    private val ns = "http://schemas.android.com/apk/res/android"
    private val manifest: File =
        listOf(File("src/main/AndroidManifest.xml"), File("app/src/main/AndroidManifest.xml")).firstOrNull { it.isFile }
            ?: error("AndroidManifest.xml not found")

    private val components: List<Element> by lazy {
        val doc = DocumentBuilderFactory.newInstance().apply { isNamespaceAware = true }.newDocumentBuilder().parse(manifest)
        listOf("activity", "activity-alias", "receiver", "service", "provider").flatMap { tag ->
            val nodes = doc.getElementsByTagName(tag)
            (0 until nodes.length).map { nodes.item(it) as Element }
        }
    }

    private fun Element.attr(name: String): String = getAttributeNS(ns, name)

    private val nfcActivity = ".routines.nfc.NfcRoutineActivity"
    private val widgetConfig = ".routines.widget.RoutineWidgetConfigActivity"

    /**
     * The allowlist: the NFC tag target (guarded by DISPATCH_NFC_MESSAGE) and the widget's configure
     * activity (launchers start it directly; it can only PICK a routine, see the test below).
     */
    private val exportedAllowlist = setOf(nfcActivity, widgetConfig)

    @Test
    fun `every routine component is not exported except the allowlisted two`() {
        val routineComponents = components.filter { it.attr("name").contains(".routines.") }
        assertTrue("expected the routine components in the manifest", routineComponents.size >= 5)
        for (component in routineComponents) {
            val name = component.attr("name")
            if (name in exportedAllowlist) continue
            assertEquals("$name must be exported=\"false\" (spec T1)", "false", component.attr("exported"))
        }
    }

    @Test
    fun `the exported widget configure activity only answers APPWIDGET_CONFIGURE and cannot run a routine`() {
        val config = components.single { it.attr("name") == widgetConfig }
        val actions = config.getElementsByTagName("action")
        assertEquals(listOf("android.appwidget.action.APPWIDGET_CONFIGURE"), (0 until actions.length).map { (actions.item(it) as Element).attr("name") })
        val source =
            listOf(File("src/main/java"), File("app/src/main/java")).first { it.isDirectory }
                .resolve("com/clindsay94/remex/routines/widget/RoutineWidgetConfigActivity.kt").readText()
        // No path from it to a run: no manual surface, no repository run, no run source, no runner.
        for (forbidden in listOf("RoutineManualSurfaces", ".run(", "RoutineRunSources", "Routines.runner", "RoutineConfirmActivity")) {
            assertTrue("RoutineWidgetConfigActivity must not reference $forbidden", !source.contains(forbidden))
        }
        // And it binds only widgets of RemEx's own Routine provider.
        assertTrue(source.contains("ComponentName(this, RoutineWidgetReceiver::class.java)"))
    }

    @Test
    fun `NfcRoutineActivity is exported only behind DISPATCH_NFC_MESSAGE`() {
        val nfc = components.single { it.attr("name") == nfcActivity }
        assertEquals("true", nfc.attr("exported"))
        assertEquals(
            "NfcRoutineActivity must keep DISPATCH_NFC_MESSAGE, or any app can run routines by intent",
            "android.permission.DISPATCH_NFC_MESSAGE",
            nfc.attr("permission"),
        )
        val actions = nfc.getElementsByTagName("action")
        val names = (0 until actions.length).map { (actions.item(it) as Element).attr("name") }
        assertEquals(listOf("android.nfc.action.NDEF_DISCOVERED"), names)
    }

    @Test
    fun `no other component handles the remex scheme`() {
        val handlers =
            components.filter { component ->
                val data = component.getElementsByTagName("data")
                (0 until data.length).any { (data.item(it) as Element).attr("scheme").equals("remex", ignoreCase = true) }
            }.map { it.attr("name") }
        assertEquals(listOf(nfcActivity), handlers)
    }
}
