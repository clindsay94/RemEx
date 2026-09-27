namespace Remex.Core.Routines;

/// <summary>The outcome of <see cref="RoutineMigration.Migrate"/>.</summary>
/// <param name="Set">The document at <see cref="RoutineSchema.CurrentVersion"/>, or untouched when newer.</param>
/// <param name="Warning">A reason code when something needs surfacing, else null.</param>
/// <param name="IsFallback">
/// True when <see cref="Set"/> was SYNTHESISED because there was no document. A store must never
/// persist a fallback (REGRESSION-GUARDS.md "a fallback profile must never be persisted"): writing it
/// back would turn a transient read failure into permanent loss of the user's routines.
/// </param>
/// <param name="IsNewerThanReader">
/// The document is from a newer RemEx. Read-only on the phone ("made by a newer RemEx"); the host
/// rejects the whole sync with <c>schema_too_new</c>.
/// </param>
public readonly record struct RoutineMigrationResult(
    RoutineSet Set,
    string? Warning,
    bool IsFallback,
    bool IsNewerThanReader);

/// <summary>
/// Brings a routine document up to <see cref="RoutineSchema.CurrentVersion"/> (routines spec §6.7).
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <c>CustomizationMigration.Migrate</c>: arms run in order against the version the
/// document ARRIVED with, an arm does not stamp, and the stamp happens once at the end. A document
/// from the future is never stamped down.
/// </para>
/// <para>
/// Routines are carried through untouched - including ones whose trigger or step type this version
/// does not know. Those stay in the set (so a downgrade-then-upgrade round trip loses nothing) and are
/// refused by <see cref="RoutineValidator"/> with <c>unsupported_trigger</c> / <c>unsupported_step</c>.
/// </para>
/// <para>Mirrored in Kotlin by <c>RoutineMigration.kt</c>.</para>
/// </remarks>
public static class RoutineMigration
{
    public static RoutineMigrationResult Migrate(RoutineSet? set)
    {
        if (set is null)
        {
            return new RoutineMigrationResult(
                new RoutineSet { SchemaVersion = RoutineSchema.CurrentVersion, Routines = [] },
                Warning: null,
                IsFallback: true,
                IsNewerThanReader: false);
        }

        if (set.SchemaVersion > RoutineSchema.CurrentVersion)
        {
            return new RoutineMigrationResult(set, RoutineReasonCodes.SchemaTooNew, IsFallback: false, IsNewerThanReader: true);
        }

        if (set.SchemaVersion == RoutineSchema.CurrentVersion)
        {
            return new RoutineMigrationResult(set, Warning: null, IsFallback: false, IsNewerThanReader: false);
        }

        // Version 0 = absent: a document written before the field existed. v1 is the first schema, so
        // the only arm is the stamp. Future arms go here, each guarded by `< N` on the ARRIVAL version.
        var migrated = set;
        return new RoutineMigrationResult(
            migrated with { SchemaVersion = RoutineSchema.CurrentVersion, Routines = migrated.Routines ?? [] },
            Warning: null,
            IsFallback: false,
            IsNewerThanReader: false);
    }
}
