using System;
using System.Linq;
using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// VM-level behaviour for Save/Delete/Apply on the saved-palette row (RemEx-8twk0.7 fix round).
/// <see cref="SavedPalettesWiringTests"/> only pins the XAML markup for these commands; nothing
/// before this exercised what they actually do to <see cref="CustomizationViewModel.SavedPalettes"/>
/// or the persisted profile.
/// </summary>
public class SavedPalettesBehaviourTests : IDisposable
{
    // DISPOSED IN Dispose() BELOW, same reason as CustomizationPaletteImportTests: ApplyAndSave
    // (reached from every command under test here) arms a real 2s debounce timer on the shared
    // redirected dashboard_layout.json, and an undisposed one can fire mid a later test.
    private DashboardLayoutService? _layoutService;

    public void Dispose() => _layoutService?.Dispose();

    private CustomizationViewModel MakeVm()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = _layoutService = new DashboardLayoutService(theme);
        // ShellViewModel is never touched by the paths under test (only IsReducedMotion,
        // IsPaletteDragging and NavigateBack read _shell), same as CustomizationPaletteImportTests.
        return new CustomizationViewModel(null!, layout, theme);
    }

    [Fact]
    public void BlankSaveUsesTheDefaultNameAndClearsTheBox()
    {
        var vm = MakeVm();
        vm.NewPaletteName = "   ";

        vm.SaveCurrentPaletteCommand.Execute(null);

        vm.SavedPalettes.Should().ContainSingle(t => t.Name == "Palette 1");
        vm.NewPaletteName.Should().BeEmpty();
        // RequestSave writes CurrentProfile synchronously; only the disk write is debounced.
        _layoutService!.CurrentProfile.Customization.SavedPalettes
            .Should().ContainSingle(p => p.Name == "Palette 1");
    }

    [Fact]
    public void TrimmedNameIsUsedAsGiven()
    {
        var vm = MakeVm();
        vm.NewPaletteName = "  Dusk  ";

        vm.SaveCurrentPaletteCommand.Execute(null);

        vm.SavedPalettes.Should().ContainSingle(t => t.Name == "Dusk");
    }

    /// <summary>
    /// Pins LOW 1: the default name fills the smallest unused "Palette N", not <c>Count + 1</c>.
    /// With "Palette 1" and "Palette 3" present (Count == 2), the old formula names the next tile
    /// "Palette 3" too — colliding with the one already there — instead of filling the gap.
    /// </summary>
    [Fact]
    public void BlankSaveFillsTheFirstGapInDefaultNames()
    {
        var vm = MakeVm();
        vm.NewPaletteName = "Palette 1";
        vm.SaveCurrentPaletteCommand.Execute(null);
        vm.NewPaletteName = "Palette 3";
        vm.SaveCurrentPaletteCommand.Execute(null);

        vm.NewPaletteName = "   ";
        vm.SaveCurrentPaletteCommand.Execute(null);

        vm.SavedPalettes.Select(t => t.Name).Should().Contain("Palette 2");
        vm.SavedPalettes.Select(t => t.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void DeleteRemovesTheTileAndPersists()
    {
        var vm = MakeVm();
        vm.NewPaletteName = "A";
        vm.SaveCurrentPaletteCommand.Execute(null);
        vm.NewPaletteName = "B";
        vm.SaveCurrentPaletteCommand.Execute(null);
        var before = vm.SavedPalettes.Count;

        var toDelete = vm.SavedPalettes.Single(t => t.Name == "A");
        vm.DeleteSavedPaletteCommand.Execute(toDelete);

        vm.SavedPalettes.Should().HaveCount(before - 1);
        vm.SavedPalettes.Should().NotContain(t => t.Name == "A");
        _layoutService!.CurrentProfile.Customization.SavedPalettes.Should().HaveCount(before - 1);
        _layoutService.CurrentProfile.Customization.SavedPalettes.Should().NotContain(p => p.Name == "A");
    }

    /// <summary>An OS-independent stand-in for RemEx-zk5bc's OS-flip repaint, exercised through the
    /// mode picker instead: whatever repaints the tiles has to run for a deliberate mode change
    /// too, not just the live OS listener.</summary>
    [Fact]
    public void FlippingModeFromDarkToLightRepaintsTheTile()
    {
        var vm = MakeVm();
        vm.AccentColor = "#3366CC";
        vm.NewPaletteName = "Flip";
        vm.SaveCurrentPaletteCommand.Execute(null);
        var tile = vm.SavedPalettes.Single();
        var beforeColor = ((SolidColorBrush)tile.SurfaceBrush).Color;

        vm.ThemeModeIndex = 0; // default is 1 (Dark); 0 is Light

        var afterColor = ((SolidColorBrush)tile.SurfaceBrush).Color;
        afterColor.Should().NotBe(beforeColor, "the tile's recipe paints a different surface in light vs dark");
    }

    [Fact]
    public void SaveCurrentThenApplyReproducesTheSeedExactlyAfterTheSeedChanges()
    {
        var vm = MakeVm();
        vm.AccentColor = "#6C4CFF";
        var savedSeed = vm.AccentColor;
        vm.NewPaletteName = "Saved";
        vm.SaveCurrentPaletteCommand.Execute(null);
        var tile = vm.SavedPalettes.Single();

        vm.AccentColor = "#112233"; // move away from the saved seed before applying the tile back

        vm.ApplySavedPaletteCommand.Execute(tile);

        vm.AccentColor.Should().Be(savedSeed);
        vm.SchemeVariant.Should().Be(tile.Record.Strategy);
        vm.ThemeContrast.Should().Be(tile.Record.Contrast);
    }

    /// <summary>
    /// Pins MEDIUM 1: <c>CurrentAsSavedPalette</c> must store the seed's OWN (achieved) chroma, not
    /// the slider's requested one. Dragging Vibrancy to 200 at a hue/tone that cannot reach it
    /// clamps <c>AccentColor</c> immediately (<c>PushSeedToAccent</c>) but leaves <c>SeedChroma</c>
    /// at the unachievable request — exactly the RemEx-8twk0.7 review finding. Saving that state
    /// must record what the seed actually has, or applying the tile later re-pushes 200 through HCT
    /// instead of short-circuiting to the stored seed.
    /// </summary>
    [Fact]
    public void SavingAClampedVibrancyStoresTheAchievedChromaNotTheRequestedOne()
    {
        var vm = MakeVm();
        vm.AccentColor = "#6C4CFF";
        vm.SeedChroma = 200; // far beyond what this hue/tone can hold in sRGB
        var clampedSeed = vm.AccentColor; // PushSeedToAccent already clamped this
        var achieved = SeedHct.ChromaOf(clampedSeed, vm.SeedChroma);

        vm.NewPaletteName = "Clamped";
        vm.SaveCurrentPaletteCommand.Execute(null);
        var tile = vm.SavedPalettes.Single();

        tile.Record.Vibrancy.Should().Be(achieved);
        tile.Record.Vibrancy.Should().NotBe(200.0,
            "200 is the unachievable request, not what the clamped seed actually has");

        vm.AccentColor = "#112233";
        vm.ApplySavedPaletteCommand.Execute(tile);

        vm.AccentColor.Should().Be(clampedSeed);
    }

    /// <summary>
    /// Gate item (RemEx-8twk0.8 fix round, addendum): <c>OnSeedChromaChanged</c> re-enters the setter
    /// with a clamped value rather than letting <see cref="CustomizationViewModel.SeedChroma"/> report
    /// a number outside the wheel's own range while only the Vibrancy slider pins visually at
    /// <see cref="SeedHct.MaxChroma"/>.
    /// </summary>
    [Theory]
    [InlineData(200.0, SeedHct.MaxChroma)]
    [InlineData(-5.0, 0.0)]
    [InlineData(60.0, 60.0)]
    public void SeedChromaClampsToTheWheelsRangeAtTheSource(double requested, double expected)
    {
        var vm = MakeVm();
        vm.AccentColor = "#6C4CFF";

        vm.SeedChroma = requested;

        vm.SeedChroma.Should().Be(expected);
    }
}
