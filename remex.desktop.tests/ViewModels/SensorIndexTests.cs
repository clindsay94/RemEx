using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Covers the per-tick sensor index that replaced the dashboard's per-reading card scan.
/// </summary>
/// <remarks>
/// <c>ProcessTelemetry</c> used to run a <c>Where + Concat + Distinct + ToList</c> over both card
/// collections for EVERY reading. Because every sensor ever seen leaves a staged template card
/// behind, the staged count tracks the sensor count — so the work grew as N², on the UI thread, once
/// a second, whether or not the window was even visible (RemEx-8tdf).
/// <para>
/// The matching itself is what these tests pin: the index has to reproduce the old LINQ exactly, or
/// a card silently stops receiving updates and its graph freezes with no error anywhere. The
/// behaviour tests matter more than the scaling one.
/// </para>
/// </remarks>
public sealed class SensorIndexTests
{
    private static SensorViewModel SensorWith(string? id, string name)
    {
        var vm = new SensorViewModel();
        vm.Update(new SensorReading { Id = id!, Name = name });
        return vm;
    }

    private static CanvasCardViewModel SensorCard(SensorViewModel? sensor) =>
        new() { CardType = "Sensor", Sensor = sensor };

    [Fact]
    public void IndexesByHostStampedId_WhenPresent()
    {
        // Identity prefers the host's Id so a live relabel keeps the card bound (RemEx-km0i.14).
        var sensor = SensorWith("cpu-pkg-0", "CPU Package");
        var index = CanvasDashboardViewModel.BuildSensorIndex(new[] { SensorCard(sensor) }, []);

        index.Should().ContainKey("cpu-pkg-0");
        index["cpu-pkg-0"].Should().ContainSingle().Which.Should().BeSameAs(sensor);
        index.Should().NotContainKey("CPU Package");
    }

    [Fact]
    public void FallsBackToName_WhenTheHostStampedNoId()
    {
        var sensor = SensorWith(null, "GPU Hot Spot");
        var index = CanvasDashboardViewModel.BuildSensorIndex(new[] { SensorCard(sensor) }, []);

        index.Should().ContainKey("GPU Hot Spot");
    }

    [Fact]
    public void TwoSensorsWhoseIdsDifferOnlyByCaseAreDistinct()
    {
        // THE MATCHING SIDE of RemEx-228x, and the side that was always right. Identities are normally
        // host-stamped ids — machine tokens like wmi:cpu:load — so case is meaningful in them and two
        // that differ by it are two different sensors.
        var upper = SensorWith("CPU", "Package");
        var lower = SensorWith("cpu", "Core");

        var index = CanvasDashboardViewModel.BuildSensorIndex(
            new[] { SensorCard(upper), SensorCard(lower) }, []);

        index.Should().ContainKey("CPU");
        index.Should().ContainKey("cpu");
        index["CPU"].Should().ContainSingle().Which.Should().BeSameAs(upper);
        index["cpu"].Should().ContainSingle().Which.Should().BeSameAs(lower);
    }

    [Fact]
    public void BothSensorsFireAlertsWhenTheirIdsDifferOnlyByCase()
    {
        // THE BUG, END TO END, and the only test here that would catch it coming back. The two below
        // pin the shared comparer and the index — but write
        // `new HashSet<string>(StringComparer.OrdinalIgnoreCase)` on the subscription set directly,
        // without touching the shared comparer, and both of them stay green while the defect returns.
        // This one drives the real path: two cards, two view models, and the question is whether BOTH
        // got their alert handler wired. Before the fix the second never did, so its threshold alerts
        // silently never fired.
        var vm = NewDashboard();

        var fired = new List<SensorAlert>();
        vm.SensorAlertFired += fired.Add;

        // First tick creates the view models — and is where subscription happens, once per identity.
        vm.ApplyTelemetry(new TelemetryPayload
        {
            Sensors = new List<SensorReading>
            {
                new() { Id = "CPU", Name = "Package", Value = 10 },
                new() { Id = "cpu", Name = "Core", Value = 10 },
            },
        });

        foreach (var card in vm.StagedCards.Where(c => c.Sensor is not null))
        {
            card.Sensor!.Alert = new SensorAlert
            {
                SensorName = card.Sensor.Name,
                Threshold = 50,
                Direction = AlertDirection.Above,
                Severity = AlertSeverity.Warning,
            };
        }

        // Second tick pushes both over the threshold.
        vm.ApplyTelemetry(new TelemetryPayload
        {
            Sensors = new List<SensorReading>
            {
                new() { Id = "CPU", Name = "Package", Value = 99 },
                new() { Id = "cpu", Name = "Core", Value = 99 },
            },
        });

        fired.Should().HaveCount(2,
            "each sensor must have its own alert subscription; case-folding the identity gave the "
            + "second one none and its alerts were lost silently");
        fired.Select(a => a.SensorName).Should().BeEquivalentTo(new[] { "Package", "Core" });
    }

    [Fact]
    public void AlertsStillFireAfterADisconnectAndReconnect()
    {
        // THE BEAD, and a wider failure than the case-collision one next door: this lost alerts for
        // EVERY sensor, after an ordinary reconnect.
        //
        // Disconnecting detaches every handler and empties the subscription set. That half always
        // worked. What did not is the re-attach: the staged template card keeps its view model across
        // the disconnect, so on the next tick the "first time seeing this sensor" branch — which was
        // the only place that wired the handler — is skipped, and nothing ever wired it again.
        // Threshold alerts were then dead for the life of the process, with cards still updating so
        // the dashboard looked entirely healthy.
        var vm = NewDashboard();

        var fired = new List<SensorAlert>();
        vm.SensorAlertFired += fired.Add;

        vm.ApplyTelemetry(new TelemetryPayload
        {
            Sensors = new List<SensorReading> { new() { Id = "cpu-pkg-0", Name = "CPU Package", Value = 10 } },
        });

        foreach (var card in vm.StagedCards.Where(c => c.Sensor is not null))
        {
            card.Sensor!.Alert = new SensorAlert
            {
                SensorName = card.Sensor.Name,
                Threshold = 50,
                Direction = AlertDirection.Above,
                Severity = AlertSeverity.Warning,
            };
        }

        // The disconnect. Handlers detached, set emptied — but the view model survives on its card.
        vm.CleanupSensorSubscriptions();

        // Reconnect: the same sensor arrives again, now over its threshold.
        vm.ApplyTelemetry(new TelemetryPayload
        {
            Sensors = new List<SensorReading> { new() { Id = "cpu-pkg-0", Name = "CPU Package", Value = 99 } },
        });

        fired.Should().ContainSingle(
            "a reconnect must re-attach the alert handler; before the fix nothing did, and every "
            + "sensor's threshold alerts were silently dead from the first disconnect onward");
    }

    [Fact]
    public void CustomizationIsStillTrackedAfterADisconnectAndReconnect()
    {
        // THE SIBLING OF THE TEST ABOVE, and the reason it needed its own. Disconnecting detaches TWO
        // handlers per sensor; RemEx-k74x re-attached the alert one and left this one behind, so
        // per-card customization silently stopped being saved from the first reconnect onward — theme,
        // custom title, overlay toggle and graph type applied live and were gone at restart. They are
        // now wired in the same block precisely so one cannot be fixed without the other (RemEx-9qgb).
        var vm = NewDashboard();

        vm.ApplyTelemetry(new TelemetryPayload
        {
            Sensors = new List<SensorReading> { new() { Id = "cpu-pkg-0", Name = "CPU Package", Value = 10 } },
        });

        vm.CleanupSensorSubscriptions();

        var before = vm.SensorCustomizationNotifications;

        // Reconnect. The re-attach happens here, and the reading's own property changes then flow
        // through the handler — which is what proves it is attached, without provoking a real save.
        vm.ApplyTelemetry(new TelemetryPayload
        {
            Sensors = new List<SensorReading> { new() { Id = "cpu-pkg-0", Name = "CPU Package", Value = 20 } },
        });

        vm.SensorCustomizationNotifications.Should().BeGreaterThan(before,
            "the customization handler must be re-attached on reconnect; before the fix nothing "
            + "re-attached it and the user's card settings stopped being persisted");
    }

    [Fact]
    public void ReconnectingDoesNotDoubleUpTheCustomizationHandler()
    {
        // The other direction, and the one that matters MORE here than it does for alerts: this
        // handler fires on every customization change and each duplicate means another write of the
        // whole layout profile to disk. A doubled alert is a visible annoyance; a doubled save is
        // invisible until the writes pile up.
        //
        // Asserted as a CONSTANT DELTA rather than an absolute count, because the number of property
        // changes one reading raises is an implementation detail of SensorViewModel — but it must not
        // grow with the number of reconnects. Two sensors, so a handler attached to only one of them
        // cannot pass by accident (the hole that let RemEx-228x's per-sensor bug through).
        //
        // EXACTLY WHAT THIS CATCHES, measured rather than assumed. There are two independent detaches
        // — the idempotent `-=` at the attach site and the one in CleanupSensorSubscriptions — and
        // removing EITHER alone leaves this green, because the other still nets each sensor back to
        // one subscription. It goes red when BOTH are gone. So this is a guard on the PAIR, not on
        // either half, which is the same shape as the bug it was written for: two things that are
        // only correct together, and were quietly separated.
        var vm = NewDashboard();
        var value = 0;

        int CycleDelta()
        {
            var before = vm.SensorCustomizationNotifications;
            value += 10; // distinct every time: an unchanged value raises nothing
            vm.ApplyTelemetry(new TelemetryPayload
            {
                Sensors = new List<SensorReading>
                {
                    new() { Id = "cpu-pkg-0", Name = "CPU Package", Value = value },
                    new() { Id = "gpu-hot-0", Name = "GPU Hotspot", Value = value + 1 },
                },
            });
            return vm.SensorCustomizationNotifications - before;
        }

        // The FIRST cycle is not a valid baseline and measuring it as one is how this test first
        // failed: first sighting populates Name, Unit and Category from their defaults as well as
        // Value, so it raises strictly more than a steady tick does (18 against 12 here). A reconnect
        // keeps the existing view model — that is the whole reason the bug existed — so post-reconnect
        // cycles are steady-state ones and must be compared against a steady-state baseline.
        CycleDelta().Should().BeGreaterThan(0, "the handler must be attached at all on first sight");
        var steadyState = CycleDelta();
        steadyState.Should().BeGreaterThan(0, "a plain reading must keep reaching the handler");

        for (int reconnect = 1; reconnect <= 3; reconnect++)
        {
            vm.CleanupSensorSubscriptions();
            CycleDelta().Should().Be(steadyState,
                $"after {reconnect} reconnect(s) each reading must still reach the handler exactly "
                + "once per sensor; a larger delta means the re-attach stacked another subscription "
                + "and every customization change now saves the profile more than once");
        }
    }

    [Fact]
    public void ReconnectingDoesNotDoubleUpTheAlert()
    {
        // The other direction, and the reason the original code guarded on first-sight at all: wiring
        // on every tick without the identity guard would attach a second handler and the user would
        // get two notifications for one crossing. Several reconnects, still exactly one alert.
        var vm = NewDashboard();

        var fired = new List<SensorAlert>();
        vm.SensorAlertFired += fired.Add;

        vm.ApplyTelemetry(new TelemetryPayload
        {
            Sensors = new List<SensorReading> { new() { Id = "gpu-hot", Name = "GPU Hot Spot", Value = 10 } },
        });

        foreach (var card in vm.StagedCards.Where(c => c.Sensor is not null))
        {
            card.Sensor!.Alert = new SensorAlert
            {
                SensorName = card.Sensor.Name,
                Threshold = 50,
                Direction = AlertDirection.Above,
                Severity = AlertSeverity.Warning,
            };
        }

        for (int cycle = 0; cycle < 3; cycle++)
        {
            vm.CleanupSensorSubscriptions();
            vm.ApplyTelemetry(new TelemetryPayload
            {
                Sensors = new List<SensorReading> { new() { Id = "gpu-hot", Name = "GPU Hot Spot", Value = 10 } },
            });
        }

        vm.ApplyTelemetry(new TelemetryPayload
        {
            Sensors = new List<SensorReading> { new() { Id = "gpu-hot", Name = "GPU Hot Spot", Value = 99 } },
        });

        fired.Should().ContainSingle("three reconnects must leave one handler attached, not three");
    }

    [Fact]
    public void TheIndexAndTheAlertSubscriptionAgreeOnWhatAnIdentityIs()
    {
        // THE BUG. The card index compared identities ordinally while the alert-subscription set
        // compared them case-insensitively, both keyed by the same string. Two sensors differing only
        // in case therefore got two cards and two view models (correct) but ONE subscription — the
        // second sensor's threshold alerts silently never fired, with no error and nothing logged.
        //
        // They now share one comparer, so they cannot disagree. Asserting the shared comparer's
        // BEHAVIOUR rather than its identity keeps this meaningful if it is ever swapped for another
        // case-sensitive one.
        var comparer = CanvasDashboardViewModel.SensorIdentityComparer;

        comparer.Equals("CPU", "cpu").Should().BeFalse(
            "an identity is a host-stamped machine token, in which case is meaningful");
        comparer.Equals("wmi:cpu:load", "wmi:cpu:load").Should().BeTrue();

        // And the index really is built with it: a case-insensitive comparer would collapse these two.
        var index = CanvasDashboardViewModel.BuildSensorIndex(
            new[] { SensorCard(SensorWith("CPU", "Package")), SensorCard(SensorWith("cpu", "Core")) }, []);

        index.Should().HaveCount(2, "the index must not collapse identities the subscription set keeps apart");
    }

    [Fact]
    public void CoversPlacedAndStagedCardsAlike()
    {
        // A sensor normally has BOTH: a staged template card and however many placed cards the user
        // dropped. Missing either side would leave one of them never updating.
        var placedSensor = SensorWith("t1", "Temp");
        var stagedSensor = SensorWith("t2", "Other");

        var index = CanvasDashboardViewModel.BuildSensorIndex(
            new[] { SensorCard(placedSensor) },
            new[] { SensorCard(stagedSensor) });

        index.Should().ContainKeys("t1", "t2");
    }

    [Fact]
    public void GroupsEveryCardBoundToTheSameSensor()
    {
        // The user can drop several cards for one sensor; all of them share one SensorViewModel and
        // all must be reachable from the single identity.
        var shared = SensorWith("fan-1", "Fan");
        var index = CanvasDashboardViewModel.BuildSensorIndex(
            new[] { SensorCard(shared), SensorCard(shared) },
            new[] { SensorCard(shared) });

        // ...but deduplicated BY REFERENCE, reproducing the Distinct() this replaced — otherwise the
        // same view-model gets Update()d three times per tick and raises three PropertyChanged storms.
        index["fan-1"].Should().ContainSingle().Which.Should().BeSameAs(shared);
    }

    [Fact]
    public void DistinctIsByReference_NotByIdentityString()
    {
        // Two DIFFERENT view-models can legitimately share an identity string (a card restored from
        // the profile before the live sensor arrives). Both must be updated.
        var first = SensorWith("dup", "Dup");
        var second = SensorWith("dup", "Dup");

        var index = CanvasDashboardViewModel.BuildSensorIndex(
            new[] { SensorCard(first), SensorCard(second) }, []);

        index["dup"].Should().HaveCount(2);
    }

    [Fact]
    public void SkipsNonSensorCardsAndCardsWithNoSensor()
    {
        var index = CanvasDashboardViewModel.BuildSensorIndex(
            new[]
            {
                new CanvasCardViewModel { CardType = "Clock" },
                SensorCard(null),
            },
            []);

        index.Should().BeEmpty();
    }

    [Fact]
    public void SkipsSensorsThatHaveNotReceivedAReadingYet()
    {
        // A freshly constructed SensorViewModel has no RawReading and therefore no identity. It gets
        // one later in the same tick, which is exactly why the index is rebuilt per tick rather than
        // cached when a card is added.
        var index = CanvasDashboardViewModel.BuildSensorIndex(
            new[] { SensorCard(new SensorViewModel()) }, []);

        index.Should().BeEmpty();
    }

    // ── The real path, through the ApplyTelemetry seam ──────────────────────────
    //
    // Everything above tests the index builder in isolation. These drive the actual telemetry tick,
    // because the subtlest thing this change introduced is not in the builder: the create branch puts
    // its new (empty) bucket into the index BEFORE running, and then mutates that same list. Get that
    // wrong and a second reading for the same sensor in one payload creates a DUPLICATE card, which
    // no isolated builder test can see.
    //
    // Replaced a timing-ratio "is it still linear" test that was removed on review: its per-reading
    // lookup loop lived in the test rather than in production, so it would have kept passing if a
    // scan were put back into ProcessTelemetry — it guarded nothing about the real path, while
    // carrying the flake profile of a wall-clock ratio over a few milliseconds.

    private static CanvasDashboardViewModel NewDashboard() =>
        // layoutService and shell are stored and never dereferenced by this path (RemEx-w9ui).
        new(new ConnectionViewModel(), null!, null!);

    [Fact]
    public void ApplyTelemetry_CreatesOneStagedCardPerSensor()
    {
        var vm = NewDashboard();

        vm.ApplyTelemetry(new TelemetryPayload
        {
            Sensors = new List<SensorReading>
            {
                new() { Id = "a", Name = "A", Value = 1 },
                new() { Id = "b", Name = "B", Value = 2 },
            },
        });

        vm.StagedCards.Should().HaveCount(2);
        vm.StagedCards.Select(c => c.CardTitle).Should().BeEquivalentTo(["A", "B"]);
    }

    [Fact]
    public void ApplyTelemetry_SameSensorTwiceInOnePayload_DoesNotCreateTwoCards()
    {
        // The create branch inserts its bucket into the index and then fills it, so the second
        // reading must find the sensor already there. If the bucket were not shared, this would
        // produce two staged cards for one sensor — and the user would see a duplicate that never
        // goes away.
        var vm = NewDashboard();

        vm.ApplyTelemetry(new TelemetryPayload
        {
            Sensors = new List<SensorReading>
            {
                new() { Id = "dup", Name = "Dup", Value = 1 },
                new() { Id = "dup", Name = "Dup", Value = 2 },
            },
        });

        vm.StagedCards.Should().ContainSingle();
        vm.StagedCards[0].Sensor!.RawReading!.Value.Should().Be(2, "the later reading wins");
    }

    [Fact]
    public void ApplyTelemetry_AcrossTicks_ReusesTheSameSensorViewModel()
    {
        // The regression that matters to a user: a card that stops receiving updates just freezes,
        // with no error anywhere. Ticking twice must update the SAME view-model, not orphan it.
        var vm = NewDashboard();
        vm.ApplyTelemetry(new TelemetryPayload { Sensors = [new SensorReading { Id = "cpu", Name = "CPU", Value = 10 }] });

        var sensor = vm.StagedCards.Should().ContainSingle().Subject.Sensor;

        vm.ApplyTelemetry(new TelemetryPayload { Sensors = [new SensorReading { Id = "cpu", Name = "CPU", Value = 55 }] });

        vm.StagedCards.Should().ContainSingle();
        vm.StagedCards[0].Sensor.Should().BeSameAs(sensor, "a new VM here means the old card froze");
        sensor!.RawReading!.Value.Should().Be(55);
    }

    [Fact]
    public void ApplyTelemetry_IgnoresAPayloadWithNoSensors()
    {
        var vm = NewDashboard();

        vm.ApplyTelemetry(new TelemetryPayload { Sensors = null! });

        vm.StagedCards.Should().BeEmpty();
    }
}
