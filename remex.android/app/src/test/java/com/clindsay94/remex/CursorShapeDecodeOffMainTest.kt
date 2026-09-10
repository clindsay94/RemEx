package com.clindsay94.remex

import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The cursor-shape decode stays off the main thread (RemEx-z2db8).
 *
 * A Base64 decode plus a `width * height` per-pixel BGRA-to-ARGB repack used to run inside a
 * `viewModelScope` collector, which is `Dispatchers.Main.immediate`. It is not periodic work: cursor
 * shapes change whenever the pointer crosses a UI element on the host, so it fires on ordinary mouse
 * movement, and it scales with the cursor — a high-DPI 256x256 one is sixty-five thousand iterations
 * plus a quarter-megabyte decode, on the thread composing the frame being watched.
 *
 * **A SOURCE TRIPWIRE, BECAUSE THE REGRESSION IS INVISIBLE.** Moving this back onto Main breaks no
 * test, changes no output, and produces no error — it just quietly spends main-thread time during a
 * stream. There is nothing to assert about the RESULT, because the result is identical either way;
 * the only observable difference is where the work happened. Testing that properly would mean
 * instrumenting the ViewModel against a real Android bitmap and a main-looper idler, which is a much
 * heavier thing to own than what it protects.
 *
 * It deliberately does not pin WHICH dispatcher, only that the decode is handed to one. Default is
 * the right choice for CPU work today, but the assertion worth keeping is that this does not run
 * where the frames are drawn.
 */
class CursorShapeDecodeOffMainTest {

    @Test
    fun `the cursor shape decode is handed to a background dispatcher`() {
        // Two candidates: Gradle runs unit tests from the module directory, other runners a level up.
        val relative = "src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopViewModel.kt"
        val candidates = listOf(java.io.File(relative), java.io.File("app/$relative"))
        val source = candidates.firstOrNull { it.isFile }

        assertTrue(
            "RemoteDesktopViewModel.kt not found - tried " + candidates.joinToString { it.path },
            source != null
        )

        val text = source!!.readText()

        assertTrue(
            "handleCursorShape should decode inside withContext(Dispatchers.Default) rather than on " +
                "the viewModelScope collector, which is Main.immediate",
            text.contains("withContext(Dispatchers.Default) { decodeCursorShape(shapeJson) }")
        )

        // AND THE HEAVY PART MUST STILL BE THE PART THAT MOVED. If the repack loop drifted back into
        // the caller, the withContext above would still be there and would be guarding nothing.
        val decodeStart = text.indexOf("private fun decodeCursorShape")
        assertTrue("decodeCursorShape not found", decodeStart >= 0)

        val decodeBody = text.substring(decodeStart, minOf(decodeStart + 2500, text.length))
        assertTrue(
            "the BGRA-to-ARGB repack should live in decodeCursorShape, which is what runs off Main",
            decodeBody.contains("Base64.decode") && decodeBody.contains("shl 24")
        )
    }
}
