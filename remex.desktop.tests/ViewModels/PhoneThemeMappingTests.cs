using System;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Core.Services.Theme;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// <c>CustomizationViewModel.TryMapPhoneTheme</c> and the "Match my phone" command it feeds
/// (RemEx-sudp8) — join side of RemEx-y06a0.1's Kotlin sender.
/// </summary>
public class PhoneThemeMappingTests
{
    private static PhoneThemeSnapshot Snapshot(
        string seed = "#6750A4", string style = "tonal_spot", string mode = "dark", double contrast = 0.5) => new()
    {
        SeedHex = seed,
        Style = style,
        Mode = mode,
        Contrast = contrast,
        DynamicColor = false,
        SentAtUnixMs = 1_700_000_000_000,
    };

    // ── Style table (RemEx-sudp8 contract update: case-insensitive, underscore-stripped; content ──
    // and spritz are Android-internal names, never user-facing, and fall through to TonalSpot too). ──
    [Theory]
    [InlineData("tonal_spot", SchemeVariants.TonalSpot)]
    [InlineData("vibrant", SchemeVariants.Vibrant)]
    [InlineData("expressive", SchemeVariants.Expressive)]
    [InlineData("rainbow", SchemeVariants.Rainbow)]
    [InlineData("fruit_salad", SchemeVariants.FruitSalad)]
    [InlineData("neutral", SchemeVariants.Neutral)]
    [InlineData("monochrome", SchemeVariants.Monochrome)]
    [InlineData("TONAL_SPOT", SchemeVariants.TonalSpot)] // case-insensitive
    [InlineData("FruitSalad", SchemeVariants.FruitSalad)] // already-PascalCase also matches
    [InlineData("content", SchemeVariants.Content)] // material 1.14's SchemeContent — a real variant on both ends since RemEx-4kv0g.13
    [InlineData("fidelity", SchemeVariants.Fidelity)]
    [InlineData("spritz", SchemeVariants.TonalSpot)] // Android-internal, not user-facing -> fallback
    [InlineData("something-else-entirely", SchemeVariants.TonalSpot)] // unknown -> fallback
    [InlineData("", SchemeVariants.TonalSpot)]
    public void StyleMapsToTheExpectedSchemeVariant(string phoneStyle, string expectedVariant)
    {
        var (_, schemeVariant, _, _) = CustomizationViewModel.TryMapPhoneTheme(Snapshot(style: phoneStyle));
        schemeVariant.Should().Be(expectedVariant);
    }

    [Theory]
    [InlineData("light", ThemeModes.Light)]
    [InlineData("dark", ThemeModes.Dark)]
    [InlineData("system", ThemeModes.System)]
    public void RecognisedModesMapToThePcConstant(string phoneMode, string expectedMode)
    {
        var (_, _, mode, _) = CustomizationViewModel.TryMapPhoneTheme(Snapshot(mode: phoneMode));
        mode.Should().Be(expectedMode);
    }

    [Theory]
    [InlineData("SYSTEM")]
    [InlineData("")]
    [InlineData("sideways")]
    public void AnUnrecognisedModeMapsToNullSoTheAxisIsLeftAlone(string badMode)
    {
        var (_, _, mode, _) = CustomizationViewModel.TryMapPhoneTheme(Snapshot(mode: badMode));
        mode.Should().BeNull();
    }

    [Theory]
    [InlineData("#6750A4", "#6750A4")]
    [InlineData("not-a-hex-color", null)]
    [InlineData("#GGGGGG", null)]
    public void SeedIsValidatedIndependentlyOfTheAgent(string phoneSeed, string? expectedSeed)
    {
        // Defense in depth: the agent already validates the hex before storing, but this mapper does
        // not trust that a stored value is still well-formed by construction.
        var (seedHex, _, _, _) = CustomizationViewModel.TryMapPhoneTheme(Snapshot(seed: phoneSeed));
        seedHex.Should().Be(expectedSeed);
    }

    // ── Contrast: Android's M3 range is -1.0..1.0 (signed) and so is the PC's (ContrastCurve on both ends since ──
    // RemEx-4kv0g). Only the range is guarded; a negative value is a genuine reduced-contrast scheme, not a 0. ──
    [Theory]
    [InlineData(-1.0, -1.0)]
    [InlineData(-0.5, -0.5)]
    [InlineData(0.0, 0.0)]
    [InlineData(0.7, 0.7)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.2, 1.0)]
    [InlineData(-1.7, -1.0)]
    public void ContrastTravelsSignedAndIsOnlyRangeGuarded(double phoneContrast, double expectedContrast)
    {
        var (_, _, _, contrast) = CustomizationViewModel.TryMapPhoneTheme(Snapshot(contrast: phoneContrast));
        contrast.Should().Be(expectedContrast);
    }

    private CustomizationViewModel MakeVm(IPhoneThemeSnapshotStore? store)
    {
        App.EmbeddedHostServices = store is null ? null : new SingleServiceProvider(store);
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = new DashboardLayoutService(theme);
        return new CustomizationViewModel(null!, layout, theme);
    }

    [Fact]
    public void HasPhoneThemeIsFalseWithNoHostOrNoSnapshot()
    {
        var savedHost = App.EmbeddedHostServices;
        try
        {
            MakeVm(store: null).HasPhoneTheme.Should().BeFalse("no embedded host means no store to ask");

            var store = new FakePhoneThemeSnapshotStore();
            MakeVm(store).HasPhoneTheme.Should().BeFalse("a store with no synced snapshot yet has nothing to offer");
        }
        finally
        {
            App.EmbeddedHostServices = savedHost;
        }
    }

    [Fact]
    public void HasPhoneThemeBecomesTrueOnceASnapshotExists()
    {
        var savedHost = App.EmbeddedHostServices;
        try
        {
            var store = new FakePhoneThemeSnapshotStore { Latest = Snapshot() };
            MakeVm(store).HasPhoneTheme.Should().BeTrue();
        }
        finally
        {
            App.EmbeddedHostServices = savedHost;
        }
    }

    [Fact]
    public void MatchPhoneThemeWritesAllFourAxesThroughApplyAndSaveAndPersists()
    {
        var savedHost = App.EmbeddedHostServices;
        DashboardLayoutService? layoutService = null;
        try
        {
            App.EmbeddedHostServices = new SingleServiceProvider(
                new FakePhoneThemeSnapshotStore { Latest = Snapshot() });
            var theme = new ThemeService { PostToUiThread = action => action() };
            layoutService = new DashboardLayoutService(theme);
            var vm = new CustomizationViewModel(null!, layoutService, theme);

            vm.MatchPhoneThemeCommand.Execute(null);

            vm.AccentColor.Should().Be("#6750A4");
            vm.SchemeVariant.Should().Be(SchemeVariants.TonalSpot);
            vm.ThemeModeIndex.Should().Be(1, "index 1 is Dark");
            vm.ThemeContrast.Should().Be(0.5);
            vm.ColorSource.Should().Be(ColorSources.Custom);

            // Persisted, not just live - the same bar SavedPalettesBehaviourTests holds ApplySavedPalette to.
            var persisted = layoutService.CurrentProfile.Customization;
            persisted.AccentColor.Should().Be("#6750A4");
            persisted.SchemeVariant.Should().Be(SchemeVariants.TonalSpot);
            persisted.ThemeMode.Should().Be(ThemeModes.Dark);
            persisted.ThemeContrast.Should().Be(0.5);
            persisted.ColorSource.Should().Be(ColorSources.Custom);
        }
        finally
        {
            App.EmbeddedHostServices = savedHost;
            layoutService?.Dispose();
        }
    }

    [Fact]
    public void MatchPhoneThemeWithNoSnapshotDoesNothing()
    {
        var savedHost = App.EmbeddedHostServices;
        DashboardLayoutService? layoutService = null;
        try
        {
            App.EmbeddedHostServices = new SingleServiceProvider(new FakePhoneThemeSnapshotStore());
            var theme = new ThemeService { PostToUiThread = action => action() };
            layoutService = new DashboardLayoutService(theme);
            var vm = new CustomizationViewModel(null!, layoutService, theme);
            var before = vm.AccentColor;

            vm.MatchPhoneThemeCommand.Execute(null);

            vm.AccentColor.Should().Be(before);
        }
        finally
        {
            App.EmbeddedHostServices = savedHost;
            layoutService?.Dispose();
        }
    }

    /// <summary>The smallest container that answers one question, matching <c>AboutHostFingerprintTests</c>.</summary>
    private sealed class SingleServiceProvider(IPhoneThemeSnapshotStore store) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IPhoneThemeSnapshotStore) ? store : null;
    }

    private sealed class FakePhoneThemeSnapshotStore : IPhoneThemeSnapshotStore
    {
        public PhoneThemeSnapshot? Latest { get; set; }
        public event Action<PhoneThemeSnapshot?>? Changed;
        public void Set(PhoneThemeSnapshot snapshot)
        {
            Latest = snapshot;
            Changed?.Invoke(snapshot);
        }
    }
}
