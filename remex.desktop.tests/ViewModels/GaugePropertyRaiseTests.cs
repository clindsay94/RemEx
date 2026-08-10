using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// A telemetry tick announces only the gauge properties that actually moved (RemEx-atgvl).
/// </summary>
/// <remarks>
/// <para>
/// Every tick used to raise <c>MinSeenValue</c>, <c>MaxSeenValue</c>, <c>ResolvedGraphType</c> and
/// <c>IsDualMetric</c> unconditionally, per sensor. Each raise invalidates the bindings on every
/// visible card reading it, so twenty cards meant eighty invalidations a second to redeliver values
/// already on screen.
/// </para>
/// <para>
/// **COUNTING RAISES IS THE ONLY WAY TO SEE THIS.** Nothing about the rendered result changes — the
/// values were always correct, they were just announced when they had not moved — so a test that
/// asserted on state would pass identically before and after. What changed is how often the
/// announcement happens, and that is what these subscribe to.
/// </para>
/// </remarks>
public class GaugePropertyRaiseTests
{
    private static CanvasDashboardViewModel NewDashboard() =>
        // layoutService and shell are stored and never dereferenced by this path (RemEx-w9ui).
        new(new ConnectionViewModel(), null!, null!);

    private static TelemetryPayload Tick(double value, string unit = "%") =>
        new()
        {
            Sensors = [new SensorReading { Id = "cpu", Name = "CPU Load", Value = value, Unit = unit }],
        };

    /// <summary>Every property name announced by the sensor, in order.</summary>
    private static (SensorViewModel Sensor, List<string> Raised) Watch(CanvasDashboardViewModel vm)
    {
        var sensor = vm.StagedCards.Select(c => c.Sensor).First(s => s is not null)!;
        var raised = new List<string>();
        sensor.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
        return (sensor, raised);
    }

    [Fact]
    public void ASteadySensorAnnouncesNoneOfTheGaugeProperties()
    {
        // THE COMMON CASE BY A LONG WAY. A machine at idle reports the same load second after second,
        // and none of these four can have moved - so the tick should say nothing about them.
        var vm = NewDashboard();
        vm.ApplyTelemetry(Tick(19));
        var (_, raised) = Watch(vm);

        vm.ApplyTelemetry(Tick(19));
        vm.ApplyTelemetry(Tick(19));

        raised.Should().NotContain("MaxSeenValue");
        raised.Should().NotContain("ResolvedGraphType");
        raised.Should().NotContain("IsDualMetric");
    }

    [Fact]
    public void MinSeenValueIsNeverAnnouncedAtAllBecauseItCannotChange()
    {
        // **ITS WHOLE BODY IS `=> 0`.** The remark on it explains why: a gauge that rescaled to its
        // own observed floor would render a CPU sitting steadily at 19% as full. So this raise never
        // once carried a different number, on any tick, in the life of the app. Asserted across a
        // rising value precisely because that is the case where a reader would expect a floor to move.
        var vm = NewDashboard();
        vm.ApplyTelemetry(Tick(10));
        var (sensor, raised) = Watch(vm);

        vm.ApplyTelemetry(Tick(50));
        vm.ApplyTelemetry(Tick(90));

        raised.Should().NotContain("MinSeenValue");
        sensor.MinSeenValue.Should().Be(0, "the constant is the point - if this ever moves, the raise has to come back");
    }

    [Fact]
    public void ANewPeakOnANonPercentMetricISAnnounced()
    {
        // THE FLOOR FOR THE WHOLE SUITE. Without this, an implementation that simply stopped raising
        // MaxSeenValue would satisfy every other assertion here while freezing every gauge's ceiling
        // at whatever it happened to be when the card was created.
        var vm = NewDashboard();
        vm.ApplyTelemetry(Tick(100, unit: "RPM"));
        var (sensor, raised) = Watch(vm);

        vm.ApplyTelemetry(Tick(2000, unit: "RPM"));

        raised.Should().Contain("MaxSeenValue");
        sensor.MaxSeenValue.Should().Be(2000);
    }

    [Fact]
    public void APercentMetricNeverAnnouncesAMaxBecauseItsCeilingIsFixedAtOneHundred()
    {
        // Percent and temperature metrics pin MaxSeenValue at 100 regardless of what arrives, so a
        // rising load cannot move it - which is most sensors on a typical dashboard, and the reason
        // the unconditional raise was almost always silent work.
        var vm = NewDashboard();
        vm.ApplyTelemetry(Tick(20));
        var (sensor, raised) = Watch(vm);

        vm.ApplyTelemetry(Tick(95));

        raised.Should().NotContain("MaxSeenValue");
        sensor.MaxSeenValue.Should().Be(100);
    }

    [Fact]
    public void TheValueItselfIsStillAnnouncedEveryTick()
    {
        // **ANTI-VACUITY, AND THE ONE THAT MATTERS MOST HERE.** Every assertion above is a negative.
        // A change that stopped raising anything at all - or a tick that silently stopped reaching the
        // sensor - would satisfy all of them and leave a dashboard frozen. This is the positive that
        // says the tick is still arriving and still saying so.
        var vm = NewDashboard();
        vm.ApplyTelemetry(Tick(19));
        var (_, raised) = Watch(vm);

        vm.ApplyTelemetry(Tick(42));

        raised.Should().Contain("Value");
    }
}
