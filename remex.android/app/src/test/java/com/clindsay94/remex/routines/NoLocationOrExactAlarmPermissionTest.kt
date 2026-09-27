package com.clindsay94.remex.routines

import java.io.File
import javax.xml.parsers.DocumentBuilderFactory
import org.junit.Assert.assertEquals
import org.junit.Test
import org.w3c.dom.Element

/**
 * Routines add no Play-policy-sensitive surface (spec §9 T16; RemEx-pp0rt.5): no location
 * permission (home detection reads network facts, never SSID or location), no exact alarms, and no
 * new foreground-service type (the runner is expedited WorkManager work). Plus the one routine
 * component this slice adds stays unexported (T1).
 */
class NoLocationOrExactAlarmPermissionTest {
    private val android = "http://schemas.android.com/apk/res/android"

    private val manifest =
        DocumentBuilderFactory.newInstance()
            .apply { isNamespaceAware = true }
            .newDocumentBuilder()
            .parse(listOf(File("src/main/AndroidManifest.xml"), File("app/src/main/AndroidManifest.xml")).first { it.isFile })

    private fun elements(tag: String): List<Element> {
        val nodes = manifest.getElementsByTagName(tag)
        return (0 until nodes.length).map { nodes.item(it) as Element }
    }

    @Test
    fun `no location or exact-alarm permission is requested`() {
        val forbidden =
            setOf(
                "android.permission.ACCESS_FINE_LOCATION",
                "android.permission.ACCESS_COARSE_LOCATION",
                "android.permission.ACCESS_BACKGROUND_LOCATION",
                "android.permission.SCHEDULE_EXACT_ALARM",
                "android.permission.USE_EXACT_ALARM",
                "android.permission.FOREGROUND_SERVICE_LOCATION",
            )
        val requested = elements("uses-permission").map { it.getAttributeNS(android, "name") }.toSet()
        assertEquals(emptySet<String>(), requested intersect forbidden)
    }

    @Test
    fun `the only foreground-service type stays the connection service's`() {
        val types =
            elements("service").mapNotNull { it.getAttributeNS(android, "foregroundServiceType").takeIf(String::isNotEmpty) }.toSet()
        assertEquals(setOf("connectedDevice"), types)
    }

    @Test
    fun `the routine notification receiver is not exported`() {
        val receiver = elements("receiver").single { it.getAttributeNS(android, "name") == ".routines.RoutineNotificationActionReceiver" }
        assertEquals("false", receiver.getAttributeNS(android, "exported"))
    }
}
