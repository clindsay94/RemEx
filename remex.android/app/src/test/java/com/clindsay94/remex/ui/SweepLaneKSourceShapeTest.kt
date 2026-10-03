package com.clindsay94.remex.ui

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 3.0 sweep lane K: the fixes whose behaviour lives in Compose or in the manager's wiring, which
 * this module cannot run (no Robolectric, no Compose UI harness). SOURCE-SCANNED, the established
 * precedent here (see `PerfP3B5SourceShapeTest`): each test pins the shape the fix put in place, so
 * a later edit that quietly reverts it fails loudly.
 */
class SweepLaneKSourceShapeTest {

    private val root: File =
        System.getProperty("remex.repoRoot")?.let(::File)
            ?: File(".").absoluteFile.let { generateSequence(it) { p -> p.parentFile }
                .firstOrNull { File(it, "remex.android").isDirectory } }
            ?: error("could not locate the repository root")

    private fun source(relative: String): String {
        val file = File(root, "remex.android/app/src/main/java/com/clindsay94/remex/$relative")
        assertTrue("expected to find $relative at ${file.path}", file.isFile)
        return file.readText()
    }

    private fun strings(folder: String): String {
        val file = File(root, "remex.android/app/src/main/res/$folder/strings.xml")
        assertTrue("expected ${file.path}", file.isFile)
        return file.readText(Charsets.UTF_8)
    }

    private val localeFolders =
        listOf("values", "values-es", "values-fr", "values-hi", "values-in", "values-pl", "values-pt-rBR", "values-tr", "values-uk")

    private fun value(xml: String, key: String): String? =
        Regex("""<string name="$key"[^>]*>(.*?)</string>""", RegexOption.DOT_MATCHES_ALL).find(xml)?.groupValues?.get(1)

    @Test
    fun `K3 the process list polls only once the PC has accepted this connection`() {
        val vm = source("ui/screens/TaskManagerViewModel.kt")
        val refresh = vm.substring(vm.indexOf("fun refreshProcesses("))
        val gate = refresh.substring(0, refresh.indexOf("return@launch"))
        assertTrue("the poll must gate on isAuthenticated", gate.contains("RemexClientManager.isAuthenticated.value"))
        assertFalse("isConnected alone lets a refused request count as a timeout", gate.contains("isConnected.value"))

        val screen = source("ui/screens/TaskManagerScreen.kt")
        assertTrue(
            "the auto-refresh must start on the authenticated edge",
            screen.contains("setAutoRefreshEnabled(isVisible && isAuthenticated)"),
        )
    }

    @Test
    fun `K4 the three dead strings stay gone in every locale`() {
        for (folder in localeFolders) {
            val xml = strings(folder)
            for (key in listOf("pairing_error_cert_mismatch", "qr_error_old_format", "qr_error_invalid")) {
                assertFalse("$folder still declares $key", xml.contains("name=\"$key\""))
            }
        }
    }

    @Test
    fun `K5 the Personalize screen and its tab carry the PC's name in every locale`() {
        val pc =
            mapOf(
                "values" to "Personalize", "values-es" to "Personalizar", "values-fr" to "Personnaliser",
                "values-hi" to "वैयक्तिकृत करें", "values-in" to "Personalisasi", "values-pl" to "Personalizuj",
                "values-pt-rBR" to "Personalizar", "values-tr" to "Kişiselleştir", "values-uk" to "Персоналізувати",
            )
        for ((folder, name) in pc) {
            val xml = strings(folder)
            assertEquals(folder, name, value(xml, "screen_personalization_title"))
            assertEquals(folder, name, value(xml, "settings_tab_personalization"))
        }
    }

    @Test
    fun `K5 the Line view reads the same as the PC's in Hindi and Polish`() {
        assertEquals("रेखा", value(strings("values-hi"), "dashboard_view_line"))
        assertEquals("Linia", value(strings("values-pl"), "dashboard_view_line"))
    }

    @Test
    fun `K5 the Preview label is not baked in capitals`() {
        for (folder in localeFolders) {
            val text = value(strings(folder), "personalization_preview") ?: error("$folder: no personalization_preview")
            val letters = text.filter { it.isLetter() && it.code < 0x0900 }
            if (letters.length > 1) assertFalse("$folder: '$text' is all capitals", letters == letters.uppercase())
        }
    }

    @Test
    fun `K6 the frame-rate FAQ no longer calls 15-20 FPS enough against a 120 default`() {
        for (folder in localeFolders) {
            val answer = value(strings(folder), "faq_a7") ?: error("$folder: no faq_a7")
            assertFalse("$folder still recommends 15-20 FPS", answer.contains("15–20"))
            assertTrue("$folder must say the default is 120", answer.contains("120"))
        }
    }

    @Test
    fun `K8 every reader of live telemetry holds a lease, and the gate is told`() {
        val manager = source("RemexClientManager.kt")
        assertTrue(
            "the telemetry gate must hear whether anything reads the stream",
            manager.contains("telemetryWanted = wanted") && manager.contains("TelemetryDemand.leases.wanted"),
        )
        for ((file, key) in
            listOf(
                "ui/screens/DashboardScreen.kt" to "TelemetryDemand.SENSORS_CANVAS",
                "ui/screens/HomeScreen.kt" to "TelemetryDemand.HOME_PINNED",
                "ui/screens/HomeScreen.kt" to "TelemetryDemand.HOME_PIN_SHEET",
                "ui/routines/RoutineEditor.kt" to "TelemetryDemand.ROUTINE_EDITOR",
                "ui/routines/RoutineTemplatesPane.kt" to "TelemetryDemand.ROUTINE_TEMPLATES",
            )
        ) {
            assertTrue("$file must hold $key", source(file).contains("TelemetryLeaseEffect($key"))
        }
        assertTrue(
            // Unconditional while Home is on screen: the PC card's uptime line reads telemetry too,
            // and froze when the lease depended on having pinned sensors.
            "Home holds its lease whenever it is shown, pins or not",
            source("ui/screens/HomeScreen.kt").contains("TelemetryLeaseEffect(TelemetryDemand.HOME_PINNED)"),
        )
    }

    @Test
    fun `K7 the idle teardown runs, and everything that uses the connection tells it`() {
        val manager = source("RemexClientManager.kt")
        assertTrue("initialize must start the idle teardown", manager.contains("startIdleTeardown(appContext, reconnectAllowed)"))
        val oneShot = manager.substring(manager.indexOf("internal suspend fun startOneShotConnect("))
            .let { it.substring(0, it.indexOf("\n    }")) }
        assertTrue("a widget tap or routine connect is use", oneShot.contains("ConnectionActivity.touch()"))

        val worker = source("routines/RoutineWorker.kt")
        assertTrue("a routine run holds the connection", worker.contains("ConnectionActivity.hold(hold)"))
        assertTrue("and lets go of it however the run ends", worker.contains("finally {\n            ConnectionActivity.release(hold)"))

        val desktop = source("ui/screens/RemoteDesktopViewModel.kt")
        assertTrue("a Remote Desktop stream holds the connection", desktop.contains("ConnectionActivity.hold(ConnectionActivity.REMOTE_DESKTOP)"))
    }

    @Test
    fun `K9 the header text finds its size in one pass`() {
        val bar = source("ui/components/RemexFlexibleTopBar.kt")
        assertTrue("auto-size must do the fitting", bar.contains("TextAutoSize.StepBased("))
        assertFalse("the per-frame shrink loop must not come back", bar.contains("HEADER_SHRINK_STEP"))
        assertTrue("the two-line fallback at the floor must stay", bar.contains("maxLines = 2"))
    }

    @Test
    fun `R1 a drag reads the current cell size, not the one captured before a rotation`() {
        val grid = source("ui/screens/DashboardScreen.kt")
        val gesture = grid.substring(grid.indexOf("detectDragGesturesAfterLongPress("))
            .let { it.substring(0, it.indexOf("onDragEnd")) }
        assertTrue(grid.contains("val currentCell by rememberUpdatedState(cell)"))
        assertTrue(grid.contains("val currentRowHeight by rememberUpdatedState(rowHeight)"))
        assertFalse("the gesture must not read the captured cell", Regex("""[(,]\s*cell\s*[,)]""").containsMatchIn(gesture))
        assertFalse("the gesture must not read the captured row height", Regex("""[(,]\s*rowHeight\s*[,)]""").containsMatchIn(gesture))
    }

    @Test
    fun `R2 the phase 3 screens draw local glyphs, not material-icons-extended`() {
        val banned =
            mapOf(
                "ui/screens/DashboardScreen.kt" to listOf("automirrored.filled.Undo", "automirrored.filled.Redo", "filled.DeleteSweep"),
                "ui/screens/sensors/SensorGridCard.kt" to listOf("outlined.PushPin"),
                "ui/screens/HomeScreen.kt" to listOf("filled.Link"),
                "ui/navigation/NavRoutes.kt" to listOf("filled.Wifi"),
                // The "needs pairing" state (phase 6 sweep P3).
                "ui/screens/CommonComponents.kt" to listOf("filled.LinkOff"),
            )
        for ((file, imports) in banned) {
            val lines = source(file).lines().map { it.trim() }
            for (name in imports) assertFalse("$file imports $name", "import androidx.compose.material.icons.$name" in lines)
        }
    }
}
