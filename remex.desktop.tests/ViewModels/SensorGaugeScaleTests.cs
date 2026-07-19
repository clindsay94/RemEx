using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Gauge/Ring/LED fills must represent the metric's real value, not its narrow observed band —
/// the bug where a steady 19% CPU load rendered nearly full because its min/max both hovered near 19.
/// </summary>
public class SensorGaugeScaleTests
{
    private static double Fraction(SensorViewModel vm) =>
        (vm.Value - vm.MinSeenValue) / (vm.MaxSeenValue - vm.MinSeenValue);

    [Fact]
    public void PercentLoad_ScalesZeroToHundred_EvenWhenSteady()
    {
        var vm = new SensorViewModel();
        // Several steady ~19% readings — observed min/max would collapse to a narrow band.
        vm.Update(new SensorReading { Name = "CPU", Value = 18, Unit = "%", Kind = MetricKind.CpuLoad });
        vm.Update(new SensorReading { Name = "CPU", Value = 20, Unit = "%", Kind = MetricKind.CpuLoad });
        vm.Update(new SensorReading { Name = "CPU", Value = 19, Unit = "%", Kind = MetricKind.CpuLoad });

        vm.MinSeenValue.Should().Be(0);
        vm.MaxSeenValue.Should().Be(100);
        Fraction(vm).Should().BeApproximately(0.19, 0.001); // 19%, not "nearly full"
    }

    [Fact]
    public void PercentByUnitOnly_AlsoScalesZeroToHundred()
    {
        var vm = new SensorViewModel();
        vm.Update(new SensorReading { Name = "Load", Value = 42, Unit = "%", Kind = MetricKind.Unknown });

        vm.MinSeenValue.Should().Be(0);
        vm.MaxSeenValue.Should().Be(100);
        Fraction(vm).Should().BeApproximately(0.42, 0.001);
    }

    [Fact]
    public void Temperature_ScalesZeroToHundredC()
    {
        var vm = new SensorViewModel();
        vm.Update(new SensorReading { Name = "CPU Temp", Value = 55, Unit = "°C", Kind = MetricKind.CpuTempC });

        vm.MinSeenValue.Should().Be(0);
        vm.MaxSeenValue.Should().Be(100);
        Fraction(vm).Should().BeApproximately(0.55, 0.001);
    }

    [Fact]
    public void NonPercent_ScalesZeroToPeakSeen()
    {
        var vm = new SensorViewModel();
        vm.Update(new SensorReading { Name = "Fan", Value = 800, Unit = "RPM", Kind = MetricKind.FanRpm });
        vm.Update(new SensorReading { Name = "Fan", Value = 1200, Unit = "RPM", Kind = MetricKind.FanRpm });

        vm.MinSeenValue.Should().Be(0);
        vm.MaxSeenValue.Should().Be(1200);
        Fraction(vm).Should().BeApproximately(1.0, 0.001); // current == peak
    }
}
