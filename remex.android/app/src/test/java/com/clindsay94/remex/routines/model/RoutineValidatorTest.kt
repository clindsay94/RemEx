package com.clindsay94.remex.routines.model

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Kotlin mirror rows that the shared fixtures do not pin on their own (routines spec §13.3
 * `RoutineValidatorTest`, RemEx-pp0rt.3). The fixtures prove the two validators agree; these prove
 * the mirror's own edges (D1, D5, budgets, strict JSON reading, migration).
 */
class RoutineValidatorTest {

    private fun routine(trigger: RoutineTrigger = RoutineTrigger(type = RoutineTriggerTypes.MANUAL), vararg steps: RoutineStep) =
        Routine(
            id = "3f0c2a4e-7b1d-4c55-9a60-2d7e8f1b9c10",
            name = "Test",
            hostIdentity = "9f2c4be07a1d33e5",
            enabled = true,
            revision = 1,
            trigger = trigger,
            steps = if (steps.isEmpty()) listOf(RoutineStep(type = "power", verb = "LOCK")) else steps.toList(),
        )

    private val pcIdle = RoutineTrigger(type = RoutineTriggerTypes.PC_IDLE, idleMinutes = 10)

    private fun code(r: Routine) = RoutineValidator.validateRoutine(r).reasonCode

    @Test
    fun theDestructiveSetIsExactlyD1() {
        assertEquals(
            listOf("SHUTDOWN", "FORCESHUTDOWN", "RESTART", "FORCERESTART", "RESTARTTOUEFI", "SIGNOUT", "SLEEP", "HIBERNATE").sorted(),
            RoutinePowerVerbs.DESTRUCTIVE.sorted(),
        )
        assertFalse(RoutinePowerVerbs.isDestructive("LOCK"))
        assertFalse(RoutinePowerVerbs.isDestructive("MONITOROFF"))
        assertEquals(10, RoutinePowerVerbs.ALL.size)
    }

    @Test
    fun wakeOnLanIsReservedAndRejected() {
        assertFalse(RoutinePowerVerbs.RESERVED_WAKE_ON_LAN in RoutinePowerVerbs.ALL)
        assertEquals(RoutineReasonCodes.INVALID_FIELD, code(routine(steps = arrayOf(RoutineStep(type = "power", verb = "WAKEONLAN")))))
    }

    @Test
    fun destructiveStepsAppearAtMostOnceAndLastOnThePc() {
        val sleep = RoutineStep(type = "power", verb = "SLEEP")
        val delay = RoutineStep(type = "delay", seconds = 1)
        assertEquals(RoutineReasonCodes.DESTRUCTIVE_NOT_LAST, code(routine(pcIdle, sleep, delay)))
        assertEquals(RoutineReasonCodes.OK, code(routine(pcIdle, delay, sleep)))
        assertEquals(RoutineReasonCodes.OK, code(routine(steps = arrayOf(sleep, delay))))
        assertEquals(
            RoutineReasonCodes.TOO_MANY_DESTRUCTIVE,
            code(routine(pcIdle, sleep, RoutineStep(type = "power", verb = "HIBERNATE"))),
        )
    }

    @Test
    fun budgetsMatchTheCSharpFormulas() {
        val launch = RoutineStep(type = "launchApp", appId = "c1d2e3f4-0000-4000-8000-00000000abcd")
        val wait = RoutineStep(type = "waitOnline")
        val delay = RoutineStep(type = "delay", seconds = 10)
        val sleep = RoutineStep(type = "power", verb = "SLEEP")
        assertEquals(10 + 300 + 60 + 60 + 15, RoutineValidator.budgetSeconds(routine(steps = arrayOf(delay, wait, launch, sleep))))
        assertEquals(10 + 30 * 2 + 15, RoutineValidator.budgetSeconds(routine(pcIdle, delay, sleep)))
    }

    @Test
    fun nameLengthCountsUserPerceivedCharacters() {
        val name = "e\u0301".repeat(40)
        assertTrue(RoutineValidator.validateRoutine(routine().copy(name = name)).isValid)
        assertFalse(RoutineValidator.validateRoutine(routine().copy(name = name + "e")).isValid)
    }

    @Test
    fun namesRejectControlCharactersAndLineBreaks() {
        listOf("a\u2028b", "a\u0085b", "a\tb").forEach {
            assertEquals(it, RoutineReasonCodes.INVALID_FIELD, code(routine().copy(name = it)))
        }
    }

    @Test
    fun readingIsStrictAboutJsonTypes() {
        // C# would refuse each of these, so the mirror must too, or the two sides disagree.
        fun malformed(stepJson: String) =
            RoutineJson.readRoutine(
                org.json.JSONObject(
                    """{"id":"3f0c2a4e-7b1d-4c55-9a60-2d7e8f1b9c10","steps":[$stepJson]}""",
                ),
            ).isMalformed

        assertTrue(malformed("""{"type":"delay","seconds":"5"}"""))
        assertTrue(malformed("""{"type":"delay","seconds":5.5}"""))
        assertTrue(malformed("""{"type":"delay","seconds":true}"""))
        assertTrue(malformed("""{"type":7}"""))
        assertTrue(malformed("5"))
        assertFalse(malformed("""{"type":"delay","seconds":5}"""))
        assertFalse(malformed("""{"type":"delay","seconds":null}"""))
        assertFalse(malformed("null"))
    }

    @Test
    fun aWholeNumberWrittenAsADecimalReadsAsThatInteger() {
        // The one place the mirror accepts what C# refuses. Android's org.json writes the double
        // 5.0 back out as "5", so rejecting it would turn a malformed routine valid across a save
        // and reload (RemEx-3dvre). A fractional value stays malformed (readingIsStrictAboutJsonTypes).
        fun read(stepJson: String) =
            RoutineJson.readRoutine(
                org.json.JSONObject(
                    """{"id":"3f0c2a4e-7b1d-4c55-9a60-2d7e8f1b9c10","revision":2.0,"steps":[$stepJson]}""",
                ),
            )

        val routine = read("""{"type":"delay","seconds":5.0}""")
        assertFalse(routine.isMalformed)
        assertEquals(5, routine.steps.orEmpty().single()?.seconds)
        assertEquals(2L, routine.revision.toLong())
        assertTrue(read("""{"type":"delay","seconds":1.0E20}""").isMalformed)
    }

    @Test
    fun aMalformedRoutineKeepsItsIdForTheRejection() {
        val r = RoutineJson.readRoutine(org.json.JSONObject("""{"id":"abc","enabled":"yes"}"""))
        assertTrue(r.isMalformed)
        assertEquals("abc", r.id)
        assertEquals(RoutineReasonCodes.INVALID_FIELD, RoutineValidator.validateRoutine(r).reasonCode)
    }

    @Test
    fun aMalformedRoutineIsWrittenBackExactlyAsItArrived() {
        // A store round trip must never replace the user's routine with the lossy placeholder.
        val original =
            """{"schemaVersion":1,"routines":[{"id":"7c2e1b90-4d5f-4a3b-b6c7-8d9e0f1a2b3c","name":5,"futureField":{"a":[1,2]}},17,null]}"""
        val set = RoutineJson.parseSet(original)!!
        assertTrue(set.routines!!.all { it.isMalformed })

        val written = RoutineJson.write(set)
        assertTrue(written.toString(), RoutineFixtures.treeEquals(org.json.JSONObject(original), written))
        assertEquals("7c2e1b90-4d5f-4a3b-b6c7-8d9e0f1a2b3c", RoutineJson.write(set.routines!![0]).getString("id"))
    }

    @Test
    fun aMalformedRoutineWithNoOriginalJsonCannotBeWrittenSilently() {
        val placeholder = Routine(id = "x", isMalformed = true)
        assertTrue(runCatching { RoutineJson.write(placeholder) }.isFailure)
        assertTrue(runCatching { RoutineJson.write(RoutineSet(1, listOf(placeholder))) }.isFailure)
    }

    @Test
    fun migrationStampsV1AndNeverStampsDown() {
        val fallback = RoutineMigration.migrate(null)
        assertTrue(fallback.isFallback)
        assertEquals(RoutineSchema.CURRENT_VERSION, fallback.set.schemaVersion)

        val stamped = RoutineMigration.migrate(RoutineSet(schemaVersion = 0))
        assertFalse(stamped.isFallback)
        assertEquals(1, stamped.set.schemaVersion)

        val newer = RoutineSet(schemaVersion = 2, routines = emptyList())
        val kept = RoutineMigration.migrate(newer)
        assertTrue(kept.isNewerThanReader)
        assertEquals(RoutineReasonCodes.SCHEMA_TOO_NEW, kept.warning)
        assertEquals(newer, kept.set)

        val current = RoutineMigration.migrate(RoutineSet(schemaVersion = 1, routines = emptyList()))
        assertNull(current.warning)
    }

    @Test
    fun unknownTypesSurviveMigrationAndAreRefusedByTheValidator() {
        val odd = routine(RoutineTrigger(type = "pc.schedule"))
        val result = RoutineMigration.migrate(RoutineSet(schemaVersion = 0, routines = listOf(odd)))
        assertEquals(listOf(odd), result.set.routines)
        assertEquals(RoutineReasonCodes.UNSUPPORTED_TRIGGER, code(odd))
    }
}
