using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Telemetry;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that a telemetry tick will not read the fallback counters until they have been built AND
/// primed (RemEx-s999).
///
/// WHY IT NEEDS PINNING. RemEx-z447 moved counter construction off the serial startup thread onto a
/// background task, which is what stopped sign-in waiting 0.5-4.9 seconds for the machine's
/// performance-counter catalog. The safety of that rests entirely on ONE bounded wait in
/// GetTelemetryAsync. Delete that line and the whole suite stays green while the artefacts RemEx-48kh
/// was reverted for come straight back: a constructed but unprimed CPU rate counter reports 0% from
/// an empty sample window, and InitializeFallbackCounters assigns _activeNic BEFORE it takes the byte
/// baseline, so a half-finished warm-up yields a live adapter whose _lastNetworkPoll is still
/// DateTime.MinValue — one stray frame divided by the process uptime.
///
/// The warm-up task is injected rather than raced. Racing the real one would be a flake generator,
/// and the window is short precisely because the warm-up is fast on a warm machine — which is the
/// worst possible property for a regression guard.
///
/// HWiNFO is pointed at a name that does not exist, deliberately. On a machine where HWiNFO IS
/// running, its readings displace the WindowsPerf sensors in whatever categories it happens to cover
/// (see MergeHwInfoOverPerf), so asserting on WindowsPerf sensors would pass or fail depending on the
/// developer's hardware.
/// </summary>
public sealed class TelemetryWarmUpGateTests
{
    private const string NoSuchHwInfoRegion = "Local\\RemExHwInfoAbsent-s999";

    /// <remarks>
    /// The fallback timeout is shrunk from its production 2 seconds: both tests deliberately let it
    /// expire, and at the real value they added 12 seconds to the agent suite for two assertions.
    /// 250ms is chosen rather than something tighter because this single value ALSO bounds the WMI
    /// read itself: the second test's post-warm-up tick must dispatch and finish that read inside it,
    /// and the three test assemblies run concurrently, so a gen2 GC or thread-pool ramp on a loaded
    /// box could eat a 50ms budget. The cost of the headroom is a fraction of a second.
    /// </remarks>
    private static WindowsTelemetryService NewService(Task countersReady) =>
        new(NullLogger<WindowsTelemetryService>.Instance, NoSuchHwInfoRegion, staleAfterMs: 30_000,
            countersReady, fallbackTimeout: TimeSpan.FromMilliseconds(250));

    [WindowsOnlyFact("drives WindowsTelemetryService, whose fallback path builds Windows PerformanceCounters")]
    public async Task ATickBeforeTheWarmUpFinishesReadsNoCountersAtAll()
    {
        var warmUp = new TaskCompletionSource();
        using var service = NewService(warmUp.Task);

        var duringWarmUp = await service.GetTelemetryAsync();

        // THE REGRESSION GUARD. With the wait removed, this tick reads the counters while they are
        // half-built and these sensors appear — carrying exactly the artefacts described above.
        Assert.DoesNotContain(duringWarmUp.Sensors, s => s.Source == "WindowsPerf");
    }

    [WindowsOnlyFact("drives WindowsTelemetryService, whose fallback path builds Windows PerformanceCounters")]
    public async Task ATickAfterTheWarmUpCompletesReadsTheCountersAgain()
    {
        // NAMED FOR WHAT IT REACHES. An earlier version of this called itself
        // "ASkippedTickDoesNotConsumeTheSeedingRead" and could not observe that at all: the skip set
        // is `_fallbackCacheSeeded && hwinfoSensors.Count > 0`, and this fixture deliberately makes
        // HWiNFO absent, so the right conjunct is permanently false and the flag has no effect here.
        // Setting _fallbackCacheSeeded inside the skip branch left the test green. The absent-HWiNFO
        // choice that makes the sibling test machine-independent is exactly what blinds this one —
        // pinning the seeding flag needs a synthetic HWiNFO region instead (RemEx-cxel).
        //
        // What it does prove, and what is worth keeping: the gate is a GATE, not a permanent
        // disable. A wait that never released, or a skip branch that became unconditional, fails it.
        var warmUp = new TaskCompletionSource();
        using var service = NewService(warmUp.Task);

        await service.GetTelemetryAsync();   // skipped: warm-up still open
        warmUp.SetResult();

        var afterWarmUp = await service.GetTelemetryAsync();

        // Network is absent by construction rather than by machine: the injected warm-up means
        // InitializeFallbackCounters never ran, so _activeNic is null and that block emits nothing.
        // Of the three below only Memory carries real data (GlobalMemoryStatusEx, counter-independent);
        // CPU and Disk arrive on the `?.NextValue() ?? 0` placeholder path. Presence is all this
        // asserts, which is all it needs.
        var categories = afterWarmUp.Sensors
            .Where(s => s.Source == "WindowsPerf")
            .Select(s => s.Category)
            .ToHashSet();

        Assert.Contains("CPU", categories);
        Assert.Contains("Memory", categories);
        Assert.Contains("Disk", categories);
    }

    [WindowsOnlyFact("fabricates a NAMED HWiNFO map, which .NET only supports on Windows, and drives " +
        "the WindowsPerf fallback")]
    public async Task ASkippedTickDoesNotConsumeTheSeedingRead()
    {
        // WHAT THE SIBLING TESTS ABOVE CANNOT REACH, and the reason this one needs a different fixture
        // (RemEx-cxel). The skip set is `_fallbackCacheSeeded && hwinfoSensors.Count > 0`; those tests
        // point HWiNFO at a region that does not exist, so the right conjunct is permanently false and
        // the flag has no effect there at all. This one supplies a real region instead.
        //
        // The invariant: a tick that did not READ must not mark the cache seeded, or the next tick
        // skips every HWiNFO-covered category and those categories never enter the per-category cache
        // — leaving a later stall to serve a payload missing them, which is the hole RemEx-c2g4 closed.
        using var region = new SyntheticHwInfoRegion();
        region.Write(("Core 0", "°C", 42.0));

        var warmUp = new TaskCompletionSource();
        using var service = new WindowsTelemetryService(
            NullLogger<WindowsTelemetryService>.Instance, region.Name, staleAfterMs: 30_000,
            warmUp.Task, fallbackTimeout: TimeSpan.FromMilliseconds(250));

        var duringWarmUp = await service.GetTelemetryAsync();

        // THE CONTROL, without which this test degrades into the vacuous one it replaces: if HWiNFO
        // were not actually being read, the skip set would be empty no matter what the flag says, and
        // every assertion below would pass on a fixture that proves nothing.
        Assert.Contains(duringWarmUp.Sensors, s => s.Source == "HWInfo");

        // Nothing was read, so nothing may be cached.
        Assert.Empty(service.CachedByCategory);

        warmUp.SetResult();
        await service.GetTelemetryAsync();

        // THE ASSERTION. CPU is the category the synthetic region covers, so it is exactly the one a
        // wrongly-seeded flag would have skipped. Observed on the cache rather than on the payload
        // deliberately: MergeHwInfoOverPerf drops every WindowsPerf sensor in a covered category, so
        // the payloads of a skipped tick and a read tick are IDENTICAL and cannot tell them apart.
        Assert.Contains("CPU", service.CachedByCategory.Keys);
    }
}
