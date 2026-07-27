using System;

namespace Remex.Core.Services;

/// <summary>
/// The identity check a host performs before ending a process, shared by every platform's
/// <see cref="IProcessMonitorService"/> implementation.
/// </summary>
/// <remarks>
/// Kept here rather than duplicated per platform because the two implementations must agree: a rule
/// that is stricter on Windows than on Linux would mean the same confirmed action is honoured on one
/// host and refused on the other, which is worse than either behaviour on its own. (RemEx-druh.)
/// </remarks>
public static class ProcessKillGuard
{
    /// <summary>
    /// Whether the live process named <paramref name="actualName"/> is still the one the caller
    /// meant.
    /// </summary>
    /// <param name="expectedName">
    /// What the client believes is running under the PID, or null when it did not say — an older
    /// client, which is allowed through unchecked so this cannot brick a version it predates.
    /// </param>
    /// <remarks>
    /// The comparison is ORDINAL, case included, and that is deliberate in both directions.
    /// <list type="bullet">
    /// <item>
    /// The name round-trips: the client is echoing back a value the host itself produced from
    /// <c>Process.ProcessName</c> in the very same session, so an exact match is the normal case
    /// rather than a demanding one.
    /// </item>
    /// <item>
    /// Case-insensitivity would be wrong on Linux, where <c>Chrome</c> and <c>chrome</c> really are
    /// different programs — and this guard exists precisely to stop the wrong program being ended.
    /// </item>
    /// <item>
    /// The failure directions are not symmetric. A wrong refusal costs the user a second attempt; a
    /// wrong kill discards unsaved work with no undo. When the two rules disagree, the strict one is
    /// the safe one.
    /// </item>
    /// </list>
    /// <para>
    /// A blank <paramref name="expectedName"/> is treated as "not supplied" rather than as a name to
    /// match, because a client that sent an empty string would otherwise be unable to kill anything
    /// and the reason would not be obvious from the message.
    /// </para>
    /// </remarks>
    public static bool IsExpectedProcess(string? expectedName, string actualName)
        => string.IsNullOrWhiteSpace(expectedName)
            || string.Equals(expectedName, actualName, StringComparison.Ordinal);

    /// <summary>
    /// How far apart two readings of the same process's start time may be and still be the same
    /// process.
    /// </summary>
    /// <remarks>
    /// The value round-trips as an integer, so exact equality would *usually* hold — and testing for
    /// it anyway would be a trap. Hosts read the start time from different sources at different
    /// moments, and any rounding, clock adjustment or per-platform truncation between the listing
    /// and the kill would refuse a legitimate action permanently, with a message telling the user to
    /// refresh a list that will keep producing the same value. Two seconds is far below the window
    /// this guard defends (a program exiting and another being handed the same PID) and far above
    /// any plausible representation drift.
    /// </remarks>
    public const long StartTimeToleranceMs = 2_000;

    /// <summary>
    /// Whether a live process started close enough to when the client last saw it to be the same
    /// instance.
    /// </summary>
    /// <param name="expectedStartUnixMs">
    /// What the client recorded, or null when it did not say — an older client, or a process whose
    /// start time the host could not read. Unknown means UNCHECKED, never "refuse": this must not
    /// become a way to make protected processes unkillable, and it must not brick clients that
    /// predate the field.
    /// </param>
    /// <param name="actualStartUnixMs">
    /// What the live process reports now, or null when the host cannot read it at kill time. Also
    /// unchecked, for the same reason — a host that has lost the ability to read a start time has
    /// not learned anything about identity, so it must fall back to the name check rather than
    /// invent a refusal.
    /// </param>
    public static bool IsExpectedStartTime(long? expectedStartUnixMs, long? actualStartUnixMs)
        => expectedStartUnixMs is not long expected
            || actualStartUnixMs is not long actual
            || Math.Abs(expected - actual) <= StartTimeToleranceMs;

    /// <summary>
    /// The refusal shown when the PID no longer belongs to the program the user confirmed.
    /// </summary>
    /// <remarks>
    /// Names both programs on purpose. "Could not end the process" would leave the user retrying a
    /// confirmed action that will keep being refused; saying the number now belongs to something else
    /// tells them the list is stale and that refreshing is the way forward.
    /// </remarks>
    public static string MismatchMessage(int processId, string? expectedName, string actualName)
        => $"Process {processId} is no longer \"{expectedName}\" — it is now \"{actualName}\". " +
           "Nothing was ended. Refresh the process list and try again.";
}
