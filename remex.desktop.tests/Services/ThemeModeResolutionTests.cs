using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins <see cref="ThemeService.ResolveIsLight"/>'s precedence (RemEx-zk5bc): the explicit mode
/// outranks everything, System asks the OS, and a profile with no mode resolves through the same
/// <c>UseLightPalette</c>-then-preset chain it always has.
/// </summary>
public class ThemeModeResolutionTests
{
    private static readonly SeedPreset DarkPreset = SeedPresetCatalog.Resolve("CyberNOC");
    private static readonly SeedPreset LightPreset = SeedPresetCatalog.Resolve("SolarFlare");

    [Fact]
    public void LightAndDarkPinRegardlessOfTheOsAndTheLegacyFields()
    {
        // The acceptance's second half: "Light and Dark still pin regardless of the OS." The
        // legacy bool disagreeing on purpose proves the mode outranks it rather than tying.
        var pinnedLight = new CustomizationSettings { ThemeMode = ThemeModes.Light, UseLightPalette = false };
        var pinnedDark = new CustomizationSettings { ThemeMode = ThemeModes.Dark, UseLightPalette = true };

        foreach (var os in new bool?[] { true, false, null })
        {
            ThemeService.ResolveIsLight(pinnedLight, DarkPreset, os).Should().BeTrue();
            ThemeService.ResolveIsLight(pinnedDark, LightPreset, os).Should().BeFalse();
        }
    }

    [Fact]
    public void SystemFollowsTheOs()
    {
        var system = new CustomizationSettings { ThemeMode = ThemeModes.System };

        ThemeService.ResolveIsLight(system, DarkPreset, osIsLight: true).Should().BeTrue();
        ThemeService.ResolveIsLight(system, DarkPreset, osIsLight: false).Should().BeFalse();
    }

    [Fact]
    public void SystemWithNoOsAnswerFallsBackToDark()
    {
        // A platform that cannot say (no PlatformSettings yet, headless) must not paint light by
        // accident: dark is what every profile painted before the mode existed.
        var system = new CustomizationSettings { ThemeMode = ThemeModes.System, UseLightPalette = true };

        ThemeService.ResolveIsLight(system, LightPreset, osIsLight: null).Should().BeFalse();
    }

    [Fact]
    public void ANullModeResolvesThroughTheLegacyChain()
    {
        // Explicit bool first, then the preset's own mode, then dark — byte-for-byte the
        // pre-zk5bc behaviour, which is what keeps an unmigrated profile painting unchanged.
        ThemeService.ResolveIsLight(
            new CustomizationSettings { UseLightPalette = true }, DarkPreset, null).Should().BeTrue();
        ThemeService.ResolveIsLight(
            new CustomizationSettings { UseLightPalette = false }, LightPreset, null).Should().BeFalse();
        ThemeService.ResolveIsLight(
            new CustomizationSettings(), LightPreset, null).Should().BeTrue("SolarFlare is a light preset");
        ThemeService.ResolveIsLight(
            new CustomizationSettings(), DarkPreset, null).Should().BeFalse();
    }

    [Fact]
    public void AModeFromANewerBuildResolvesThroughTheLegacyChainInsteadOfThrowing()
    {
        // The field is a string, not an enum, precisely so a newer build's value degrades to the
        // legacy answer here rather than to a deserialization failure there.
        var future = new CustomizationSettings { ThemeMode = "HighContrast", UseLightPalette = true };

        ThemeService.ResolveIsLight(future, DarkPreset, osIsLight: false).Should().BeTrue(
            "an unknown mode must fall back to the explicit legacy bool, not crash or pin dark");
    }
}
