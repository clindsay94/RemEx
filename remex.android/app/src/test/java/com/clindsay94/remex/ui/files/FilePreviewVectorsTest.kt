package com.clindsay94.remex.ui.files

import com.clindsay94.remex.ui.files.preview.PreviewClassifier
import com.clindsay94.remex.ui.files.preview.PreviewKind
import com.clindsay94.remex.ui.files.preview.SyntaxKind
import com.clindsay94.remex.ui.files.preview.SyntaxLanguage
import com.clindsay94.remex.ui.files.preview.SyntaxSpan
import com.clindsay94.remex.ui.files.preview.SyntaxTokenizer
import com.clindsay94.remex.ui.files.preview.TextPreviewDecoder
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The phone half of the preview parity check (file browser redesign, 2026-10-08). The vectors were generated from
 * the PC's C# classifier, decoder and tokenizer; `FilePreviewVectorTests` in remex.desktop.tests asserts the PC
 * against the same file and keeps this copy byte-identical. A rule changed on one platform only fails one side.
 */
class FilePreviewVectorsTest {
    private val doc = JSONObject(
        javaClass.classLoader!!.getResourceAsStream("file-preview-vectors.json")!!.readBytes().toString(Charsets.UTF_8),
    )

    private fun section(name: String): List<JSONObject> {
        val array = doc.getJSONArray(name)
        assertTrue("an empty '$name' section would pass vacuously", array.length() > 3)
        return (0 until array.length()).map { array.getJSONObject(it) }
    }

    @Test
    fun theClassifierMatchesThePc() {
        for (v in section("classify")) {
            val name = v.getString("name")
            // decodesHeif = false asks for the PC's answer; the phone's own HEIF rule is checked below.
            assertEquals(name, v.getString("kind"), PreviewClassifier.classify(name, v.getBoolean("isDirectory"), decodesHeif = false).name)
        }
    }

    @Test
    fun onThePhone_HeifIsAFullImage_AndEverythingElseIsAsOnThePc() {
        for (v in section("classify")) {
            val name = v.getString("name")
            val pc = PreviewKind.valueOf(v.getString("kind"))
            val expected = if (pc == PreviewKind.ImageThumbnailOnly) PreviewKind.Image else pc
            assertEquals(name, expected, PreviewClassifier.classify(name, v.getBoolean("isDirectory")))
        }
    }

    @Test
    fun theLanguagesMatchThePc() {
        for (v in section("languages")) {
            assertEquals(v.getString("name"), v.getString("language"), SyntaxTokenizer.languageFor(v.getString("name")).name)
        }
    }

    @Test
    fun theTokenizerMatchesThePc() {
        for (v in section("tokenize")) {
            val line = v.getString("line")
            val spans: JSONArray = v.getJSONArray("spans")
            val expected = (0 until spans.length()).map {
                val s = spans.getJSONArray(it)
                SyntaxSpan(s.getInt(0), s.getInt(1), SyntaxKind.valueOf(s.getString(2)))
            }
            assertEquals(line, expected, SyntaxTokenizer.tokenize(line, SyntaxLanguage.valueOf(v.getString("language"))))
        }
    }

    @Test
    fun theDecoderMatchesThePc() {
        for (v in section("decode")) {
            val hex = v.getString("hex")
            val bytes = ByteArray(hex.length / 2) { hex.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
            assertEquals(hex, v.getString("text"), TextPreviewDecoder.decode(bytes, v.getBoolean("startsMidFile"), v.getBoolean("endsMidFile")))
            assertEquals(hex, v.getInt("completeUtf8Length"), TextPreviewDecoder.completeUtf8Length(bytes))
            assertEquals(hex, v.getBoolean("looksBinary"), TextPreviewDecoder.looksBinary(bytes))
        }
    }
}
