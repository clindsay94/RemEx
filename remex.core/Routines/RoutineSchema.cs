namespace Remex.Core.Routines;

/// <summary>
/// The routine schema version (routines spec §6.7, RemEx-pp0rt.3).
/// </summary>
/// <remarks>
/// <para>
/// Mirrored in Kotlin as <c>RoutineSchema.CURRENT_VERSION</c>. The phone store, the export file and
/// every <c>routines_sync</c> carry it.
/// </para>
/// <para>
/// <b>Any additive field that an older validator would reject with <c>field_not_allowed</c> needs
/// <c>CurrentVersion + 1</c> and a migration arm</b> (<see cref="RoutineMigration"/>), even when the
/// arm is a pure stamp: an older reader must see "newer than me" and refuse to edit, not reject a
/// routine it simply does not understand yet.
/// </para>
/// </remarks>
public static class RoutineSchema
{
    /// <summary>The version this build reads and writes.</summary>
    public const int CurrentVersion = 1;
}
