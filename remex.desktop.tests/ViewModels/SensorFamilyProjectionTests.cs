using System.Collections.Generic;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Theming;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Spec B (RemEx-4kv0g.3.2): <see cref="SensorViewModel.Family"/>/<c>IsThemed</c>/the four
/// <c>Is*Family</c> flags drive the "family-*" style classes bound in CanvasView.axaml,
/// HomeView.axaml and TrayFlyoutWindow.axaml. Before any reading arrives, <see cref="MetricKind.Unknown"/>
/// resolves to <see cref="SensorFamily.Neutral"/> (spec B §1) — a card is grey until its first
/// reading tells it what it is, which is intended, not a bug.
/// </summary>
public class SensorFamilyProjectionTests
{
    [Fact]
    public void BeforeAnyReading_TheCardIsNeutral()
    {
        var sensor = new SensorViewModel();

        sensor.Family.Should().Be(SensorFamily.Neutral);
        sensor.IsNeutralFamily.Should().BeTrue();
        sensor.IsPrimaryFamily.Should().BeFalse();
        sensor.IsSecondaryFamily.Should().BeFalse();
        sensor.IsTertiaryFamily.Should().BeFalse();
        sensor.IsThemed.Should().BeTrue("Presets[0] is the constructor default, which is the follow-theme state");
        sensor.IsCustomTheme.Should().BeFalse();
    }

    [Fact]
    public void AReadingWithAKnownKind_ResolvesTheFamily_AndFiresOnlyThatFlag()
    {
        var sensor = new SensorViewModel();

        var raised = new List<string?>();
        sensor.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        sensor.Update(new SensorReading { Id = "cpu", Name = "CPU Load", Kind = MetricKind.CpuLoad });

        sensor.Family.Should().Be(SensorFamily.Primary);
        sensor.IsPrimaryFamily.Should().BeTrue();
        sensor.IsSecondaryFamily.Should().BeFalse();
        sensor.IsTertiaryFamily.Should().BeFalse();
        sensor.IsNeutralFamily.Should().BeFalse();
        sensor.IsCustomTheme.Should().BeFalse();

        raised.Should().Contain(nameof(SensorViewModel.IsPrimaryFamily),
            "the family projections must be re-announced when RawReading is assigned, or the "
            + "Classes.family-primary binding never updates past the card's first Neutral paint");
    }

    [Fact]
    public void SettingACustomPreset_ClearsEveryFamilyFlag_AndFiresIsCustomTheme()
    {
        var sensor = new SensorViewModel();
        sensor.Update(new SensorReading { Id = "cpu", Name = "CPU Load", Kind = MetricKind.CpuLoad });

        var raised = new List<string?>();
        sensor.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        sensor.Theme = SensorCardTheme.Presets[4]; // Sunset

        sensor.IsCustomTheme.Should().BeTrue();
        sensor.IsThemed.Should().BeFalse();
        sensor.IsPrimaryFamily.Should().BeFalse();
        sensor.IsSecondaryFamily.Should().BeFalse();
        sensor.IsTertiaryFamily.Should().BeFalse();
        sensor.IsNeutralFamily.Should().BeFalse();

        raised.Should().Contain(nameof(SensorViewModel.IsPrimaryFamily),
            "IsPrimaryFamily must be re-announced when it goes false too, or the canvas card keeps "
            + "its family class after the user picks a preset");
    }

    [Fact]
    public void ApplyingDefaultAgain_RestoresTheFamily()
    {
        var sensor = new SensorViewModel();
        sensor.Update(new SensorReading { Id = "cpu", Name = "CPU Load", Kind = MetricKind.CpuLoad });
        sensor.Theme = SensorCardTheme.Presets[4]; // Sunset

        sensor.ApplyThemeCommand.Execute("Default");

        sensor.IsThemed.Should().BeTrue();
        sensor.IsCustomTheme.Should().BeFalse();
        sensor.IsPrimaryFamily.Should().BeTrue("Family is unaffected by Theme; only IsThemed toggled back");
    }
}
