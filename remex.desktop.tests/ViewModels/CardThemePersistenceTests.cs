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

    // ═══ Non-vacuous load-side coverage (whole-branch review). A SensorViewModel is shared per
    // sensor name across cards, so ApplyPersistedSensorState runs, on an import or a layout sync
    // onto a live session, against a VM that may ALREADY carry an override from before the sync/
    // import landed — not always a fresh Presets[0] VM, which is why every fixture below starts on
    // Sunset first. A test that starts from Presets[0] and asserts IsThemed afterward would pass
    // whether or not the assignment ran at all - it was already true. ═══

    [Fact]
    public void ALiveSunsetSensor_ResetsToThemed_WhenTheIncomingStateHasNoOverride()
    {
        var sensor = new SensorViewModel { Theme = SensorCardTheme.Presets[4] }; // Sunset, live
        var state = new CardState { CardTheme = null };

        ApplyPersistedSensorState.Invoke(null, new object[] { sensor, state });

        sensor.IsThemed.Should().BeTrue(
            "an import/sync with no override must reset a sensor that was already overridden, " +
            "not leave the live Sunset theme in place — this is the regression a skip-when-null shape reintroduces");
        sensor.Theme.Name.Should().Be("Default");
    }

    [Fact]
    public void ALiveSunsetSensor_ResetsToThemed_WhenTheIncomingStateIsExplicitDefault()
    {
        var sensor = new SensorViewModel { Theme = SensorCardTheme.Presets[4] }; // Sunset, live
        var state = new CardState { CardTheme = SensorCardTheme.Presets[0] }; // explicit "Default"

        ApplyPersistedSensorState.Invoke(null, new object[] { sensor, state });

        sensor.IsThemed.Should().BeTrue(
            "a stored override named \"Default\" must reset a live Sunset sensor back to themed, not freeze it on Sunset");
        sensor.Theme.Name.Should().Be("Default");
    }

    [Fact]
    public void ALiveSunsetSensor_StaysSunset_WhenTheIncomingStateRepeatsSunset()
    {
        var sensor = new SensorViewModel { Theme = SensorCardTheme.Presets[4] }; // Sunset, live
        var state = new CardState { CardTheme = SensorCardTheme.Presets[4] }; // Sunset again

        ApplyPersistedSensorState.Invoke(null, new object[] { sensor, state });

        sensor.IsThemed.Should().BeFalse("an incoming Sunset override must still apply, not be skipped because one was already live");
        sensor.Theme.Name.Should().Be("Sunset");
    }
}
