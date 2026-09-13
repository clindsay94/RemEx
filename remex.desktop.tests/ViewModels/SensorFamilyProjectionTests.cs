using System.Collections.Generic;
using System.Linq;
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
    /// <summary>
    /// The seven properties <c>RaiseFamilyProjections</c> announces together. Named explicitly
    /// (fix round 1, RemEx-4kv0g.3.2 review) so a test can assert the FULL set fired, not merely
    /// that one of the seven did — <c>Contains(oneName)</c> would stay green even if
    /// <c>RaiseFamilyProjectionsIfFamilyChanged</c>'s change-guard accidentally dropped six of them.
    /// </summary>
    private static readonly string[] FamilyProjectionNames =
    {
        nameof(SensorViewModel.Family), nameof(SensorViewModel.IsThemed), nameof(SensorViewModel.IsCustomTheme),
        nameof(SensorViewModel.IsPrimaryFamily), nameof(SensorViewModel.IsSecondaryFamily),
        nameof(SensorViewModel.IsTertiaryFamily), nameof(SensorViewModel.IsNeutralFamily),
    };

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

        var raisedFamilyNames = raised.Where(n => FamilyProjectionNames.Contains(n)).Distinct().ToArray();
        raisedFamilyNames.Should().BeEquivalentTo(FamilyProjectionNames,
            "the family changed (Neutral → Primary), so ALL SEVEN projections must be re-announced "
            + "together — a partial raise would leave some bindings (e.g. Classes.family-primary) stuck "
            + "on the card's first Neutral paint while others updated");
    }

    [Fact]
    public void ASecondReadingWithTheSameFamily_DoesNotReRaiseTheFamilyProjections()
    {
        // Fix round 1 (RemEx-4kv0g.3.2 review): Update() runs once a second per sensor - 454 sensors
        // on Connor's machine - so re-announcing all seven family projections on every tick even when
        // nothing about the family moved is 3178 needless binding invalidations a second. This pins
        // the change-guard: a second reading of the SAME MetricKind must raise none of them.
        var sensor = new SensorViewModel();
        sensor.Update(new SensorReading { Id = "cpu", Name = "CPU Load", Kind = MetricKind.CpuLoad, Value = 10 });

        var raised = new List<string?>();
        sensor.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        sensor.Update(new SensorReading { Id = "cpu", Name = "CPU Load", Kind = MetricKind.CpuLoad, Value = 20 });

        raised.Where(n => FamilyProjectionNames.Contains(n)).Should().BeEmpty(
            "Family did not change on this tick, so none of the seven family projections should be re-announced");
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
