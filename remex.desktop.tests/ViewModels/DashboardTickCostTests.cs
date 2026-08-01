using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Guards the properties that any future narrowing of the telemetry tick must not break (RemEx-4q6l).
/// </summary>
/// <remarks>
/// <para>
/// RemEx-4q6l proposed three narrowings: update only view models bound to placed cards, keep staged
/// entries as plain data until placed, and gate the whole tick on window visibility with a ~10s
/// fallback. Each is a BEHAVIOUR change rather than a pure optimisation, and the measurement that was
/// supposed to justify them had never been taken.
/// </para>
/// <para>
/// IT WAS TAKEN, AND IT ARGUES AGAINST ALL THREE. Driving <c>ApplyTelemetry</c> at 40 / 120 / 250
/// sensors with no cards placed costs well under a millisecond per tick: two runs of the harness gave
/// 0.016-0.029 ms at 40, 0.040-0.103 ms at 120 and 0.087-0.139 ms at 250, and 0.272 ms at 500. The
/// spread between runs is larger than most of the effect being argued about, which is itself the
/// answer. At the 1 Hz telemetry rate this is around 0.01% of one core. The 500-sensor point matters
/// twice over: it confirms the tick is linear rather than merely small, landing within a few percent
/// of what doubling 250 predicts. 250 is a high-end machine rather than an extreme one — per-core
/// temperature, clock, load and power on a 16-core part is already 100+ readings before the GPU,
/// several NVMe drives and the super-I/O chip — but the tick is linear in sensor count (pinned by
/// <see cref="SensorIndexTests"/>) and measured to be so, the conclusion does not turn on any single
/// figure. <c>BenchmarkTick</c> below is the harness, kept as a skipped manual benchmark so
/// the number stays reproducible; it is not asserted on, because a timing threshold would be flaky on
/// shared CI hardware.
/// </para>
/// <para>
/// THE BINDING SIDE IS NEARLY FREE TOO, BUT NOT FOR THE REASON THAT FIRST LOOKS OBVIOUS. The staging
/// drawer's container carries <c>IsVisible="{Binding IsStagingDrawerOpen}"</c>, and a collapsed
/// subtree is dropped from measure and arrange entirely — that, rather than list virtualisation, is
/// what makes a closed drawer cost nothing to lay out. The distinction matters because
/// <c>IsVisible=false</c> does NOT stop binding evaluation: once the drawer has been opened, whatever
/// <c>ListBoxItem</c>s were realised keep live bindings to their sensor's value and re-evaluate on
/// every tick thereafter. How many that is depends on virtualisation actually working, and the
/// <c>ListBox</c> sits inside a redundant outer <c>ScrollViewer</c> that measures its child with
/// infinite height — the classic way to defeat a <c>VirtualizingStackPanel</c>. That was not resolved
/// at runtime here. It does not change the conclusion at these magnitudes, but "the drawer costs
/// nothing" is only true of layout, not of bindings.
/// </para>
/// <para>
/// THE FINDING THAT MATTERS MOST IS NOT ABOUT SPEED. <c>SensorViewModel.CheckAlert</c> is called from
/// exactly one place — the last line of <c>Update</c> — so threshold alerts are evaluated only as a
/// side effect of the same per-sensor work the bead proposed to skip. Gating the tick on window
/// visibility, AS THE BEAD DESCRIBES IT, therefore stops evaluating alerts while the window is
/// hidden — the state a tray-resident monitor spends nearly all its time in and the state its alerts
/// exist for. With the suggested ~10s fallback a crossing is reported up to ten seconds late, and a
/// spike that begins and ends between two fallback ticks is never reported at all.
/// </para>
/// <para>
/// THAT IS AN ARGUMENT AGAINST THE NAIVE IMPLEMENTATION, NOT AGAINST THE IDEA. Splitting
/// <c>SensorViewModel.Update</c> into an evaluate half (<c>Value</c>, <c>RawReading</c>,
/// <c>CheckAlert</c> — always) and a present half (<c>History</c> and the gauge notifications — only
/// when something is on screen) keeps alerts at full rate and still skips most of the measured work.
/// It is maybe thirty lines and it is CORRECT; it is simply not worth thirty lines to reclaim a
/// fraction of a millisecond per second, and it would add a resync-on-show path that has to be right.
/// Recording it here so the option is not re-derived, and so nobody reads the paragraph above as
/// "this can never be optimised".
/// </para>
/// </remarks>
public class DashboardTickCostTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public DashboardTickCostTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private static CanvasDashboardViewModel NewDashboard() => new(new ConnectionViewModel(), null!, null!);

    private static TelemetryPayload Reading(string id, double value) => new()
    {
        Sensors = new List<SensorReading> { new() { Id = id, Name = id, Value = value, Unit = "C" } },
    };

    /// <summary>
    /// The harness the figures in the class remarks came from. Skipped by default: it asserts nothing,
    /// and a timing threshold would be flaky on shared hardware. Un-skip and read the test output to
    /// re-take the measurement before proposing any of RemEx-4q6l's three narrowings again. Run it
    /// with <c>-l "console;verbosity=detailed"</c> or the numbers go nowhere.
    /// </summary>
    [Theory(Skip = "manual benchmark - un-skip to re-measure, see class remarks")]
    [InlineData(40)]
    [InlineData(120)]
    [InlineData(250)]
    [InlineData(500)]
    public void BenchmarkTick(int sensorCount)
    {
        var vm = NewDashboard();
        var payloads = new List<TelemetryPayload>();
        for (int round = 0; round <= 60; round++)
        {
            var list = new List<SensorReading>(sensorCount);
            for (int i = 0; i < sensorCount; i++)
                list.Add(new SensorReading { Id = $"s-{i}", Name = $"Sensor {i}", Value = round + i, Unit = "C" });
            payloads.Add(new TelemetryPayload { Sensors = list });
        }

        vm.ApplyTelemetry(payloads[0]); // first sight: creates the view models and staged cards

        var notificationsBefore = vm.SensorCustomizationNotifications;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int round = 1; round <= 60; round++) vm.ApplyTelemetry(payloads[round]);
        clock.Stop();

        // Reported through the output helper rather than an assertion, so un-skipping produces a
        // measurement instead of a failure. Run with `-l "console;verbosity=detailed"` to see it.
        _output.WriteLine(
            $"N={sensorCount} staged={vm.StagedCards.Count} placed={vm.Cards.Count} | "
            + $"{clock.Elapsed.TotalMilliseconds / 60:F3} ms/tick | "
            + $"{(vm.SensorCustomizationNotifications - notificationsBefore) / 60.0:F0} PropertyChanged/tick");
    }

    [Fact]
    public void AlertsAreEvaluatedForASensorWithNoPlacedCard()
    {
        // THE GUARD FOR ITEM [2]. "Update only the view models bound to placed cards" sounds free, and
        // for rendering it is — but a sensor the user has configured an alert on and then never
        // dropped onto the canvas has only a staged card. Skipping it would silently disarm exactly
        // the sensors someone is watching rather than displaying.
        var vm = NewDashboard();
        vm.ApplyTelemetry(Reading("cpu-pkg-0", 10));

        vm.Cards.Should().BeEmpty("this sensor was never placed — it exists only in the staging drawer");
        var sensor = vm.StagedCards.Single().Sensor!;
        sensor.Alert = new SensorAlert { SensorName = "cpu-pkg-0", Threshold = 90, Direction = AlertDirection.Above };

        var fired = new List<SensorAlert>();
        vm.SensorAlertFired += fired.Add;

        vm.ApplyTelemetry(Reading("cpu-pkg-0", 95));

        fired.Should().ContainSingle("an alert on an unplaced sensor must still fire; the card it is "
            + "not bound to is a display concern, the alert is not");
    }

    [Fact]
    public void EveryReadingHandedToTheTickIsEvaluatedAgainstTheThreshold()
    {
        // WHAT THIS DOES AND DOES NOT GUARD, because the obvious reading of it is wrong. It drives
        // ApplyTelemetry, so it catches sampling introduced INSIDE the tick. It does NOT catch item
        // [4] as the bead specifies it: that gates ProcessTelemetry, one level above this seam, and a
        // faithful implementation of it would leave this test green. ProcessTelemetry cannot be
        // driven from here — it posts to the UI dispatcher, which is why ApplyTelemetry exists as an
        // internal entry point at all.
        //
        // It is kept rather than deleted because the property it does pin is real: a crossing that
        // starts and ends between two ticks is only ever seen by the tick that lands inside it, so
        // any per-reading skipping loses alerts outright rather than merely delaying them. The gap
        // above the seam is covered by the writeup, not by a test, and saying so is the point.
        var vm = NewDashboard();
        vm.ApplyTelemetry(Reading("gpu-hot-0", 40));

        vm.StagedCards.Single().Sensor!.Alert =
            new SensorAlert { SensorName = "gpu-hot-0", Threshold = 90, Direction = AlertDirection.Above };

        var fired = new List<SensorAlert>();
        vm.SensorAlertFired += fired.Add;

        // Normal, spike, normal — one tick each, exactly as telemetry delivers them.
        vm.ApplyTelemetry(Reading("gpu-hot-0", 41));
        vm.ApplyTelemetry(Reading("gpu-hot-0", 95));
        vm.ApplyTelemetry(Reading("gpu-hot-0", 42));

        fired.Should().ContainSingle("the spike occupied a single tick, so dropping ticks drops the "
            + "alert entirely rather than merely delaying it");
    }
}
