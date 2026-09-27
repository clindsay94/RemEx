package com.clindsay94.remex.ui.splash

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * The Live Handshake field is ONE shader on two platforms (RemEx-8g6n0): SkiaSharp compiles the
 * PC copy, Android's RuntimeShader compiles this one. They must stay byte-identical, or the two
 * splashes quietly stop being the same splash.
 */
class LiveHandshakeShaderParityTest {

    private fun repoRoot(): File =
        System.getProperty("remex.repoRoot")?.let(::File)
            ?: File(".").absoluteFile.let { start ->
                generateSequence(start) { it.parentFile }
                    .firstOrNull { File(it, "remex.android").isDirectory }
            }
            ?: error("could not locate the repository root")

    @Test
    fun `the Android field shader is byte-identical to the PC one`() {
        val root = repoRoot()
        val android = File(root, "remex.android/app/src/main/res/raw/live_handshake_field.agsl")
        val pc = File(root, "remex.branding/Shaders/live_handshake_field.sksl")
        assertTrue("missing $android", android.isFile)
        assertTrue("missing $pc", pc.isFile)
        assertArrayEquals(
            "live_handshake_field.agsl and live_handshake_field.sksl differ; edit both together",
            pc.readBytes(),
            android.readBytes(),
        )
    }
}
