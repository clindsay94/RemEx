package com.clindsay94.remex.routines

import java.io.File
import javax.xml.parsers.DocumentBuilderFactory
import org.junit.Assert.assertTrue
import org.junit.Test
import org.w3c.dom.Element

/**
 * Every routine store and the routine keyset are excluded from cloud backup AND device transfer
 * (spec §9 T12, R-SEC-10; RemEx-pp0rt.5). Parses both rule files, the `DeadSdkGuardTest` way, and
 * takes the names from [RoutineStoreNames] so a renamed store cannot slip out of the rules.
 *
 * A routine store in a backup would carry home-network facts and NFC tokens to another device, and
 * restored ciphertext under a keyset that did not travel would read as a corrupt store.
 */
class BackupRulesRoutineExclusionTest {
    private val xmlDir: File =
        listOf(File("src/main/res/xml"), File("app/src/main/res/xml")).firstOrNull { it.isDirectory }
            ?: error("res/xml not found")

    private fun excludes(file: String, section: String?): Set<Pair<String, String>> {
        val doc = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(File(xmlDir, file))
        val scope: Element =
            if (section == null) doc.documentElement
            else doc.getElementsByTagName(section).item(0) as? Element ?: error("$file has no <$section>")
        val nodes = scope.getElementsByTagName("exclude")
        return (0 until nodes.length).map { (nodes.item(it) as Element).let { e -> e.getAttribute("domain") to e.getAttribute("path") } }.toSet()
    }

    private val required: Set<Pair<String, String>> =
        RoutineStoreNames.DATASTORES.map { "file" to "datastore/$it.preferences_pb" }.toSet() +
            RoutineStoreNames.PREFS_FILES.map { "sharedpref" to "$it.xml" }

    @Test
    fun `backup_rules excludes every routine store and the routine keyset`() {
        val missing = required - excludes("backup_rules.xml", null)
        assertTrue("backup_rules.xml is missing $missing", missing.isEmpty())
    }

    @Test
    fun `data_extraction_rules excludes them from cloud backup and from device transfer`() {
        for (section in listOf("cloud-backup", "device-transfer")) {
            val missing = required - excludes("data_extraction_rules.xml", section)
            assertTrue("data_extraction_rules.xml <$section> is missing $missing", missing.isEmpty())
        }
    }

    @Test
    fun `the pairing keyset stays excluded next to the routine one`() {
        // Separate keysets (§6.8): adding the routine one must not have displaced PinnedHostStore's.
        assertTrue(("sharedpref" to "remex_tink_prefs.xml") in excludes("backup_rules.xml", null))
    }
}
