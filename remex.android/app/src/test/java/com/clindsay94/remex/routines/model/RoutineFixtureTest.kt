package com.clindsay94.remex.routines.model

import com.clindsay94.remex.security.HostIdentity
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test

/**
 * The Kotlin mirror reaches the same verdicts as C# on the shared fixtures (routines spec §13.1,
 * §13.3 `RoutineFixtureTest`), and the parity lists match (RemEx-pp0rt.3).
 */
class RoutineFixtureTest {

    @Test
    fun everyValidationFixtureReachesItsExpectedVerdict() {
        val cases = RoutineFixtures.validation
        assertTrue("only ${cases.size} validation fixtures - was the manifest emptied?", cases.size > 30)

        val failures =
            cases.mapNotNull { (file, expected) ->
                val set = RoutineJson.parseSet(RoutineFixtures.text(file))
                val actual = RoutineValidator.validateSet(set).firstFailure
                if (actual == expected) null else "$file: expected $expected, got $actual"
            }

        assertTrue(failures.joinToString("\n"), failures.isEmpty())
    }

    @Test
    fun theReasonCodeListEqualsTheSharedFixtureInOrder() {
        val array = RoutineFixtures.json("reason-codes.json").getJSONArray("codes")
        val fixture = (0 until array.length()).map { array.getString(it) }

        assertEquals(fixture, RoutineReasonCodes.ALL)
        assertEquals(73, RoutineReasonCodes.ALL.size)
        assertEquals(RoutineReasonCodes.ALL.size, RoutineReasonCodes.ALL.toSet().size)
    }

    @Test
    fun hostIdentityMatchesTheSharedVectors() {
        // The C# port (remex.core/Security/HostIdentity.cs) asserts the same file: a routine is bound
        // to a PC by this key, and a disagreement would make the PC refuse every routine as wrong_pc.
        val vectors = RoutineFixtures.json("host-identity-vectors.json").getJSONArray("vectors")
        assertTrue(vectors.length() >= 8)
        for (i in 0 until vectors.length()) {
            val vector = vectors.getJSONObject(i)
            val pin = if (vector.isNull("pin")) null else vector.getString("pin")
            val key = if (vector.isNull("key")) null else vector.getString("key")
            assertEquals("pin <$pin>", key, HostIdentity.keyFor(pin))
        }
    }

    @Test
    fun theFixtureCopyMatchesTheCSharpCopyWhenTheRepoIsAvailable() {
        // The C# RoutineFixtureParityTests is the authoritative drift guard; this is the same check
        // from this side, skipped when the test JVM was not handed the repo root.
        val root = System.getProperty("remex.repoRoot")?.let(::File)
        assumeTrue(root != null && File(root, "remex.core.tests/Fixtures/Routines").isDirectory)
        val csDir = File(root, "remex.core.tests/Fixtures/Routines")
        val ktDir = File(root, "remex.android/app/src/test/resources/routines")

        val csFiles = csDir.listFiles()!!.map { it.name }.sorted()
        assertEquals(csFiles, ktDir.listFiles()!!.map { it.name }.sorted())
        for (name in csFiles) {
            assertTrue("$name differs", File(csDir, name).readBytes().contentEquals(File(ktDir, name).readBytes()))
        }
    }

    @Test
    fun theSpecExampleParsesIntoTypedRoutines() {
        val set = RoutineJson.parseSet(RoutineFixtures.text("spec-example.valid.json"))
        assertNotNull(set)
        val game = set!!.routines!![0]
        assertEquals(RoutineTriggerTypes.HOME_ARRIVE, game.trigger!!.type)
        assertEquals("0A:1B:2C:3D:4E:5F", game.steps!![0]!!.mac)
        assertEquals(120, game.steps!![1]!!.timeoutSeconds)
        assertEquals(RoutinePowerVerbs.SLEEP, set.routines!![1].steps!![1]!!.verb)
    }
}
