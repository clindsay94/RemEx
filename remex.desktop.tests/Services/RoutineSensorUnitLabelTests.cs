using FluentAssertions;
using Remex.Core.Routines;
using Remex.Desktop.Services.Routines;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The PC card's sensor chip carries the limit's unit (live pass 2026-09-27: "Physical Memory Load
/// above 1" with no %, RemEx-pp0rt.16).
/// </summary>
public class RoutineSensorUnitLabelTests
{
    private static RoutineTrigger Sensor(string label, double limit, string id = "/ram/0/load/0") =>
        new() { Type = RoutineTriggerTypes.PcSensor, SensorId = id, SensorLabel = label, Direction = RoutineSensorDirections.Above, Threshold = limit };

    [Fact]
    public void APercentSitsOnTheNumberAndOtherUnitsAreSpaced()
    {
        RoutinePresentation.LimitText(90, "%").Should().Be("90%");
        RoutinePresentation.LimitText(85, "°C").Should().Be("85 °C");
        RoutinePresentation.LimitText(85, null).Should().Be("85");
    }

    [Fact]
    public void TheLiveCatalogUnitWins()
    {
        var label = RoutinePresentation.TriggerLabel(Sensor("GPU Core", 85, "/gpu/0/temperature/0"), id => id == "/gpu/0/temperature/0" ? "°F" : null);
        label.Should().Contain("GPU Core").And.EndWith("85 °F");
    }

    [Fact]
    public void WithoutTheCatalogALoadSensorStillShowsPercent()
    {
        RoutinePresentation.TriggerLabel(Sensor("Physical Memory Load", 1)).Should().EndWith("1%");
        RoutinePresentation.TriggerLabel(Sensor("CPU Package Temperature", 90, "/cpu/0/temperature/0")).Should().EndWith("90 °C");
        RoutinePresentation.TriggerLabel(Sensor("Fan #1", 1200, "/fan/0")).Should().EndWith("1200");
    }
}
