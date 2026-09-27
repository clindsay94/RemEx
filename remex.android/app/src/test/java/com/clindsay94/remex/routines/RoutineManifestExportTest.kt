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

    @Test
    fun `every routine component is not exported except the NFC tag target`() {
        val routineComponents = components.filter { it.attr("name").contains(".routines.") }
        assertTrue("expected the routine components in the manifest", routineComponents.size >= 5)
        for (component in routineComponents) {
            val name = component.attr("name")
            if (name == nfcActivity) continue
            assertEquals("$name must be exported=\"false\" (spec T1)", "false", component.attr("exported"))
        }
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
