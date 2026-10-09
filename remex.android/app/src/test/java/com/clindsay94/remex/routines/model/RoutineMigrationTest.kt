package com.clindsay94.remex.routines.model

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

class RoutineMigrationTest {

    private val routineA = Routine(id = "a", name = "Game time", steps = listOf(RoutineStep(type = "power", verb = "LOCK")))
    private val routineB = Routine(id = "b", name = "Unknown kind", trigger = RoutineTrigger(type = "future.trigger"))

    @Test
    fun nullDocument_isEmptyFallbackAtCurrentVersion() {
        val result = RoutineMigration.migrate(null)

        assertEquals(RoutineSchema.CURRENT_VERSION, result.set.schemaVersion)
        assertEquals(emptyList<Routine>(), result.set.routines)
        assertTrue(result.isFallback)
        assertFalse(result.isNewerThanReader)
        assertNull(result.warning)
    }

    @Test
    fun newerDocument_isReturnedUntouchedAndFlagged() {
        val set = RoutineSet(schemaVersion = RoutineSchema.CURRENT_VERSION + 1, routines = listOf(routineA))

        val result = RoutineMigration.migrate(set)

        assertSame(set, result.set)
        assertEquals(RoutineSchema.CURRENT_VERSION + 1, result.set.schemaVersion)
        assertEquals(RoutineReasonCodes.SCHEMA_TOO_NEW, result.warning)
        assertTrue(result.isNewerThanReader)
        assertFalse(result.isFallback)
    }

    @Test
    fun currentDocument_passesThroughWithNoFlags() {
        val set = RoutineSet(schemaVersion = RoutineSchema.CURRENT_VERSION, routines = listOf(routineA, routineB))

        val result = RoutineMigration.migrate(set)

        assertSame(set, result.set)
        assertNull(result.warning)
        assertFalse(result.isFallback)
        assertFalse(result.isNewerThanReader)
    }

    @Test
    fun versionZeroWithoutRoutines_isStampedWithEmptyList() {
        val result = RoutineMigration.migrate(RoutineSet(schemaVersion = 0, routines = null))

        assertEquals(RoutineSchema.CURRENT_VERSION, result.set.schemaVersion)
        assertEquals(emptyList<Routine>(), result.set.routines)
        assertFalse(result.isFallback)
        assertFalse(result.isNewerThanReader)
        assertNull(result.warning)
    }

    @Test
    fun olderDocument_keepsTheSameRoutineObjectsInOrder() {
        val result = RoutineMigration.migrate(RoutineSet(schemaVersion = 0, routines = listOf(routineA, routineB)))

        val routines = result.set.routines!!
        assertEquals(RoutineSchema.CURRENT_VERSION, result.set.schemaVersion)
        assertEquals(2, routines.size)
        assertSame(routineA, routines[0])
        assertSame(routineB, routines[1])
        assertFalse(result.isFallback)
    }
}
