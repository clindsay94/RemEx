package com.clindsay94.remex.ui

import java.io.File
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The tutorial's QR page names the PC's real button (sweep P2, RemEx-wqo7a.7). The PC shows the QR
 * from Home's "Pair a phone" (`Home_PairPhoneButton`); the old step sent people to a "Show QR Code"
 * setting that no longer exists. Each phone locale must use that locale's PC wording, so this reads
 * the PC's own resx files rather than a copied list.
 *
 * Unit tests run from the module root (remex.android/app), so the PC strings are three levels up.
 */
class TutorialQrStepNamesPcButtonTest {

    /** Android values folder -> PC resx file, one pair per supported language. */
    private val locales =
            mapOf(
                    "values" to "Strings.resx",
                    "values-es" to "Strings.es.resx",
                    "values-fr" to "Strings.fr.resx",
                    "values-hi" to "Strings.hi.resx",
                    "values-in" to "Strings.id.resx",
                    "values-pl" to "Strings.pl.resx",
                    "values-pt-rBR" to "Strings.pt-BR.resx",
                    "values-tr" to "Strings.tr.resx",
                    "values-uk" to "Strings.uk.resx",
            )

    private fun read(path: String): String {
        val file = File(path)
        assertTrue("Could not locate $path at ${file.absolutePath} - test setup is broken.", file.exists())
        return file.readText(Charsets.UTF_8)
    }

    private fun pcButton(resx: String): String {
        val match =
                Regex("""<data name="Home_PairPhoneButton"[^>]*>\s*<value>(.*?)</value>""", RegexOption.DOT_MATCHES_ALL)
                        .find(read("../../remex.desktop/Localization/$resx"))
        assertTrue("$resx has no Home_PairPhoneButton", match != null)
        return match!!.groupValues[1].trim()
    }

    private fun qrBody(values: String): String {
        val match =
                Regex("""<string name="tutorial_page_qr_body">(.*?)</string>""", RegexOption.DOT_MATCHES_ALL)
                        .find(read("src/main/res/$values/strings.xml"))
        assertTrue("$values/strings.xml has no tutorial_page_qr_body", match != null)
        // Android escapes quotes in the XML; the PC text has none to compare against.
        return match!!.groupValues[1].replace("\\'", "'").replace("\\\"", "\"")
    }

    @Test
    fun `every locale's QR step names the PC's Pair a phone button in that locale's words`() {
        for ((values, resx) in locales) {
            val button = pcButton(resx)
            assertTrue("$resx Home_PairPhoneButton is empty", button.isNotBlank())
            assertTrue(
                    "$values tutorial_page_qr_body must name the PC button \"$button\"",
                    qrBody(values).contains(button),
            )
        }
    }

    @Test
    fun `the English step no longer sends people to Show QR Code`() {
        assertFalse(qrBody("values").contains("Show QR Code"))
    }
}
