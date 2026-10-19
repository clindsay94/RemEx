using System.Globalization;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The Set-alert dialog shows the sensor's friendly name, unit and live reading, and a new alert
/// starts its threshold at the current value.
/// </summary>
public class SetAlertViewModelTests
{
    private static SetAlertViewModel Create(SensorAlert? existing = null, string? displayName = null,
        string? unit = null, double? currentValue = null) =>
        new("cpu.temp", existing, _ => { }, displayName, unit, currentValue);

    [Fact]
    public void ANewAlertStartsItsThresholdAtTheRoundedLiveValue()
    {
        Create(currentValue: 63.4).Threshold.Should().Be(63);
    }

    [Fact]
    public void ANewAlertWithoutALiveValueStartsAtZero()
    {
        Create().Threshold.Should().Be(0);
    }

    [Fact]
    public void EditingKeepsTheExistingThresholdEvenWithALiveValue()
    {
        var existing = new SensorAlert { SensorName = "cpu.temp", Threshold = 90 };

        var vm = Create(existing, currentValue: 41.2);

        vm.Threshold.Should().Be(90);
        vm.HasExisting.Should().BeTrue();
    }

    [Fact]
    public void HasExistingIsFalseForANewAlert()
    {
        Create().HasExisting.Should().BeFalse();
    }

    [Fact]
    public void TheHeaderUsesTheFriendlyNameAndFallsBackToTheRawOne()
    {
        Create(displayName: "CPU Package").HeaderName.Should().Be("CPU Package");
        Create().HeaderName.Should().Be("cpu.temp");
        Create(displayName: "  ").HeaderName.Should().Be("cpu.temp");
    }

    [Fact]
    public void TheCurrentReadingIsNullWithoutAValueAndFormattedWithTheUnitOtherwise()
    {
        Create(unit: "°C").CurrentReadingText.Should().BeNull();

        var vm = Create(unit: "°C", currentValue: 63.4);
        vm.CurrentReadingText.Should().Be(SensorReadingFormat.FormatReading(63.4, "°C"));
        vm.CurrentReadingText.Should().Contain("°C").And.Contain(63.4.ToString("F1", LocalizationService.Instance.Culture));
        vm.Unit.Should().Be("°C");
    }
}
