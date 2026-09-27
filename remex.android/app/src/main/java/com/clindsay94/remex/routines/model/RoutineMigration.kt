package com.clindsay94.remex.routines.model

/**
 * The outcome of [RoutineMigration.migrate]. Mirrors C# `RoutineMigrationResult`.
 *
 * @property isFallback [set] was synthesised because there was no document. A store must NEVER
 *   persist it: writing it back turns a transient read failure into permanent loss of the user's
 *   routines (REGRESSION-GUARDS.md, "a fallback profile must never be persisted").
 * @property isNewerThanReader the document is from a newer RemEx: show it, block editing.
 */
data class RoutineMigrationResult(
    val set: RoutineSet,
    val warning: String?,
    val isFallback: Boolean,
    val isNewerThanReader: Boolean,
)

/**
 * Brings a routine document up to [RoutineSchema.CURRENT_VERSION] (routines spec §6.7). Mirrors C#
 * `RoutineMigration.Migrate`: arms run against the ARRIVAL version, the stamp happens once at the
 * end, a newer document is never stamped down, and routines (unknown types included) are carried
 * through untouched for the validator to judge.
 */
object RoutineMigration {
    fun migrate(set: RoutineSet?): RoutineMigrationResult {
        if (set == null) {
            return RoutineMigrationResult(
                RoutineSet(schemaVersion = RoutineSchema.CURRENT_VERSION, routines = emptyList()),
                warning = null,
                isFallback = true,
                isNewerThanReader = false,
            )
        }
        if (set.schemaVersion > RoutineSchema.CURRENT_VERSION) {
            return RoutineMigrationResult(set, RoutineReasonCodes.SCHEMA_TOO_NEW, isFallback = false, isNewerThanReader = true)
        }
        if (set.schemaVersion == RoutineSchema.CURRENT_VERSION) {
            return RoutineMigrationResult(set, warning = null, isFallback = false, isNewerThanReader = false)
        }
        // Version 0 = absent. v1 is the first schema, so the only arm is the stamp; future arms go
        // here, each guarded by `< N` on the arrival version.
        return RoutineMigrationResult(
            set.copy(schemaVersion = RoutineSchema.CURRENT_VERSION, routines = set.routines ?: emptyList()),
            warning = null,
            isFallback = false,
            isNewerThanReader = false,
        )
    }
}
