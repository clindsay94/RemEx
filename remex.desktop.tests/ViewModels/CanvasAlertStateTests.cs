using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-8wpvr.2: the canvas stopped owning alert state privately. Configured alerts now route
/// through <see cref="SensorAlertStore"/>, trip state through <see cref="SensorAlertTracker"/>, and
/// the old 2-second flash timer on <see cref="CanvasCardViewModel.IsAlertActive"/> is gone — it is a
/// pure, synchronous mirror of the sensor's own live flag instead.
/// </summary>
public sealed class CanvasAlertStateTests
{
    private static CanvasDashboardViewModel NewDashboard() =>
        // layoutService and shell are stored and never dereferenced by ApplyTelemetry or by setting
        // Sensor.Alert directly (RemEx-w9ui) — only a save (ApplySensorAlert, via the store's Changed
        // event) touches layoutService, which is why the one test that calls ApplySensorAlert below
        // builds a real one instead of reusing this helper.
        new(new ConnectionViewModel(), null!, null!, new SensorAlertStore(), new SensorAlertTracker());

    private static TelemetryPayload Reading(string id, double value) => new()
    {
        Sensors = new List<SensorReading> { new() { Id = id, Name = id, Value = value, Unit = "°C" } },
    };

    [Fact]
    public void IsAlertActiveMirrorsTheSensorLiveWithNoTimer()
    {
        var vm = NewDashboard();
        vm.ApplyTelemetry(Reading("cpu-pkg-0", 10));

        var card = vm.StagedCards.Single();
        card.Sensor!.Alert = new SensorAlert { SensorName = "cpu-pkg-0", Threshold = 90, Direction = AlertDirection.Above };

        card.IsAlertActive.Should().BeFalse();

        vm.ApplyTelemetry(Reading("cpu-pkg-0", 95));
        card.IsAlertActive.Should().BeTrue(
            "the card mirrors the sensor's live flag synchronously — there is no flash timer to wait out");

        // Recovers the instant the sensor does. The deleted flash timer used to hold the card hot for
        // a further two seconds after this; the mirror must not.
        vm.ApplyTelemetry(Reading("cpu-pkg-0", 10));
        card.IsAlertActive.Should().BeFalse(
            "the old 2-second flash held the card hot after recovery; the live mirror must not");
    }

    [Theory]
    [InlineData(AlertDirection.Above, 90, 91, 89, 87)]
    [InlineData(AlertDirection.Below, 20, 19, 20.3, 21)]
    public void ASensorHoveringAtItsThresholdDoesNotRetripEveryTick(
        AlertDirection direction, double threshold, double over, double justBack, double clearlyBack)
    {
        // Perf audit P3-57: the live flag used to clear the instant the value crossed back, so a
        // sensor sitting on its threshold re-fired AlertTriggered (a full Trip + card walk) every
        // other tick. It now clears only once the value retreats 2% of the threshold past it.
        var vm = NewDashboard();
        vm.ApplyTelemetry(Reading("hover-0", clearlyBack));
        var card = vm.StagedCards.Single();
        card.Sensor!.Alert = new SensorAlert { SensorName = "hover-0", Threshold = threshold, Direction = direction };
        var fired = 0;
        card.Sensor.AlertTriggered += (_, _) => fired++;

        for (var i = 0; i < 5; i++)
        {
            vm.ApplyTelemetry(Reading("hover-0", over));
            vm.ApplyTelemetry(Reading("hover-0", justBack));
        }
        fired.Should().Be(1, "hovering inside the deadband is one crossing, not five");
        card.IsAlertActive.Should().BeTrue("the value has not left the deadband yet");

        vm.ApplyTelemetry(Reading("hover-0", clearlyBack));
        card.IsAlertActive.Should().BeFalse("past the deadband the alert re-arms");

        vm.ApplyTelemetry(Reading("hover-0", over));
        fired.Should().Be(2, "a crossing after re-arming fires again");
    }

    [Fact]
    public void EditingTheThresholdDropsTheOldDeadband()
    {
        // The clear-band belongs to the threshold that tripped. Live at "above 80" and sitting at
        // 79.5 inside its band, then raised to 81: 79.5 never crossed 81, so the alert must clear.
        var vm = NewDashboard();
        vm.ApplyTelemetry(Reading("edit-0", 70));
        var card = vm.StagedCards.Single();
        card.Sensor!.Alert = new SensorAlert { SensorName = "edit-0", Threshold = 80, Direction = AlertDirection.Above };

        vm.ApplyTelemetry(Reading("edit-0", 85));
        vm.ApplyTelemetry(Reading("edit-0", 79.5));
        card.IsAlertActive.Should().BeTrue("79.5 is inside the 2% band of the tripped threshold");

        card.Sensor.Alert = new SensorAlert { SensorName = "edit-0", Threshold = 81, Direction = AlertDirection.Above };
        vm.ApplyTelemetry(Reading("edit-0", 79.5));
        card.IsAlertActive.Should().BeFalse("the value never crossed the new threshold");
    }

    [Fact]
    public void ATripMarksMatchingCardsTrippedUntilAcknowledged()
    {
        var vm = NewDashboard();
        vm.ApplyTelemetry(Reading("gpu-hot-0", 10));

        var card = vm.StagedCards.Single();
        card.Sensor!.Alert = new SensorAlert { SensorName = "gpu-hot-0", Threshold = 90, Direction = AlertDirection.Above };

        card.IsAlertTripped.Should().BeFalse();

        vm.ApplyTelemetry(Reading("gpu-hot-0", 95));
        card.IsAlertTripped.Should().BeTrue("crossing the threshold trips the card");

        // Recovering does not clear a trip — only acknowledgement does (state model in the design doc:
        // "Live has no interaction with Tripped").
        vm.ApplyTelemetry(Reading("gpu-hot-0", 10));
        card.IsAlertTripped.Should().BeTrue("a trip stays live until acknowledged, even after the value recovers");
        card.IsAlertActive.Should().BeFalse("Live and Tripped are independent flags");

        card.AcknowledgeAlertCommand.Execute(null);
        card.IsAlertTripped.Should().BeFalse("acknowledging clears the trip");
    }

    [Fact]
    public async Task RemovingAnAlertClearsHasAlertAndForgetsTheTrackerCooldown()
    {
        var tempDir = Directory.CreateTempSubdirectory("remex-8wpvr2-").FullName;
        try
        {
            // A real DashboardLayoutService, unlike NewDashboard()'s null! — ApplySensorAlert routes
            // through the store's Changed event, which triggers a save.
            var theme = new ThemeService { PostToUiThread = action => action() };
            var layoutService = new DashboardLayoutService(Path.Combine(tempDir, "dashboard_layout.json"), theme);
            await layoutService.LoadAsync();

            // RemEx-8wpvr.2 moved the dashboard's SensorAlertStore.Changed subscription out of the
            // constructor and into the end of InitializeAsync's load path, so a VM that never calls
            // InitializeAsync no longer wires up ApplySensorAlert's re-apply (the shape this test
            // exercises below). But InitializeAsync itself is not called here — it awaits
            // Dispatcher.UIThread.InvokeAsync, and this assembly has no Avalonia.Headless reference
            // (see DispatcherPostedWorkTests / ProfileReplacementInvalidatesCustomizationVmTests), so
            // nothing ever drains a real Post. An earlier version of this test built a real
            // ShellViewModel — whose constructor subscribes to Presence, forcing the lazy
            // PhonePresenceMonitor.Instance and its 3 s DispatcherTimer into existence (back then a
            // HardwareThemeService 5 s timer, deleted in RemEx-dbjfy, did the same job first) — and
            // awaited vm.InitializeAsync() directly; that hung, measured directly, because "the" UI thread
            // ends up bound to whichever thread anywhere in the process first touched
            // Dispatcher.UIThread, which is generally not the thread InitializeAsync's own internal
            // LoadAsync happens to resume on. Wiring the same private handler InitializeAsync would
            // have subscribed reproduces its effect on ApplySensorAlert without going anywhere near
            // the dispatcher — shell can stay null! the way NewDashboard()'s does, since nothing
            // reached here dereferences it.
            var connection = new ConnectionViewModel();
            var alertStore = new SensorAlertStore();
            var alertTracker = new SensorAlertTracker();
            var vm = new CanvasDashboardViewModel(connection, layoutService, null!, alertStore, alertTracker);

            var onAlertStoreChanged = typeof(CanvasDashboardViewModel)
                .GetMethod("OnAlertStoreChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
            alertStore.Changed += (Action)Delegate.CreateDelegate(typeof(Action), vm, onAlertStoreChanged);

            vm.ApplyTelemetry(Reading("cpu-pkg-0", 10));

            var card = vm.StagedCards.Single();
            var sensorName = card.Sensor!.Name;

            vm.ApplySensorAlert(sensorName, new SensorAlert { SensorName = sensorName, Threshold = 90, Direction = AlertDirection.Above });
            card.HasAlert.Should().BeTrue();

            var fired = new List<double>();
            vm.SensorAlertFired += (_, value) => fired.Add(value);

            vm.ApplyTelemetry(Reading(sensorName, 95));
            fired.Should().ContainSingle("the first crossing must always notify");

            // Remove the alert. HasAlert must clear on the card, and — the RemEx-8wpvr.1 review
            // addendum — the tracker must FORGET the sensor, not just acknowledge it, so the
            // notification cooldown does not survive the alert that set it.
            vm.ApplySensorAlert(sensorName, null);
            card.HasAlert.Should().BeFalse("removing the alert clears HasAlert on every matching card");
            card.IsAlertTripped.Should().BeFalse("removing an alert also acknowledges/forgets its trip");

            // Recover, then re-configure and re-cross. If Forget had not cleared the cooldown, this
            // second crossing would be silently suppressed for 60 seconds and never reach `fired`.
            vm.ApplyTelemetry(Reading(sensorName, 10));
            vm.ApplySensorAlert(sensorName, new SensorAlert { SensorName = sensorName, Threshold = 90, Direction = AlertDirection.Above });
            vm.ApplyTelemetry(Reading(sensorName, 95));

            fired.Should().HaveCount(2,
                "a re-configured alert must notify fresh; a stale cooldown from the removed alert must "
                + "not carry over (RemEx-8wpvr.1 review addendum: Forget, not just Acknowledge)");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void SensorCatalogListsCanvasAndStagedSensors()
    {
        var vm = NewDashboard();
        vm.Connection.IsConnected = true; // IsConnected also gates on the live host connection (RemEx-8wpvr.2 review round 2, MEDIUM)
        vm.ApplyTelemetry(Reading("cpu-pkg-0", 10)); // never placed — stays in the staging drawer

        ISensorCatalog catalog = vm;

        catalog.Known.Should().ContainSingle(s => s.Name == "cpu-pkg-0",
            "a sensor that only exists in the staging drawer must still be in the catalog");

        // A second sensor placed directly on the canvas (Cards), not staged.
        var placedSensor = new SensorViewModel();
        placedSensor.Update(new SensorReading { Id = "gpu-hot-0", Name = "GPU Hotspot", Value = 40, Unit = "°C" });
        vm.Cards.Add(new CanvasCardViewModel { CardType = "Sensor", Sensor = placedSensor });

        catalog.Known.Should().Contain(s => s.Name == "GPU Hotspot" && s.Unit == "°C",
            "the catalog is sourced from Cards as well as StagedCards");

        catalog.TryResolve("gpu-hot-0", out _).Should().BeFalse("resolution is by sensor Name, not the host id");
        catalog.TryResolve("GPU Hotspot", out var info).Should().BeTrue();
        info!.IsConnected.Should().BeTrue("a freshly-added, non-stale card is currently present");
    }

    /// <summary>
    /// RemEx-8wpvr.2 (review, MEDIUM): <c>IsConnected</c> used to be <c>g.Any(c => !c.IsStale)</c>, but
    /// a placed card is never stale by contract — so a sensor that was placed but had never actually
    /// reported a reading read as connected forever. It must instead reflect whether the sensor has
    /// received a live reading via the telemetry path (<see cref="SensorViewModel.Update"/>, which
    /// sets <see cref="SensorViewModel.RawReading"/>).
    /// </summary>
    /// <remarks>
    /// Review round 2 (HIGH... no, MEDIUM) found this was STILL monotonic: <c>RawReading is not
    /// null</c> is set on the first reading and never cleared, so a sensor that had ever reported once
    /// read as connected forever even after the host dropped. The fix ANDs in
    /// <see cref="ConnectionViewModel.IsConnected"/> — see the third case below.
    /// </remarks>
    [Fact]
    public void IsConnectedReflectsWhetherTheSensorHasEverReportedAReading()
    {
        var vm = NewDashboard();

        // Placed directly on the canvas, like a restored card whose sensor hasn't reported yet — no
        // Update() call, so RawReading is still null.
        var neverReported = new SensorViewModel { Name = "never-reported" };
        vm.Cards.Add(new CanvasCardViewModel { CardType = "Sensor", Sensor = neverReported });

        ISensorCatalog catalog = vm;
        vm.Connection.IsConnected = true;
        catalog.TryResolve("never-reported", out var info).Should().BeTrue();
        info!.IsConnected.Should().BeFalse(
            "a placed card is never IsStale, so IsConnected must not be derived from staleness — a " +
            "sensor that has never actually reported a reading must not read as connected, even while " +
            "the host is connected");

        // The same sensor instance, once telemetry has actually arrived for it, while still connected.
        neverReported.Update(new SensorReading { Id = "never-reported", Name = "never-reported", Value = 1, Unit = "°C" });

        catalog.TryResolve("never-reported", out info).Should().BeTrue();
        info!.IsConnected.Should().BeTrue("the sensor has now received a live reading and the host is connected");

        // The host drops. A reported reading alone must no longer read as connected — RawReading is
        // never cleared, so without gating on the live connection this stayed true forever.
        vm.Connection.IsConnected = false;

        catalog.TryResolve("never-reported", out info).Should().BeTrue();
        info!.IsConnected.Should().BeFalse(
            "the host disconnected — a previously-reported reading must not keep reading as connected");
    }

    /// <summary>
    /// RemEx-8wpvr.2 (review, MEDIUM): <c>CanvasCardViewModel.OnSensorChanged</c> subscribes to its
    /// sensor's <c>PropertyChanged</c> but nothing unsubscribed on removal — <see cref="SensorViewModel"/>
    /// instances live for the whole session, so a removed card stayed rooted and kept mirroring the
    /// sensor's alert state forever. <c>Cards.Remove</c> call sites must now also call
    /// <see cref="CanvasCardViewModel.Detach"/>.
    /// </summary>
    [Fact]
    public async Task RemovingACardStopsItMirroringItsSensorsAlertState()
    {
        var tempDir = Directory.CreateTempSubdirectory("remex-8wpvr2-detach-").FullName;
        try
        {
            // A real DashboardLayoutService, unlike NewDashboard()'s null! — RemoveSelectedCommand
            // unconditionally calls TriggerSave, which dereferences it.
            var theme = new ThemeService { PostToUiThread = action => action() };
            var layoutService = new DashboardLayoutService(Path.Combine(tempDir, "dashboard_layout.json"), theme);
            await layoutService.LoadAsync();

            var vm = new CanvasDashboardViewModel(
                new ConnectionViewModel(), layoutService, null!, new SensorAlertStore(), new SensorAlertTracker());
            var sensor = new SensorViewModel();
            sensor.Update(new SensorReading { Id = "cpu-pkg-0", Name = "cpu-pkg-0", Value = 10, Unit = "°C" });
            var card = new CanvasCardViewModel { CardType = "Sensor", Sensor = sensor };
            vm.Cards.Add(card);

            vm.ToggleCardSelection(card);
            vm.RemoveSelectedCommand.Execute(null);
            vm.Cards.Should().NotContain(card, "RemoveSelected removed it from the canvas");

            // The sensor lives on for the rest of the session (it may still be in StagedCards, or
            // referenced by a fresh placed card) - raising its PropertyChanged here stands in for any
            // later activity on it.
            sensor.IsAlertActive = true;

            card.IsAlertActive.Should().BeFalse(
                "a removed card must be detached from its sensor - it must not keep mirroring " +
                "PropertyChanged after Cards.Remove");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
