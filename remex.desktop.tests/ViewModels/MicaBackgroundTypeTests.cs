using System;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Mica's return to the background-mode picker (RemEx-rq0xl). Gated on
/// <see cref="MicaBackdrop.IsSupported"/> so this suite behaves correctly whether or not the CI/dev
/// box is Windows 11 22H2+ — the same platform-fallback mechanism
/// <see cref="BackgroundFallbackDoesNotPersistTests"/> already pins for an arbitrary unsupported
/// material also has to hold for a persisted Mica on a machine that cannot show it.
/// </summary>
public class MicaBackgroundTypeTests : IDisposable
{
    private DashboardLayoutService? _layoutService;

    public void Dispose() => _layoutService?.Dispose();

    private (CustomizationViewModel Vm, DashboardProfile Seeded) MakeVmWithMica()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = _layoutService = new DashboardLayoutService(theme);

        var seeded = new DashboardProfile
        {
            Customization = new CustomizationSettings { BackgroundMaterial = "Mica" },
        };
        layout.RequestSave(seeded);

        var vm = new CustomizationViewModel(null!, layout, theme);
        return (vm, seeded);
    }

    [Fact]
    public void AvailableBackgroundTypes_OffersMicaBetweenWallpaperAndAcrylic_OnlyWhenSupported()
    {
        var (vm, _) = MakeVmWithMica();

        if (OperatingSystem.IsWindows() && MicaBackdrop.IsSupported)
        {
            vm.AvailableBackgroundTypes.Should().Contain("Mica");

            var wallpaperIndex = vm.AvailableBackgroundTypes.IndexOf("Wallpaper");
            var micaIndex = vm.AvailableBackgroundTypes.IndexOf("Mica");
            micaIndex.Should().BeGreaterThan(wallpaperIndex,
                "Mica sits between Wallpaper and Acrylic in the picker");

            if (vm.AvailableBackgroundTypes.Contains("Acrylic"))
            {
                var acrylicIndex = vm.AvailableBackgroundTypes.IndexOf("Acrylic");
                micaIndex.Should().BeLessThan(acrylicIndex);
            }
        }
        else
        {
            vm.AvailableBackgroundTypes.Should().NotContain("Mica",
                "Mica must not be offered where MicaBackdrop cannot ask DWM for it");
        }
    }

    [Fact]
    public void APersistedMica_SurvivesWhenSupported_OrFallsBackWithoutBeingOverwritten()
    {
        var (vm, seeded) = MakeVmWithMica();

        if (OperatingSystem.IsWindows() && MicaBackdrop.IsSupported)
        {
            vm.CanvasBackgroundType.Should().Be("Mica",
                "a supported platform must show the profile's real, persisted choice");
            _layoutService!.CurrentProfile.Should().BeSameAs(seeded,
                "showing a supported material must never trigger a save");
        }
        else
        {
            vm.CanvasBackgroundType.Should().Be("Aurora",
                "Mica is unsupported here, so it falls back for display exactly like any other " +
                "unavailable material (RemEx-k7891)");
            _layoutService!.CurrentProfile.Should().BeSameAs(seeded,
                "the constructor's platform fallback must never call ApplyAndSave / RequestSave");
            _layoutService.CurrentProfile.Customization.BackgroundMaterial.Should().Be("Mica",
                "the profile's real, persisted choice must survive a session-only display fallback");
        }
    }

    [Fact]
    public void APickOfMica_RoundTripsThroughBuildCurrentSettings()
    {
        // A direct pick bypasses RefreshBackgroundTypes' platform gate entirely (the same as any
        // other CanvasBackgroundType write), so this exercises BuildCurrentSettings' persistence
        // path regardless of whether this machine can actually render Mica.
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = _layoutService = new DashboardLayoutService(theme);
        var vm = new CustomizationViewModel(null!, layout, theme);

        vm.CanvasBackgroundType = "Mica";

        vm.CanvasBackgroundType.Should().Be("Mica");
        layout.CurrentProfile.Customization.BackgroundMaterial.Should().Be("Mica",
            "BuildCurrentSettings writes BackgroundMaterial = CanvasBackgroundType verbatim");
    }
}
