using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            var vm = new CanvasDashboardViewModel(
                new ConnectionViewModel(), layoutService, null!, new SensorAlertStore(), new SensorAlertTracker());
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
}
