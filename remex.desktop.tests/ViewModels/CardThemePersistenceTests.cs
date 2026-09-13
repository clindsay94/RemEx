using System.Reflection;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Spec B §4/§7 (RemEx-4kv0g.3.2), corrected 2026-09-13 after the preset wipe: a themed
/// (follow-theme) sensor persists its <c>CardTheme</c> as the explicit "Default" preset, never as
/// null — null is what a source with NO colour information carries (the layout the phone syncs
/// back), and the loader must keep the live theme for it. A stored "Default" — this writer's own
/// output, an old layout, or the retired navy preset — loads back as themed; any other preset
/// applies verbatim. <c>ApplyPersistedSensorState</c> is private, same reflection seam
/// <c>CanvasDashboardViewModelApplyProfileAlertSeedTests</c> already uses for <c>ApplyProfile</c>.
/// </summary>
public class CardThemePersistenceTests
{
    private static readonly MethodInfo ApplyPersistedSensorState =
        typeof(CanvasDashboardViewModel).GetMethod("ApplyPersistedSensorState", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void AThemedSensor_WritesCardThemeAsTheExplicitDefaultPreset_NeverNull()
    {
        var sensor = new SensorViewModel(); // Theme defaults to Presets[0] ("Default") — IsThemed
        var card = new CanvasCardViewModel { CardType = "Sensor", Sensor = sensor };

        var state = card.ToCardState();

        state.CardTheme.Should().NotBeNull(
            "null means 'no colour information' to the loader (it is what the phone's layout carries); " +
            "a themed card must say so explicitly or a round trip cannot tell 'themed' from 'unknown'");
        state.CardTheme!.Name.Should().Be(SensorCardTheme.Presets[0].Name);
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
    public void ALiveSunsetSensor_KeepsSunset_WhenTheIncomingStateCarriesNoColourInformation()
    {
        // THE 2026-09-13 PRESET WIPE. The layout the phone syncs back after every launch carries
        // CardTheme = null for every card; a loader that treated null as "reset to follow-theme"
        // erased all 44 of Connor's presets on the first sync and the writer persisted the loss.
        // null is "unknown", and unknown keeps what is live. REGRESSION-GUARDS.md pins this.
        var sensor = new SensorViewModel { Theme = SensorCardTheme.Presets[4] }; // Sunset, live
        var state = new CardState { CardTheme = null };

        ApplyPersistedSensorState.Invoke(null, new object[] { sensor, state });

        sensor.IsThemed.Should().BeFalse("null carries no colour information and must not touch a live override");
        sensor.Theme.Name.Should().Be("Sunset");
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
