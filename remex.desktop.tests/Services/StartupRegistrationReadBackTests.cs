using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins that "could not check" is not reported as "off" (RemEx-h5lr).
/// </summary>
/// <remarks>
/// <para>
/// The launch-at-login switch is the one control in Settings that is BOTH a display and a write.
/// That is what makes a false negative worse than a stale label: told their PC will not start RemEx
/// at sign-in, a user flips the switch — and that flip issues a real registration against a state
/// nobody ever established.
/// </para>
/// <para>
/// The old read collapsed every failure to <c>false</c>: an EDR blocking <c>schtasks</c>, an
/// unavailable Task Scheduler endpoint, an unreadable <c>~/.config/autostart</c>. RemEx-q0j7 is a
/// real instance of the resulting drift, and <c>SystemReadinessProbe</c> already documents this
/// exact swallowing as the reason its autostart probe takes a <c>bool?</c>.
/// </para>
/// </remarks>
public class StartupRegistrationReadBackTests
{
    /// <summary>A service whose every answer is dictated by the test.</summary>
    private sealed class FakeStartup : IStartupRegistrationService
    {
        public bool? Registered { get; set; } = true;
        public bool IsSupported { get; set; } = true;

        /// <summary>Every value SetEnabled was called with, so a redundant write cannot hide.</summary>
        public List<bool> Writes { get; } = [];

        public bool IsEnabled() => TryIsEnabled() == true;

        public bool? TryIsEnabled() => Registered;

        public void SetEnabled(bool enabled) => Writes.Add(enabled);
    }

    [Fact]
    public void AnUnanswerableQueryIsNotTheSameAsNotRegistered()
    {
        // THE DISTINCTION THE WHOLE CHANGE IS ABOUT. Null and false are different facts, and the
        // two-valued view must be the one that loses information, not the other way round.
        var probe = new FakeStartup { Registered = null };

        Assert.Null(probe.TryIsEnabled());
        Assert.False(probe.IsEnabled());
    }

    [Fact]
    public void TheTwoValuedViewAgreesWithTheThreeValuedOneWhereverItCan()
    {
        // IsEnabled is kept for callers that genuinely cannot act on "do not know", so it must stay
        // a faithful narrowing rather than a second implementation that can drift.
        Assert.True(new FakeStartup { Registered = true }.IsEnabled());
        Assert.False(new FakeStartup { Registered = false }.IsEnabled());
        Assert.False(new FakeStartup { Registered = null }.IsEnabled());
    }

    [Fact]
    public void SeedingTakesTheRealStateWhenItIsKnown()
    {
        // Calls the PRODUCTION rule. Review caught the first version of these tests asserting
        // against a private reimplementation inside this file - the same "the test exercises a
        // stand-in" gap mutation testing had already found once in this change, on the schtasks
        // mapping. A copy of the logic in the test verifies the copy.
        Assert.Equal(
            new SettingsViewModel.LaunchAtLoginSeed(Enabled: true, StateUnknown: false),
            SettingsViewModel.SeedLaunchAtLogin(registered: true, currentToggleState: false));

        Assert.Equal(
            new SettingsViewModel.LaunchAtLoginSeed(Enabled: false, StateUnknown: false),
            SettingsViewModel.SeedLaunchAtLogin(registered: false, currentToggleState: true));
    }

    [Fact]
    public void AnUnknownStateLeavesTheSwitchWhereItWas()
    {
        // Neither state may be asserted. Forcing "off" is the drift being fixed; forcing "on" would
        // be the same lie in the other direction - and since the switch is also the write control,
        // an asserted state is one the user may then "correct" with a real registration.
        Assert.Equal(
            new SettingsViewModel.LaunchAtLoginSeed(Enabled: true, StateUnknown: true),
            SettingsViewModel.SeedLaunchAtLogin(registered: null, currentToggleState: true));

        Assert.Equal(
            new SettingsViewModel.LaunchAtLoginSeed(Enabled: false, StateUnknown: true),
            SettingsViewModel.SeedLaunchAtLogin(registered: null, currentToggleState: false));
    }

    [Fact]
    public void OnlyAnUnknownReadRaisesTheWarning()
    {
        // The warning row is the entire user-visible half of this change. Raising it for a known
        // state would cry wolf; failing to raise it for an unknown one restores the silence.
        Assert.True(SettingsViewModel.SeedLaunchAtLogin(null, false).StateUnknown);
        Assert.False(SettingsViewModel.SeedLaunchAtLogin(true, false).StateUnknown);
        Assert.False(SettingsViewModel.SeedLaunchAtLogin(false, true).StateUnknown);
    }
}
