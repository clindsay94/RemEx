using System.Reflection;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Spec B §4/§7 (RemEx-4kv0g.3.2): a themed (follow-theme) sensor persists its <c>CardTheme</c> as
/// null rather than the "Default" preset it happens to hold, and a stored override literally named
/// "Default" — an old layout, or the retired navy preset — loads back as themed rather than being
/// applied verbatim. <c>ApplyPersistedSensorState</c> is private, same reflection seam
/// <c>CanvasDashboardViewModelApplyProfileAlertSeedTests</c> already uses for <c>ApplyProfile</c>.
/// </summary>
public class CardThemePersistenceTests
{
    private static readonly MethodInfo ApplyPersistedSensorState =
        typeof(CanvasDashboardViewModel).GetMethod("ApplyPersistedSensorState", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void AThemedSensor_WritesCardThemeAsNull()
    {
        var sensor = new SensorViewModel(); // Theme defaults to Presets[0] ("Default") — IsThemed
        var card = new CanvasCardViewModel { CardType = "Sensor", Sensor = sensor };

        var state = card.ToCardState();

        state.CardTheme.Should().BeNull("a themed card must not freeze on whichever hex the Default preset happens to carry");
    }

    [Fact]
    public void ASunsetSensor_RoundTripsWithThePreset()
    {
        var sensor = new SensorViewModel { Theme = SensorCardTheme.Presets[4] }; // Sunset
        var card = new CanvasCardViewModel { CardType = "Sensor", Sensor = sensor };

        var state = card.ToCardState();
        state.CardTheme.Should().NotBeNull();
        state.CardTheme!.Name.Should().Be("Sunset");

        var restored = new SensorViewModel();
        ApplyPersistedSensorState.Invoke(null, new object[] { restored, state });

        restored.Theme.Name.Should().Be("Sunset");
        restored.IsThemed.Should().BeFalse();
    }

    [Fact]
    public void AStoredExplicitDefaultOverride_LoadsAsThemed()
    {
        ApplyPersistedSensorState.Should().NotBeNull("ApplyPersistedSensorState moved or was renamed — this guard cannot see it");

        var state = new CardState { CardTheme = SensorCardTheme.Presets[0] }; // explicit "Default"
        var sensor = new SensorViewModel();

        ApplyPersistedSensorState.Invoke(null, new object[] { sensor, state });

        sensor.IsThemed.Should().BeTrue("a stored override named \"Default\" must resume following the theme, not freeze on it");
        sensor.Theme.Name.Should().Be("Default");
    }
}
