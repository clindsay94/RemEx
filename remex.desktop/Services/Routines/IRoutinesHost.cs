using Remex.Core.Routines;

namespace Remex.Desktop.Services.Routines;

/// <summary>One stored PC-run routine as the PC Routines page shows it (routines spec §2.3, §7.4.5).</summary>
/// <param name="Routine">The definition the owner phone synced. Read-only on the PC (D8).</param>
/// <param name="DisabledOnPc">Switched off by the person at the PC (<c>pcDisabled</c>).</param>
/// <param name="Running">A run of it is active right now.</param>
public sealed record RoutineHostEntry(Routine Routine, bool DisabledOnPc, bool Running, bool WaitingForSensor = false);

/// <summary>One owner phone's routines on this PC.</summary>
/// <param name="ClientId">The owner's paired client id. Never shown; pass it back to the mutators.</param>
/// <param name="PhoneName">The phone's display name, or null when none is on file.</param>
/// <param name="Paused">The phone's own Pause all, applied here ("Paused from &lt;phone&gt;", D4).</param>
/// <param name="BlockedByPc">The person at the PC blocked this phone's routines.</param>
/// <param name="Suspended">Owner absent for 30 days (§7.4.4); the next sync lifts it.</param>
/// <param name="LastSeenUnixMs">When the phone last synced, for "Last seen" on the page.</param>
/// <param name="Routines">Its stored routines, in the phone's order.</param>
public sealed record RoutineOwnerView(
    string ClientId,
    string? PhoneName,
    bool Paused,
    bool BlockedByPc,
    bool Suspended,
    long LastSeenUnixMs,
    IReadOnlyList<RoutineHostEntry> Routines);

/// <summary>Everything the PC Routines page needs in one read.</summary>
/// <param name="HostPaused">PC-side Pause all (every owner, automatic sources only, §8.7).</param>
/// <param name="DryRun">The process was started with <c>--routines-dry-run</c>; show the banner.</param>
/// <param name="IdleSource">The idle source in use (§8.5.2), or null when this PC has none.</param>
/// <param name="SessionSource">The lock/unlock source in use (§8.5.3), or null when this PC has none.</param>
/// <param name="Owners">One entry per phone with routines on this PC.</param>
/// <param name="Warnings">Localized problems the page should show (an unreadable store, T14).</param>
public sealed record RoutinesHostSnapshot(
    bool HostPaused,
    bool DryRun,
    string? IdleSource,
    string? SessionSource,
    IReadOnlyList<RoutineOwnerView> Owners,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The PC-run routines backend as the PC Routines page sees it (routines S4a, RemEx-pp0rt.9).
/// </summary>
/// <remarks>
/// <para>
/// Declared here, implemented in <c>remex.agent</c> (<c>RoutineHostService</c>), for the same reason as
/// <see cref="IRoutineUi"/>: the desktop cannot name the host's types without a project-reference cycle.
/// </para>
/// <para>
/// <b>THE PC NEVER EDITS A ROUTINE (D8).</b> It can switch one off, pause everything, block a phone and
/// run one now. Every mutator persists before it returns and, when the owner phone is connected, sends it
/// an unsolicited <c>routine_sync_result</c> so the phone shows the PC's state at once (R-UX-37).
/// </para>
/// </remarks>
public interface IRoutinesHost
{
    /// <summary>Raised on any change the page shows: sync, toggle, pause, block, run start or end.</summary>
    /// <remarks>Raised on a background thread; marshal to the UI thread before touching controls.</remarks>
    event EventHandler? Changed;

    /// <summary>The current state.</summary>
    RoutinesHostSnapshot GetSnapshot();

    /// <summary>The PC-side history, newest first, optionally narrowed to one owner and/or routine.</summary>
    IReadOnlyList<RoutineRun> GetHistory(string? ownerClientId = null, string? routineId = null);

    /// <summary>PC Pause all. Pausing cancels a countdown in progress (<c>cancelled_on_pc</c>).</summary>
    Task SetHostPausedAsync(bool paused);

    /// <summary>Switches one routine off (or back on) on this PC only; the phone's own flag is untouched.</summary>
    Task SetDisabledOnPcAsync(string ownerClientId, string routineId, bool disabled);

    /// <summary>Blocks (or unblocks) every routine of one phone on this PC (<c>blocked_by_pc</c>).</summary>
    Task SetBlockedAsync(string ownerClientId, bool blocked);

    /// <summary>
    /// PC Run now. Returns the run record as it started, or the skipped record when it could not start;
    /// the run itself continues in the background.
    /// </summary>
    /// <param name="ownerClientId">The routine's owner.</param>
    /// <param name="routineId">The routine.</param>
    /// <param name="presenceConfirmed">
    /// <b>IN-PROCESS ONLY (T21).</b> True only when the person at the PC confirmed a destructive routine in
    /// <c>ConfirmationDialogHost</c>. It skips the 15 s countdown. It has no wire representation, and a
    /// confirmation dialog that cannot show must pass false (fail closed).
    /// </param>
    Task<RoutineRun> RunNowAsync(string ownerClientId, string routineId, bool presenceConfirmed);

    /// <summary>Cancels a running host run from the PC (<c>cancelled_on_pc</c>). False when none matched.</summary>
    bool CancelRun(string runId);
}
