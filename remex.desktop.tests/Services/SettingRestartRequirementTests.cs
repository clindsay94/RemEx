using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins which settings tell the user they need a restart (RemEx-pbp4).
/// </summary>
/// <remarks>
/// There are zero hits repo-wide for restart-required today, so a setting that needs one looks
/// identical to one that applies live. The failure is silent and undiagnosable from the user's side:
/// they flip a switch, nothing happens, and there is no way to tell "broken" from "not started yet".
/// </remarks>
public class SettingRestartRequirementTests
{
    [Fact]
    public void SettingsTheHostReadsOnceAtStartupCarryTheChip()
    {
        Assert.Equal(SettingEffect.AfterRestart, SettingRestartRequirement.EffectOf("host.port"));
        Assert.Equal(SettingEffect.AfterRestart, SettingRestartRequirement.EffectOf("capture.backend"));
        Assert.Equal(SettingEffect.AfterRestart, SettingRestartRequirement.EffectOf("startup.startMinimized"));
    }

    [Fact]
    public void SettingsThatApplyLiveDoNot()
    {
        Assert.Equal(SettingEffect.Immediate, SettingRestartRequirement.EffectOf("appearance.theme"));
        Assert.Equal(SettingEffect.Immediate, SettingRestartRequirement.EffectOf("general.language"));
        Assert.False(SettingRestartRequirement.ShowsRestartChip("appearance.accent"));
    }

    [Fact]
    public void AnUnclassifiedSettingErrsTowardTellingTheUserSomethingIsPending()
    {
        // THE DEFAULT THAT MATTERS, and the two ways to be wrong are not equal. Wrongly showing the
        // chip costs a restart nobody needed - visible, annoying at worst. Wrongly omitting it
        // leaves the user staring at a setting that silently does nothing, which reads as a broken
        // feature and cannot be told apart from one.
        Assert.Equal(SettingEffect.AfterRestart,
            SettingRestartRequirement.EffectOf("something.nobody.classified"));
        Assert.Equal(SettingEffect.AfterRestart, SettingRestartRequirement.EffectOf(null));
        Assert.Equal(SettingEffect.AfterRestart, SettingRestartRequirement.EffectOf("   "));
    }

    [Fact]
    public void NoSettingIsInBothRegisters()
    {
        // A setting in both lists would resolve by whichever check runs first, which is a coin flip
        // dressed as a rule - and the two answers lead the user to opposite conclusions.
        Assert.Empty(SettingRestartRequirement.RestartRequiredSettings
            .Intersect(SettingRestartRequirement.LiveApplyingSettings));
    }

    [Fact]
    public void TheLiveListIsNonEmpty_SoTheSafeDefaultCannotBecomeAPermanentLie()
    {
        // Without a live-applying register, EVERY setting would carry the chip forever, users would
        // learn it means nothing, and it would stop working for the settings that genuinely need
        // it. A safe default only stays safe while something can opt out of it.
        Assert.NotEmpty(SettingRestartRequirement.LiveApplyingSettings);
    }

    [Fact]
    public void SettingIdsAreComparedOrdinally()
    {
        // Ids are stable keys, not prose. Case-insensitive matching would let a typo in one view
        // resolve to a different view's setting, and the two rows would then disagree about
        // whether a restart is needed.
        Assert.Equal(SettingEffect.AfterRestart, SettingRestartRequirement.EffectOf("Host.Port"));
        Assert.Equal(SettingEffect.AfterRestart, SettingRestartRequirement.EffectOf("APPEARANCE.THEME"));
    }

    [Fact]
    public void TheRegistersAreKeyedByIdRatherThanByLabel()
    {
        // Keying on a localized label would empty this table the moment someone translated the UI,
        // and the failure would appear only in other languages - the worst possible place for it.
        foreach (var id in SettingRestartRequirement.RestartRequiredSettings
            .Concat(SettingRestartRequirement.LiveApplyingSettings))
        {
            Assert.Contains('.', id);
            Assert.DoesNotContain(' ', id);
        }
    }

    [Fact]
    public void ShowsRestartChipAgreesWithEffectOf()
    {
        // Two entry points, one rule - a view binding the chip and a view model deciding whether to
        // offer "Restart RemEx now" must not disagree about the same row.
        foreach (var id in new[] { "host.port", "appearance.theme", "unclassified.thing", null })
        {
            Assert.Equal(
                SettingRestartRequirement.EffectOf(id) == SettingEffect.AfterRestart,
                SettingRestartRequirement.ShowsRestartChip(id));
        }
    }
}
