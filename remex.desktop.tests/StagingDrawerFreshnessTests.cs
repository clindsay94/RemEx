using Remex.Desktop.Services;

namespace Remex.Desktop.Tests;

/// <summary>
/// The staging drawer marks what has gone quiet and keeps it (RemEx-yqpa).
/// </summary>
/// <remarks>
/// The decision these pin is recorded rather than derived: GROUP AND MARK STALE, do not delete.
/// Eviction is the obvious fix and it is the wrong one here, because the host alternates its sensor
/// set by category — so entries would flicker out and back in as the source rotated, and a drawer
/// that loses rows while you are reaching for one is worse than a long drawer.
/// </remarks>
public class StagingDrawerFreshnessTests
{
    private static IReadOnlySet<string> Seen(params string[] ids) => new HashSet<string>(ids, StringComparer.Ordinal);

    private static StagedSensor S(string id, string? title = null) => new(id, title ?? id);

    [Fact]
    public void AnythingMissingFromTheTickThatJustArrivedIsStale()
    {
        var seen = Seen("cpu", "gpu");

        Assert.False(StagingDrawerFreshness.IsStale("cpu", seen));
        Assert.True(StagingDrawerFreshness.IsStale("fan", seen));
    }

    [Fact]
    public void AnEmptyTickMakesEVERYTHINGStaleRatherThanNothing()
    {
        // The direction matters and the inverse is a plausible mistake: "nothing was seen, so leave
        // everything as it was" is what a null-guard written in a hurry does. A host that stopped
        // reporting is exactly when the drawer should say so.
        Assert.True(StagingDrawerFreshness.IsStale("cpu", Seen()));
    }

    [Fact]
    public void LiveSensorsComeFirstAndTheQuietOnesSinkRatherThanVanish()
    {
        // BOTH HALVES. Sinking is the requirement; still being THERE is the decision - nothing is
        // dropped, so a placed card's shared SensorViewModel cannot be orphaned by this.
        var ordered = StagingDrawerFreshness.Order(
            [S("fan"), S("cpu"), S("gpu")], Seen("cpu", "gpu"));

        Assert.Equal(["cpu", "gpu", "fan"], ordered.Select(e => e.Identity));
    }

    [Fact]
    public void WithinEachGroupTheOrderIsByTitle()
    {
        // NOT INSERTION ORDER, which is first-sight order - arrival order from the host, meaningless
        // to a reader and different next session because the source that answers first varies.
        var ordered = StagingDrawerFreshness.Order(
            [S("b", "Zulu"), S("a", "Alpha"), S("d", "Yankee"), S("c", "Bravo")],
            Seen("a", "b"));

        Assert.Equal(["Alpha", "Zulu", "Bravo", "Yankee"], ordered.Select(e => e.Title));
    }

    [Fact]
    public void OrderingIsCaseInsensitiveSoASensorDoesNotSortByItsCapitalisation()
    {
        // Host sensor labels are arbitrary - "cpu Package" and "CPU Fan" come from the same machine.
        // An ordinal sort puts every capitalised name above every lowercase one, which reads as
        // random to someone scanning for a name.
        var ordered = StagingDrawerFreshness.Order(
            [S("1", "cpu Package"), S("2", "CPU Fan")], Seen("1", "2"));

        Assert.Equal(["CPU Fan", "cpu Package"], ordered.Select(e => e.Title));
    }

    [Fact]
    public void TheDrawerIsLeftALONEWhenNothingAboutTheOrderChanged()
    {
        // **THE ASSERTION THAT KEEPS THIS FROM BEING WORSE THAN THE BUG.** Telemetry arrives about
        // once a second. Rewriting the collection every tick resets the item containers, restarts the
        // entrance animations, drops the scroll position, and cancels a drag out of the drawer as its
        // source vanishes mid-gesture. On a steady host the answer here is "no change", forever.
        // COMPARED AGAINST A REBUILT ORDER, NOT AGAINST ITSELF. The first version passed the same
        // reference twice, which can only fail if OrderWouldChange is outright broken - it says
        // nothing about whether ordering is idempotent, which is the property the claim rests on.
        // Make Order non-idempotent, by any means, and this fails where the old one did not.
        var seen = Seen("cpu", "gpu");
        var current = StagingDrawerFreshness.Order([S("gpu"), S("cpu")], seen);
        var again = StagingDrawerFreshness.Order(current, seen);

        Assert.False(StagingDrawerFreshness.OrderWouldChange(current, again));
    }

    [Fact]
    public void ASENSORGOINGQUIETISAChangeWorthReordering()
    {
        var before = StagingDrawerFreshness.Order([S("cpu"), S("gpu")], Seen("cpu", "gpu"));
        var after = StagingDrawerFreshness.Order([S("cpu"), S("gpu")], Seen("gpu"));

        Assert.True(StagingDrawerFreshness.OrderWouldChange(before, after));
    }

    [Fact]
    public void ANEWSENSORISAChangeEvenWhenNothingMoved()
    {
        // Count first, because a pure position comparison over a shorter list would walk the common
        // prefix, find it identical and report no change - leaving the newcomer off the drawer.
        var before = StagingDrawerFreshness.Order([S("cpu")], Seen("cpu"));
        var after = StagingDrawerFreshness.Order([S("cpu"), S("gpu")], Seen("cpu", "gpu"));

        Assert.True(StagingDrawerFreshness.OrderWouldChange(before, after));
    }

    [Fact]
    public void AlternatingSensorSetsDoNotLoseAnybody()
    {
        // THE CASE THE DECISION WAS MADE FOR, AND IT IS NOT A PER-TICK FLIP. WindowsTelemetryService
        // swaps between HWiNFO and WindowsPerf by CATEGORY, and a skipped tick emits the same payload
        // as a read one, so the set is stable second to second - the alternation modelled here is a
        // stall and recovery across a session. Under eviction those two phases would delete each
        // other's entries; here each just re-sorts and the union survives. Do not read this as
        // evidence the drawer reshuffles at 1 Hz; the steady-state test above is what says it does
        // not.
        var all = new[] { S("hw-temp"), S("perf-cpu") };

        var hwinfoTick = StagingDrawerFreshness.Order(all, Seen("hw-temp"));
        var perfTick = StagingDrawerFreshness.Order(all, Seen("perf-cpu"));

        Assert.Equal(["hw-temp", "perf-cpu"], hwinfoTick.Select(e => e.Identity));
        Assert.Equal(["perf-cpu", "hw-temp"], perfTick.Select(e => e.Identity));
        Assert.Equal(2, hwinfoTick.Count);
        Assert.Equal(2, perfTick.Count);
    }

    [Fact]
    public void OrderingNothingIsNotAnError()
    {
        // Anti-vacuity for the suite: every assertion above depends on entries being returned at all,
        // so a version that returned an empty list would need to fail here rather than pass quietly.
        Assert.Empty(StagingDrawerFreshness.Order([], Seen("cpu")));
        Assert.False(StagingDrawerFreshness.OrderWouldChange([], []));
    }
}
