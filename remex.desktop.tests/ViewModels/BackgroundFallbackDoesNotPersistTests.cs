using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-k7891: <see cref="CustomizationViewModel"/>'s constructor-time platform-fallback for
/// <c>BackgroundMaterial</c> must be session-only. Before this fix, falling back went through the
/// generated <c>CanvasBackgroundType</c> setter, whose <c>OnCanvasBackgroundTypeChanged</c> partial
/// ends in <c>ApplyAndSave</c> — so opening a profile whose saved material isn't offered on this
/// platform (or a hand-edited profile with the field missing/null) silently and permanently
/// rewrote the real choice to "Aurora" on disk.
/// </summary>
public class BackgroundFallbackDoesNotPersistTests : IDisposable
{
    // DISPOSED IN Dispose() BELOW, same reason as the sibling CustomizationViewModel test files:
    // RequestSave (reached by a real pick, see the positive-control test below) arms a real 2s
    // debounce timer against the shared redirected dashboard_layout.json, and an undisposed one can
    // fire mid a later test.
    private DashboardLayoutService? _layoutService;

    public void Dispose() => _layoutService?.Dispose();

    /// <summary>
    /// Seeds <see cref="DashboardLayoutService.CurrentProfile"/> with a profile carrying
    /// <paramref name="material"/> as its <c>BackgroundMaterial</c>, then constructs a
    /// <see cref="CustomizationViewModel"/> from it — the exact shape of opening the personalization
    /// sheet on a profile written elsewhere. <c>RequestSave</c> writes <c>CurrentProfile</c>
    /// synchronously (same idiom as <c>SavedPalettesBehaviourTests</c>); only the disk write is
    /// debounced, so this needs no actual file content.
    /// </summary>
    private (CustomizationViewModel Vm, DashboardProfile Seeded) MakeVmWithMaterial(string? material)
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = _layoutService = new DashboardLayoutService(theme);

        var seeded = new DashboardProfile
        {
            Customization = new CustomizationSettings { BackgroundMaterial = material! },
        };
        layout.RequestSave(seeded);

        // ShellViewModel is never touched by RefreshBackgroundTypes (only IsReducedMotion,
        // IsPaletteDragging and NavigateBack read _shell), same as the sibling test files.
        var vm = new CustomizationViewModel(null!, layout, theme);
        return (vm, seeded);
    }

    /// <summary>A material string this build will never offer on any platform.</summary>
    private const string UnsupportedMaterial = "NotOfferedAnywhere";

    [Fact]
    public void UnsupportedMaterial_FallsBackForDisplay_WithoutPersisting()
    {
        var (vm, seeded) = MakeVmWithMaterial(UnsupportedMaterial);

        vm.CanvasBackgroundType.Should().Be("Aurora",
            "the picker has to show what the platform can actually render");

        // The strongest available proof that RefreshBackgroundTypes never saved: CurrentProfile is
        // still the exact instance this test seeded (RequestSave always writes a new one), and its
        // BackgroundMaterial is untouched.
        _layoutService!.CurrentProfile.Should().BeSameAs(seeded,
            "the constructor's platform fallback must never call ApplyAndSave / RequestSave");
        _layoutService.CurrentProfile.Customization.BackgroundMaterial.Should().Be(UnsupportedMaterial,
            "the profile's real, persisted choice must survive a session-only display fallback");
    }

    [Fact]
    public void NullMaterial_FallsBackForDisplay_WithoutPersisting()
    {
        // The hand-edited-null shape: CustomizationSettings.BackgroundMaterial defaults to "Aurora"
        // via its member initializer, so only an explicit null (as a corrupt/legacy file on disk
        // could produce) reaches AvailableBackgroundTypes.Contains as anything but a normal value.
        var (vm, seeded) = MakeVmWithMaterial(null);

        vm.CanvasBackgroundType.Should().Be("Aurora");

        _layoutService!.CurrentProfile.Should().BeSameAs(seeded,
            "a null/absent BackgroundMaterial must not be normalised onto the persisted profile either");
        _layoutService.CurrentProfile.Customization.BackgroundMaterial.Should().BeNull(
            "the migration owns normalising a null material, not the picker (out of scope for RemEx-k7891)");
    }

    [Fact]
    public void UserPickAfterTheFallback_StillSavesNormally()
    {
        var (vm, _) = MakeVmWithMaterial(UnsupportedMaterial);
        vm.CanvasBackgroundType.Should().Be("Aurora"); // the session-only fallback from construction

        vm.CanvasBackgroundType = "Wallpaper"; // a real, deliberate pick

        vm.CanvasBackgroundType.Should().Be("Wallpaper");
        _layoutService!.CurrentProfile.Customization.BackgroundMaterial.Should().Be("Wallpaper",
            "a genuine pick after the fallback must persist exactly like any other pick");
    }
}
