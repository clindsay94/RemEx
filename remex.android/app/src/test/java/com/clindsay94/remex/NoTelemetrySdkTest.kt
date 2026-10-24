package com.clindsay94.remex

import java.io.File
import javax.xml.parsers.DocumentBuilderFactory
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.w3c.dom.Element

/**
 * The Play data safety form says "No data collected" (RemEx-6cci2), and Firebase has been re-added
 * more than once. The build-time ban in app/build.gradle.kts catches the dependency; this keeps the
 * ban itself, and the ML Kit telemetry opt-out, from being quietly deleted.
 */
class NoTelemetrySdkTest {
    private val androidNs = "http://schemas.android.com/apk/res/android"
    private val toolsNs = "http://schemas.android.com/tools"
    private val repoRoot = File(System.getProperty("remex.repoRoot") ?: error("remex.repoRoot not set"))
    private val androidRoot = File(repoRoot, "remex.android")

    @Test
    fun `the app build still bans analytics and crash SDKs`() {
        val appBuild = File(androidRoot, "app/build.gradle.kts").readText()
        assertTrue(appBuild.contains("val bannedTelemetryPlugins"))
        assertTrue(appBuild.contains("\"firebase-analytics\", \"firebase-crashlytics\""))
        assertTrue(appBuild.contains("\"play-services-measurement\""))
    }

    @Test
    fun `no build file declares the google-services or crashlytics plugin`() {
        val builds = listOf(File(androidRoot, "build.gradle.kts"), File(androidRoot, "app/build.gradle.kts"))
        val declared =
            builds.flatMap { file ->
                file.readLines()
                    .filter { it.trimStart().startsWith("id(") || it.trimStart().startsWith("alias(") }
                    .filter { it.contains("google-services") || it.contains("crashlytics") || it.contains("firebase") }
                    .map { "${file.name}: ${it.trim()}" }
            }
        assertEquals(emptyList<String>(), declared)
    }

    @Test
    fun `ML Kit's datatransport upload backend is removed from the manifest`() {
        val manifest =
            DocumentBuilderFactory.newInstance()
                .apply { isNamespaceAware = true }
                .newDocumentBuilder()
                .parse(File(androidRoot, "app/src/main/AndroidManifest.xml"))
        val services = manifest.getElementsByTagName("service")
        val discovery =
            (0 until services.length)
                .map { services.item(it) as Element }
                .filter {
                    it.getAttributeNS(androidNs, "name") ==
                        "com.google.android.datatransport.runtime.backends.TransportBackendDiscovery"
                }
        assertFalse("TransportBackendDiscovery removal is missing", discovery.isEmpty())
        assertEquals("remove", discovery.single().getAttributeNS(toolsNs, "node"))
    }
}
