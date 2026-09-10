namespace Remex.Desktop.Services;

/// <summary>One entry in the staging drawer, reduced to what the ordering rule needs.</summary>
/// <param name="Identity">The sensor's stable identity — the host-stamped id, or its name.</param>
/// <param name="Title">What the drawer shows, and the tie-break within a group.</param>
public readonly record struct StagedSensor(string Identity, string Title);

/// <summary>
/// Which staged sensors the PC is still hearing about, and what order to show them in (RemEx-yqpa).
/// </summary>
/// <remarks>
/// <para>
/// **THE DRAWER ONLY EVER GREW.** A staged template is created the first time a sensor is seen and
/// nothing ever removed it — <c>ReturnToStaging</c> deliberately leaves templates in place and
/// <c>PlaceFromStaging</c> clones rather than moves — so over a session the drawer filled with
/// overlapping, partly-stale sensor sets the user had to scroll past.
/// </para>
/// <para>
/// **STALE IS MARKED, NOT DELETED, AND THAT IS A RECORDED DECISION RATHER THAN A PREFERENCE.**
/// <c>WindowsTelemetryService</c> alternates its sensor set by category between HWiNFO and
/// WindowsPerf, so the union seen across a session is larger than any single tick reports — which is
/// exactly why time-based eviction is wrong here: entries would flicker out and back in as the source
/// rotated. Keeping them is also what makes this incapable of orphaning a <c>SensorViewModel</c> that
/// a placed card still binds to, which deleting would have had to reason about.
/// </para>
/// <para>
/// **NOT A PERFORMANCE FIX, AND THAT WAS MEASURED BEFORE IT WAS RULED OUT.** The per-tick cost is
/// 0.087–0.139 ms at 250 sensors and 0.272 ms at 500 — linear and negligible. The defect is that the
/// drawer becomes hard to use, and the bead is explicit that it must not be re-opened as a perf issue.
/// </para>
/// </remarks>
public static class StagingDrawerFreshness
{
    /// <summary>Whether a staged entry was absent from the tick that just arrived.</summary>
    public static bool IsStale(string identity, IReadOnlySet<string> seenThisTick) =>
        !seenThisTick.Contains(identity);

    /// <summary>
    /// The order to show the drawer in: everything still being reported first, then the rest.
    /// </summary>
    /// <remarks>
    /// **ALPHABETICAL WITHIN EACH GROUP RATHER THAN INSERTION ORDER.** Insertion order is first-sight
    /// order, which is arrival order from the host — meaningless to the person reading the list, and
    /// unstable across sessions because the source that answers first varies. Sorting by title means
    /// the same PC shows the same drawer twice running, which is the property that makes a list
    /// scannable at all.
    /// </remarks>
    public static IReadOnlyList<StagedSensor> Order(
        IEnumerable<StagedSensor> entries, IReadOnlySet<string> seenThisTick) =>
        entries
            .OrderBy(e => IsStale(e.Identity, seenThisTick))
            // INVARIANT, NOT CURRENT-CULTURE. Current culture makes the same host sort differently on
            // Windows and CachyOS - different collation backends - and makes this rule's own test
            // depend on the machine that runs it, which is how a tr-TR runner fails a suite nobody
            // changed. Sensor labels are ASCII-ish host strings; invariant reads the same everywhere.
            .ThenBy(e => e.Title, StringComparer.InvariantCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// Whether the drawer's order would actually change, so the caller can leave it alone if not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **AN EARLY EXIT, NOT A CORRECTNESS GUARD, AND MUTATION TESTING IS WHY THAT IS STATED.** It was
    /// written when the caller rebuilt the collection with Clear-and-refill, where skipping a no-op
    /// tick genuinely mattered: a reset rebuilds every row, restarting entrance animations, dropping
    /// the scroll position and cancelling a drag out of the drawer mid-gesture. The caller now moves
    /// items in place instead, and a reorder that changes nothing issues zero moves on its own — so
    /// removing this check kills no test, and it is kept for the allocation and the loop rather than
    /// because the drawer would misbehave without it.
    /// </para>
    /// <para>
    /// Comparing identity by position rather than set membership on purpose: two drawers holding the
    /// same sensors in different orders are a change, and that is the whole question being asked.
    /// </para>
    /// </remarks>
    public static bool OrderWouldChange(
        IReadOnlyList<StagedSensor> current, IReadOnlyList<StagedSensor> desired)
    {
        if (current.Count != desired.Count) return true;

        for (var i = 0; i < current.Count; i++)
        {
            if (!string.Equals(current[i].Identity, desired[i].Identity, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
