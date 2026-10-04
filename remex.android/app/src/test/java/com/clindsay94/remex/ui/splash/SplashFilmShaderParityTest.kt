package com.clindsay94.remex.ui.splash

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * The film splashes' field is ONE shader on two platforms (RemEx-pp4cm.11), like Live Handshake's:
 * SkiaSharp compiles the PC copy, Android's RuntimeShader compiles this one. They must stay
 * byte-identical, and the style ids the two hosts pass must mean the same world.
 */
class SplashFilmShaderParityTest {

    private fun repoRoot(): File =
        System.getProperty("remex.repoRoot")?.let(::File)
            ?: File(".").absoluteFile.let { start ->
                generateSequence(start) { it.parentFile }
                    .firstOrNull { File(it, "remex.android").isDirectory }
            }
            ?: error("could not locate the repository root")

    @Test
    fun `the Android film shader is byte-identical to the PC one`() {
        val root = repoRoot()
        val android = File(root, "remex.android/app/src/main/res/raw/splash_film_field.agsl")
        val pc = File(root, "remex.branding/Shaders/splash_film_field.sksl")
        assertTrue("missing $android", android.isFile)
        assertTrue("missing $pc", pc.isFile)
        assertArrayEquals(
            "splash_film_field.agsl and splash_film_field.sksl differ; edit both together",
            pc.readBytes(),
            android.readBytes(),
        )
    }

    @Test
    fun `style ids match the PC enum`() {
        // remex.branding/SplashFilmField.cs: Command = 0, Cosmic = 1, Pong = 2.
        val pc = File(repoRoot(), "remex.branding/SplashFilmField.cs").readText()
        for (style in FilmStyle.entries) {
            assertTrue(
                "SplashFilmField.cs must declare ${style.name} = ${style.id.toInt()}",
                pc.contains("${style.name} = ${style.id.toInt()},"),
            )
        }
        assertEquals(3, FilmStyle.entries.size)
    }
}
