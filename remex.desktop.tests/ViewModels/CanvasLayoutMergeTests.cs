using Remex.Core.Models;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Tests;

/// <summary>
/// Regression tests for <see cref="CanvasLayoutMerge"/> — the RemEx-jwvg fix that stops the canvas
/// layout being silently truncated to only the sensors that happen to be live when a save fires.
/// </summary>
public sealed class CanvasLayoutMergeTests
{
    private static CardState Sensor(string id, string sensorName) =>
        new() { CardId = id, CardType = "Sensor", SensorId = sensorName };

    private static CardState NonSensor(string id, string type) =>
        new() { CardId = id, CardType = type };

    private static HashSet<string> Materialized(params string[] names) =>
        new(names, StringComparer.OrdinalIgnoreCase);

    // ═══════════════ The bug this fixes ═══════════════

    [Fact]
    public void MergeCards_PreservesPersistedSensorsThatHaveNotReportedYet()
    {
        // Five sensor cards were persisted, but only two sensors have reported this session, so only
        // two are live on the canvas. The naive (pre-fix) save wrote just those two and deleted the
        // other three forever. The merge must keep all five.
        var persisted = new List<CardState>
        {
            Sensor("c1", "CPU Temp"),
            Sensor("c2", "GPU Temp"),
            Sensor("c3", "+12V"),
            Sensor("c4", "+5V"),
            Sensor("c5", "Fan1"),
        };

        var live = new List<CardState> { Sensor("c1", "CPU Temp"), Sensor("c2", "GPU Temp") };
        var materialized = Materialized("CPU Temp", "GPU Temp");

        var merged = CanvasLayoutMerge.MergeCards(persisted, live, materialized);

        Assert.Equal(5, merged.Count);
        Assert.Equal(
            new[] { "c1", "c2", "c3", "c4", "c5" }.OrderBy(x => x),
            merged.Select(c => c.CardId).OrderBy(x => x));
    }

    // ═══════════════ Deletions and unpins must still stick ═══════════════

    [Fact]
    public void MergeCards_DoesNotResurrectDeletedCardForSeenSensor()
    {
        // The user deleted the "+12V" card. Its sensor IS materialized (reported this session), so the
        // live canvas is authoritative and the deletion must persist — do NOT preserve it.
        var persisted = new List<CardState> { Sensor("c1", "CPU Temp"), Sensor("c3", "+12V") };
        var live = new List<CardState> { Sensor("c1", "CPU Temp") };
        var materialized = Materialized("CPU Temp", "+12V");

        var merged = CanvasLayoutMerge.MergeCards(persisted, live, materialized);

        Assert.Single(merged);
        Assert.Equal("c1", merged[0].CardId);
    }

    [Fact]
    public void MergeCards_IncludesNonSensorAndLiveCards_NoDuplicates()
    {
        var persisted = new List<CardState> { Sensor("c1", "CPU Temp"), Sensor("c2", "GPU Temp") };
        var live = new List<CardState>
        {
            NonSensor("conn", "Connection"),
            Sensor("c1", "CPU Temp"), // already live + also in persisted → must appear once
        };
        var materialized = Materialized("CPU Temp");

        var merged = CanvasLayoutMerge.MergeCards(persisted, live, materialized);

        // conn + c1 (live) + c2 (preserved, GPU not materialized) = 3, no duplicate c1.
        Assert.Equal(3, merged.Count);
        Assert.Equal(1, merged.Count(c => c.CardId == "c1"));
        Assert.Contains(merged, c => c.CardId == "conn");
        Assert.Contains(merged, c => c.CardId == "c2");
    }

    [Fact]
    public void MergeCards_CaseInsensitiveSensorNameMatch()
    {
        var persisted = new List<CardState> { Sensor("c1", "cpu temp") };
        var live = new List<CardState>();
        var materialized = Materialized("CPU TEMP"); // seen, different case → authoritative (dropped)

        var merged = CanvasLayoutMerge.MergeCards(persisted, live, materialized);

        Assert.Empty(merged);
    }

    // ═══════════════ Pinned sensors ═══════════════

    [Fact]
    public void MergePinnedSensors_PreservesPinsForDormantSensors()
    {
        var persistedPins = new[] { "CPU Temp", "GPU Temp", "+12V" };
        var livePins = new[] { "CPU Temp" };
        var materialized = Materialized("CPU Temp"); // GPU Temp & +12V not seen yet

        var merged = CanvasLayoutMerge.MergePinnedSensors(persistedPins, livePins, materialized);

        Assert.Equal(3, merged.Count);
        Assert.Contains("GPU Temp", merged);
        Assert.Contains("+12V", merged);
    }

    [Fact]
    public void MergePinnedSensors_UnpinOfSeenSensorSticks()
    {
        var persistedPins = new[] { "CPU Temp", "GPU Temp" };
        var livePins = new[] { "CPU Temp" };                 // user unpinned GPU Temp
        var materialized = Materialized("CPU Temp", "GPU Temp"); // both seen → live authoritative

        var merged = CanvasLayoutMerge.MergePinnedSensors(persistedPins, livePins, materialized);

        Assert.Single(merged);
        Assert.Equal("CPU Temp", merged[0]);
    }
}
