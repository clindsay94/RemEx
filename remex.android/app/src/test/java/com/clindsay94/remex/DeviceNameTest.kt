package com.clindsay94.remex

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins what this phone tells a PC about ITSELF at pairing — its name and its version.
 *
 * Two kinds of test live here, and the file is organised by the failure rather than by the class:
 *
 * The unit tests exercise [DeviceName.choose] only. `Build.MANUFACTURER` and `Settings.Global` have
 * no value under plain JUnit and this project has no Robolectric, which is exactly why the decision
 * was written as a pure function of its three inputs instead of reading the platform inline.
 *
 * The source-scanning guards cover the two pairing call sites, which pass the name and the version
 * in adjacent arguments and got the same treatment for the same reason. Every device used to send
 * the literal `"Android Client"` (RemEx-8m3r) and the literal `"2.0.0"` (RemEx-2zng); the first is
 * now stored and displayed by the PC, the second is logged by it. Neither wrong value had a symptom
 * anybody would notice, which is what a guard is for.
 */
class DeviceNameTest {

    @Test
    fun `the name the user typed wins over the hardware`() {
        // "Connor's Pixel" is what they will look for in a list on the PC. "SM-S938B" is the more
        // accurate answer and the less useful one.
        assertEquals(
            "Connor's Pixel",
            DeviceName.choose(userChosen = "Connor's Pixel", manufacturer = "Google", model = "Pixel 9 Pro"),
        )
    }

    @Test
    fun `a manufacturer already spelled out in the model is not repeated`() {
        // Google reports MODEL "Pixel 9 Pro" with MANUFACTURER "Google"; OnePlus reports
        // "OnePlus 12". Joining unconditionally would produce "Google Google Pixel 9 Pro".
        assertEquals(
            "Google Pixel 9 Pro",
            DeviceName.choose(null, manufacturer = "Google", model = "Google Pixel 9 Pro"),
        )
        assertEquals("OnePlus 12", DeviceName.choose(null, manufacturer = "OnePlus", model = "OnePlus 12"))
    }

    @Test
    fun `a brand the model spells differently is re-cased, not passed through`() {
        // vivo reports MANUFACTURER "vivo" with MODEL "vivo 1906"; older OnePlus reports "OnePlus"
        // with "ONEPLUS A6013". Passing those through renders one brand lower-case and another
        // shouting, right beside a joined "Samsung SM-S938B" — and shouting is the exact output the
        // capitalization rule exists to avoid, so the prefix branch has to obey it too.
        assertEquals("Vivo 1906", DeviceName.choose(null, manufacturer = "vivo", model = "vivo 1906"))
        assertEquals("OnePlus A6013", DeviceName.choose(null, manufacturer = "OnePlus", model = "ONEPLUS A6013"))

        // And a model already cased the way its maker reports it is untouched.
        assertEquals("Google Pixel 9 Pro", DeviceName.choose(null, "Google", "Google Pixel 9 Pro"))
    }

    @Test
    fun `a bare model code is joined to its maker`() {
        // Samsung reports MODEL "SM-S938B" with MANUFACTURER "samsung" — the model alone tells the
        // user nothing, and the lower-case make is how the platform actually reports it.
        assertEquals("Samsung SM-S938B", DeviceName.choose(null, manufacturer = "samsung", model = "SM-S938B"))
    }

    @Test
    fun `only the first letter of the maker is touched`() {
        // Upper-casing the whole string turns "OnePlus" into "ONEPLUS"; title-casing every word turns
        // it into "Oneplus". Manufacturers report themselves inconsistently and none of them want
        // either of those.
        assertEquals("OnePlus KB2003", DeviceName.choose(null, manufacturer = "OnePlus", model = "KB2003"))
        assertEquals("Xiaomi 2201123G", DeviceName.choose(null, manufacturer = "xiaomi", model = "2201123G"))
    }

    @Test
    fun `blank and whitespace sources are treated as absent`() {
        assertEquals("Pixel 9 Pro", DeviceName.choose(userChosen = "   ", manufacturer = "", model = "Pixel 9 Pro"))
        assertEquals("Pixel 9 Pro", DeviceName.choose(userChosen = null, manufacturer = "  ", model = "Pixel 9 Pro"))
    }

    @Test
    fun `a user-chosen name is trimmed rather than sent with its padding`() {
        assertEquals("Connor's Pixel", DeviceName.choose("  Connor's Pixel  ", "Google", "Pixel 9 Pro"))
    }

    @Test
    fun `something is always returned, because blank fails the pairing outright`() {
        // NOT A TIDINESS RULE. StartPairingNative rejects an empty client name with ArgMissing
        // before it opens a socket, so returning blank here does not produce an unnamed device — it
        // stops the user pairing at all, and tells them only that something was missing.
        assertEquals(DeviceName.FALLBACK, DeviceName.choose(null, null, null))
        assertEquals(DeviceName.FALLBACK, DeviceName.choose("", "", ""))
        assertEquals(DeviceName.FALLBACK, DeviceName.choose("  ", "  ", "  "))
    }

    @Test
    fun `every combination of present and absent sources yields a non-blank name`() {
        // Exhaustive rather than illustrative: this is the one property the pairing path depends on,
        // and the interesting failures are in the combinations nobody thought to write down.
        val values = listOf(null, "", "   ", "Connor's Pixel")
        val makes = listOf(null, "", "  ", "samsung", "Google")
        val models = listOf(null, "", "  ", "SM-S938B", "Google Pixel 9 Pro")

        for (user in values) {
            for (make in makes) {
                for (model in models) {
                    val name = DeviceName.choose(user, make, model)
                    assertTrue(
                        "choose($user, $make, $model) returned a blank name, which fails pairing",
                        name.isNotBlank(),
                    )
                    assertEquals("choose($user, $make, $model) returned untrimmed text", name.trim(), name)
                }
            }
        }
    }

    /** The repository root, so a source-scanning guard does not depend on the working directory. */
    private fun repoRoot(): File =
        System.getProperty("remex.repoRoot")?.let(::File)
            ?: File(".").absoluteFile.let { start ->
                generateSequence(start) { it.parentFile }
                    .firstOrNull { File(it, "remex.android").isDirectory }
            }
            ?: error("could not locate the repository root")

    @Test
    fun `no shipping code reports a hardcoded client version`() {
        // Same failure as the device name beside it, in the field next to it (RemEx-2zng). Both
        // pairing call sites passed the literal "2.0.0" while the app was 2.4, so the PC logged
        // every device paired since 2.1 as a 2.0 client — the one record you would reach for while
        // debugging a protocol or capability question, quietly wrong.
        //
        // Nothing on the host compares it: PairingHandler logs it, PairingMessages carries it, and
        // no code anywhere gates behaviour on it. So this is a truthfulness fix, and a truthfulness
        // fix has no symptom to notice when it regresses — which is what the guard is for.
        val root = repoRoot()
        val callSites =
            listOf(
                "remex.android/app/src/main/java/com/clindsay94/remex/ui/screens/PairingScreen.kt",
                "remex.android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt",
            )

        for (relative in callSites) {
            val file = File(root, relative)
            assertTrue("expected a pairing call site at $relative", file.isFile)

            // Comments stripped first: the fix's own explanation quotes the literal it replaced,
            // and a guard that trips on its own rationale is a guard nobody keeps.
            val code =
                file.readText().replace("\r\n", "\n")
                    .replace(Regex("""/\*[\s\S]*?\*/"""), "")
                    .replace(Regex("""//.*"""), "")

            // ANY version-shaped literal, not just the one that was there. Pinning it to "2.0.0"
            // is the trap the device-name guard next door already fell into: a DIFFERENT literal
            // defeats it, which the mutation test proves.
            val hardcoded = Regex(""""\d+\.\d+(\.\d+)?"""").find(code)
            assertTrue(
                "$relative hardcodes a client version (${hardcoded?.value}). Pass " +
                    "BuildConfig.VERSION_NAME so the host logs what the phone actually is.",
                hardcoded == null,
            )

            // Kept as well, but no longer the load-bearing half. On its own it has an expiry date:
            // four screens already render "v${'$'}{BuildConfig.VERSION_NAME}", so the day one of these
            // files displays the version too, this assertion is satisfied by the display string and
            // stops saying anything about the pairing argument.
            assertTrue(
                "$relative must pass BuildConfig.VERSION_NAME as the client version.",
                "BuildConfig.VERSION_NAME" in code,
            )
        }
    }

    @Test
    fun `version properties actually supplies a version`() {
        // BLANK IS NOT A DEGRADED VERSION, IT IS A BRICK. StartPairingNative rejects an empty
        // clientVersion with ArgMissing before it opens a socket, so a blank BuildConfig.VERSION_NAME
        // fails EVERY pairing on BOTH call sites. Before RemEx-2zng the argument was a literal and
        // could not be empty; now it is only as non-empty as version.properties happens to be.
        //
        // The gap is real rather than theoretical: build.gradle.kts uses
        // getProperty("versionName", "1.0.0"), and Properties returns the default only when the key
        // is ABSENT — a present-but-blank `versionName=` yields "" and builds happily.
        val properties = File(repoRoot(), "remex.android/app/version.properties")
        assertTrue("expected version.properties at ${properties.path}", properties.isFile)

        val versionName =
            properties.readLines()
                .firstOrNull { it.trimStart().startsWith("versionName=") }
                ?.substringAfter("versionName=")
                ?.trim()

        assertTrue(
            "version.properties has no usable versionName ($versionName). BuildConfig.VERSION_NAME " +
                "would be blank, and every pairing would fail with ArgMissing before opening a socket.",
            !versionName.isNullOrBlank(),
        )
    }

    @Test
    fun `no shipping code sends the old constant`() {
        // The constant lived at TWO call sites — the pairing screen and the automatic re-pair inside
        // RemexClientManager, which never shows that screen. Fixing only the visible one would have
        // left the name depending on which route the user happened to take, and the automatic path
        // is the one nobody watches. Scans the whole source tree so a third caller cannot reintroduce
        // it either.
        val root = repoRoot()

        val sources =
            File(root, "remex.android/app/src/main/java")
                .walkTopDown()
                .filter { it.isFile && it.extension == "kt" }
                .toList()

        assertTrue("expected Kotlin sources to scan", sources.size > 50)

        for (source in sources) {
            // COMMENTS STRIPPED FIRST, so that explaining what was replaced does not trip the guard.
            // No file is excluded by name instead: DeviceName.kt documents the old constant, and so
            // does the log line in RemexCoreClient.kt that stopped printing the name — excluding
            // those two files would leave real call sites inside them unscanned.
            val code =
                source.readText().replace("\r\n", "\n")
                    .replace(Regex("""/\*[\s\S]*?\*/"""), "")
                    .replace(Regex("""//.*"""), "")
            assertTrue(
                "${source.name} still sends the hardcoded \"Android Client\". Use " +
                    "DeviceName.forPairing(context) so every phone does not arrive as the same row.",
                "\"Android Client\"" !in code,
            )
        }

        // ABSENCE IS NOT PRESENCE. Everything above still passes if a call site is changed to some
        // OTHER literal, so the two known pairing entry points are also checked for the real call.
        for (relative in listOf(
            "remex.android/app/src/main/java/com/clindsay94/remex/ui/screens/PairingScreen.kt",
            "remex.android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt",
        )) {
            val file = File(root, relative)
            assertTrue("expected a pairing call site at $relative", file.isFile)
            assertTrue(
                "$relative must pass DeviceName.forPairing(...) as the client name.",
                "DeviceName.forPairing(" in file.readText(),
            )
        }
    }
}
