package com.clindsay94.remex.security

import java.io.File
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Guards that every DataStore declared in `PinnedHostStore.kt` is excluded from cloud backup,
 * device transfer, AND the pre-Marshmallow full-backup path.
 *
 * This is a source-text check, for the same reason [PinnedHostStoreThreadingTest] is one: there is
 * no Robolectric here, so the manifest-referenced XML resources cannot be parsed through the
 * Android backup APIs in a JVM unit test.
 *
 * **WHY THIS EXISTS.** `remex_reconnect_secrets` (PAIR-1 reconnect-secret ciphertext,
 * docs/REGRESSION-GUARDS.md "PinnedHostStore — reconnect-secret persistence") shipped for a full
 * branch without an entry in any of the three exclude lists below, while its sibling stores
 * (`remex_pinned_hosts`, `remex_host_aliases`) were correctly excluded. Nothing failed loudly: the
 * app worked, backups worked, and a cloud backup or device transfer silently carried the secret
 * store off-device. Adding a fourth DataStore to `PinnedHostStore.kt` without updating all three
 * XML files must fail a test, not wait for the next whole-branch review to notice.
 */
class BackupExclusionGuardTest {

    private fun repoRoot(): File =
        System.getProperty("remex.repoRoot")?.let(::File)
            ?: File(".").absoluteFile.let { start ->
                generateSequence(start) { it.parentFile }
                    .firstOrNull { File(it, "remex.android").isDirectory }
            }
            ?: error("could not locate the repository root")

    private fun androidRes(relative: String): File {
        val file = File(repoRoot(), "remex.android/app/src/main/res/$relative")
        assertTrue("expected resource at ${file.path}", file.isFile)
        return file
    }

    private fun androidMain(relative: String): File {
        val file = File(repoRoot(), "remex.android/app/src/main/java/$relative")
        assertTrue("expected source at ${file.path}", file.isFile)
        return file
    }

    /** Every `preferencesDataStore(name = "...")` declared in PinnedHostStore.kt. */
    private fun declaredDataStoreNames(): Set<String> {
        val source =
            androidMain("com/clindsay94/remex/security/PinnedHostStore.kt")
                .readText()
                .replace("\r\n", "\n")
        val names =
            Regex("""preferencesDataStore\(name\s*=\s*"([^"]+)"\)""")
                .findAll(source)
                .map { it.groupValues[1] }
                .toSet()
        assertTrue("expected at least one preferencesDataStore declaration", names.isNotEmpty())
        return names
    }

    /** Every `datastore/<name>.preferences_pb` path excluded in one backup XML file. */
    private fun excludedDataStoreNames(file: File): Set<String> =
        Regex("""datastore/([^."]+)\.preferences_pb""")
            .findAll(file.readText())
            .map { it.groupValues[1] }
            .toSet()

    @Test
    fun `every PinnedHostStore DataStore is excluded from cloud backup and device transfer`() {
        val declared = declaredDataStoreNames()
        val extractionRules = androidRes("xml/data_extraction_rules.xml")
        val text = extractionRules.readText()

        val cloudBackupBlock = text.substringAfter("<cloud-backup>").substringBefore("</cloud-backup>")
        val deviceTransferBlock =
            text.substringAfter("<device-transfer>").substringBefore("</device-transfer>")

        val excludedInCloudBackup =
            Regex("""datastore/([^."]+)\.preferences_pb""").findAll(cloudBackupBlock).map {
                it.groupValues[1]
            }.toSet()
        val excludedInDeviceTransfer =
            Regex("""datastore/([^."]+)\.preferences_pb""").findAll(deviceTransferBlock).map {
                it.groupValues[1]
            }.toSet()

        val missingFromCloudBackup = declared - excludedInCloudBackup
        val missingFromDeviceTransfer = declared - excludedInDeviceTransfer

        assertTrue(
            "DataStore(s) declared in PinnedHostStore.kt but missing from <cloud-backup> in " +
                "data_extraction_rules.xml: $missingFromCloudBackup",
            missingFromCloudBackup.isEmpty(),
        )
        assertTrue(
            "DataStore(s) declared in PinnedHostStore.kt but missing from <device-transfer> in " +
                "data_extraction_rules.xml: $missingFromDeviceTransfer",
            missingFromDeviceTransfer.isEmpty(),
        )
    }

    @Test
    fun `every PinnedHostStore DataStore is excluded from the legacy full-backup path`() {
        val declared = declaredDataStoreNames()
        val backupRules = androidRes("xml/backup_rules.xml")
        val excluded = excludedDataStoreNames(backupRules)

        val missing = declared - excluded
        assertTrue(
            "DataStore(s) declared in PinnedHostStore.kt but missing from backup_rules.xml: $missing",
            missing.isEmpty(),
        )
    }
}
