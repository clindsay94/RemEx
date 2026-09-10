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

        public bool IsEnabled() => TryIsEnabled() == true;

        public bool? TryIsEnabled() => Registered;

        public void SetEnabled(bool enabled) { }
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

    // ── Source guards for the write-back suppression (RemEx-p2ex) ──────────────
    //
    // A CATCH-UP REVIEW FOUND THIS UNPINNED: deleting the guard restored the original defect - every
    // first Settings open re-registering the logon task with the current Environment.ProcessPath -
    // with all tests still green. The file even declared a `Writes` list whose comment claimed "a
    // redundant write cannot hide", and no test ever read it. That list is gone.
    //
    // The handler resolves IStartupRegistrationService from the STATIC App.Services, so no unit test
    // can drive it. This module documents source reading as its last resort when the alternative is
    // no test at all (see RemexConnectionServiceContractTests, FileConflictWiringTest). Comments are
    // stripped first, so a guard cannot be satisfied by prose describing the rule.

    private static string ViewModelSource()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "remex.desktop", "ViewModels", "SettingsViewModel.cs");
        Assert.True(File.Exists(path), $"missing source: {Path.GetFullPath(path)}");

        return System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(path), @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline),
            @"(?m)//.*$", "");
    }

    [Fact]
    public void TheChangeHandlerHonoursTheSuppressionFlag()
    {
        // Without this early return, ASSIGNING the seeded value writes it straight back - the switch
        // is both the display and the write control, which is what makes seeding dangerous at all.
        Assert.Matches(
            @"partial void OnIsLaunchAtLoginEnabledChanged\(bool value\)\s*\{\s*if \(_suppressLaunchAtLoginWrite\) return;",
            ViewModelSource());
    }

    [Fact]
    public void SeedingSetsAndAlwaysClearsTheFlag()
    {
        // try/finally, not a bare pair: an exception between them would leave the flag stuck true and
        // silently disable the switch's write for the rest of the process - a failure that looks
        // like "the toggle does nothing" and points nowhere near this code.
        var source = ViewModelSource();

        Assert.Contains("_suppressLaunchAtLoginWrite = true;", source);
        Assert.Matches(@"finally\s*\{\s*_suppressLaunchAtLoginWrite = false;\s*\}", source);
    }

    [Fact]
    public void SeedingGoesThroughTheSharedRuleRatherThanReimplementingIt()
    {
        // The rule is internal production code precisely so the tests above it exercise the real
        // thing. A load path that inlined `registered ?? current` again would drift from it silently.
        Assert.Contains("SeedLaunchAtLogin(startupService.TryIsEnabled()", ViewModelSource());
    }
}
