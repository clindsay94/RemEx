using System;
using System.Linq;
using System.Net.NetworkInformation;
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

    [WindowsOnlyFact("drives the REAL warm-up, which builds Windows PerformanceCounters and enumerates NICs")]
    public async Task TheFirstReadingAfterARealWarmUpIsNotTheUnprimedArtefact()
    {
        // THE REAL WARM-UP, not an injected one, and that is forced rather than chosen. The
        // constructor does `_countersReady = countersReady ?? Task.Run(InitializeFallbackCounters)`,
        // so passing a task - the only way to hold the warm-up open - means InitializeFallbackCounters
        // NEVER RUNS. The CPU counter then stays null and its read falls to `?? 0`, which is the very
        // artefact this guarantee excludes, and _activeNic stays null so no Network sensor is emitted
        // at all and there is nothing to assert on. The sibling tests above can inject precisely
        // because they assert on ABSENCE; this one asserts on what a served reading is made of.
        // THE TIMEOUT IS WIDENED, NOT DEFAULTED, AND THAT IS THE OPPOSITE OF THE SIBLINGS ABOVE.
        // They shrink it to 250ms because they WANT it to expire. This one must not: a timeout
        // serves the cached fallback instead of a real read, which on a first tick is empty, and
        // the test would fail for having no sensors rather than for the ordering it exists to pin.
        //
        // The production default of 2 seconds is not enough of a budget. This is the ONLY test in
        // the assembly that runs InitializeFallbackCounters for real - the others inject, so the
        // counter catalog is never loaded - and the service's own comment records the measured
        // cost of that first construction as 510-888ms warm and 4.9 SECONDS on the first run after
        // a cold OS cache, with three test assemblies running concurrently on top. A 2-second
        // budget against a documented 4.9-second worst case is a flake generator, which is exactly
        // what this file's header warns about. Still bounded, so a genuine wedge cannot hang the
        // suite.
        using var service = new WindowsTelemetryService(
            NullLogger<WindowsTelemetryService>.Instance, NoSuchHwInfoRegion, staleAfterMs: 30_000,
            countersReady: null, fallbackTimeout: TimeSpan.FromSeconds(30));

        var payload = await service.GetTelemetryAsync();

        // NOT ASSERTED BY VALUE, deliberately. Both obvious assertions are intrinsically weak: an
        // idle machine's first primed CPU sample can legitimately round to ~0%, so "not 0" is not a
        // true statement about correct behaviour; and the unprimed-NIC artefact divides one stray
        // frame by a DateTime.MinValue baseline - roughly 6.4e10 seconds - producing a near-zero
        // rate no sane-bounds check can tell from a genuinely idle adapter. The magnitude simply
        // does not carry the information, so the ORDER is asserted instead.
        var network = payload.Sensors.FirstOrDefault(
            sensor => sensor.Source == "WindowsPerf" && sensor.Category == "Network");

        // FIRST, THAT THE REAL WARM-UP RAN AND THE GATE OPENED.
        Assert.Contains(payload.Sensors, sensor => sensor.Source == "WindowsPerf");

        // SECOND, AND THIS IS THE GUARD THAT ACTUALLY STOPS THE TEST GOING VACUOUS. The assertion
        // above rules out only one of the two silent-pass modes - the gate never opening. It says
        // nothing about the NIC, because the WindowsPerf sensors come from CPU, Disk and Memory,
        // none of which depend on _activeNic. If `new PerformanceCounter(...)` throws - the
        // ordinary corrupt-catalog condition that wants `lodctr /R` - the warm-up's catch swallows
        // it, _activeNic stays null, WindowsPerf sensors still appear full of zeros, and every
        // assertion below would pass while proving nothing.
        //
        // So the test evaluates the service's own adapter predicate itself: if this machine HAS a
        // qualifying adapter, the warm-up had no excuse for not recording an interval.
        var hasQualifyingAdapter = NetworkInterface.GetAllNetworkInterfaces().Any(nic =>
            nic.OperationalStatus == OperationalStatus.Up &&
            nic.NetworkInterfaceType != NetworkInterfaceType.Loopback);

        if (hasQualifyingAdapter)
        {
            Assert.True(service.FirstNetworkIntervalSeconds is not null,
                "this machine has a qualifying adapter, so the warm-up should have reached the " +
                "network block - a null interval means it died before the baseline was taken");
        }

        // THE INVARIANT. If a caller has been served a Network reading, the byte baseline and its
        // timestamp must already have been taken - otherwise the rate they were just handed was
        // computed against DateTime.MinValue.
        //
        // Checking `_activeNic != null` instead would pass on exactly the broken state, because
        // InitializeFallbackCounters assigns the NIC BEFORE it takes the baseline. The timestamp is
        // what moves last, so the timestamp is what gets asked about. This is the ordering that
        // RemEx-48kh was reverted for breaking.
        //
        // THE FIRST SHAPE OF THIS ASSERTION WAS GREEN AGAINST THE DEFECT, and mutation is what
        // caught it: asking whether _lastNetworkPoll is still DateTime.MinValue AFTER the read is
        // always false, because the read path stamps that field on its way out. Deleting the
        // warm-up baseline entirely left that version passing. The interval actually used for the
        // division is the only thing that carries the answer, because it is captured before the
        // stamp overwrites the evidence.
        //
        // Written as an implication so a machine with no qualifying adapter - nothing Up and
        // non-loopback - is honestly out of scope rather than failing for a reason unrelated to
        // ordering. The WindowsPerf assertion above is what stops that leniency going vacuous.
        Assert.True(
            network is null || service.FirstNetworkIntervalSeconds < 3600,
            "the first network rate was divided by " +
            $"{service.FirstNetworkIntervalSeconds} seconds. A primed baseline yields a handful of " +
            "seconds; the process uptime measured from DateTime.MinValue is about 6.4e10, which " +
            "means the byte baseline was never taken before the reading was served");
    }
}
