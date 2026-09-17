using System;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-jt6w5.5: the Text section's view-model half — load (clamped), live apply + persist through
/// the ONE existing save path, the "default → effective" labels, and a reset that restores the spec
/// table with exactly one apply-and-save while leaving the rest of Personalize alone.
/// Harness is <see cref="BackgroundFallbackDoesNotPersistTests"/>'s: a redirected
/// DashboardLayoutService seeded by RequestSave (synchronous CurrentProfile), ThemeService posting inline.
/// </summary>
public class CustomizationTypographyTests : IDisposable
{
    private DashboardLayoutService? _layoutService;

    public void Dispose() => _layoutService?.Dispose();

    private (CustomizationViewModel Vm, DashboardLayoutService Layout, ThemeService Theme) MakeVm(TypographySettings seed, double cornerRadius = 16)
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = _layoutService = new DashboardLayoutService(theme);
        layout.RequestSave(new DashboardProfile
        {
            Customization = new CustomizationSettings { Typography = seed, CornerRadius = cornerRadius },
        });
        return (new CustomizationViewModel(null!, layout, theme), layout, theme);
    }

    private static readonly TypographySettings Tuned = new()
    {
        HeadersScale = 1.4, SubtitlesScale = 1.3, BodyScale = 0.85, SmallScale = 1.2, SensorScale = 1.5,
        HeadersBold = true, SubtitlesBold = true, BodyBold = true, SmallBold = true, SensorBold = true,
        SensorTitleBackdrop = true, ShadowEnabled = false, ShadowStrength = 90,
    };

    [Fact]
    public void Constructor_LoadsTheProfilesTypography_Clamped()
    {
        var (vm, _, _) = MakeVm(Tuned with { HeadersScale = 9, ShadowStrength = 500 });

        vm.HeadersScale.Should().Be(TypographySettings.MaxScale);
        vm.SubtitlesScale.Should().Be(1.3);
        vm.BodyScale.Should().Be(0.85);
        vm.SmallScale.Should().Be(1.2);
        vm.SensorScale.Should().Be(1.5);
        vm.HeadersBold.Should().BeTrue();
        vm.SubtitlesBold.Should().BeTrue();
        vm.SensorTitleBackdrop.Should().BeTrue();
        vm.TextShadowEnabled.Should().BeFalse();
        vm.TextShadowStrength.Should().Be(TypographySettings.MaxShadowStrength);
    }

    [Fact]
    public void SizeLabels_ReadDefaultArrowEffective_FromTheReferenceMember()
    {
        var (vm, _, _) = MakeVm(TypographySettings.Default);

        vm.HeadersSizeLabel.Should().Be("20 → 20");
        vm.SubtitlesSizeLabel.Should().Be("12 → 12");
        vm.BodySizeLabel.Should().Be("14 → 14");
        vm.SmallSizeLabel.Should().Be("12 → 12");
        vm.SensorSizeLabel.Should().Be("12 → 12");

        vm.HeadersScale = 1.2;
        vm.BodyScale = 1.05;
        vm.SubtitlesScale = 1.5;

        vm.HeadersSizeLabel.Should().Be("20 → 24");
        vm.SubtitlesSizeLabel.Should().Be("12 → 18");
        vm.BodySizeLabel.Should().Be("14 → 15", "14.7 rounds away from zero");
        vm.SmallSizeLabel.Should().Be("12 → 12", "another section's slider does not move this label");
    }

    [Fact]
    public void MovingASlider_AppliesLive_AndPersistsThroughTheProfile()
    {
        var (vm, layout, theme) = MakeVm(TypographySettings.Default);

        vm.SensorScale = 1.5;
        vm.SensorBold = true;

        layout.CurrentProfile.Customization.Typography.SensorScale.Should().Be(1.5);
        layout.CurrentProfile.Customization.Typography.SensorBold.Should().BeTrue();
        theme.Typography.LastApplied!.FontSizes["Typo.SensorTitle.FontSize"].Should().Be(18, "the same ApplyAndSave call paints it");
        layout.CurrentProfile.Customization.Typography.HeadersScale.Should().Be(1.0, "untouched fields stay at their defaults");
    }

    [Fact]
    public void ShadowStrength_PersistsAsAWholeNumber()
    {
        var (vm, layout, _) = MakeVm(TypographySettings.Default);

        vm.TextShadowStrength = 72.6;

        layout.CurrentProfile.Customization.Typography.ShadowStrength.Should().Be(73);
    }

    [Fact]
    public void Reset_RestoresTheSpecTable_PersistsOnce_AndLeavesTheRestOfPersonalizeAlone()
    {
        var (vm, layout, theme) = MakeVm(Tuned, cornerRadius: 9);
        var applied = 0;
        theme.CustomizationApplied += _ => applied++;

        vm.ResetTextToDefaultsCommand.Execute(null);

        applied.Should().Be(1, "thirteen fields change but the sheet must save once, not thirteen times");
        layout.CurrentProfile.Customization.Typography.Should().Be(TypographySettings.Default);
        layout.CurrentProfile.Customization.CornerRadius.Should().Be(9, "reset touches the Text section only");
        vm.HeadersScale.Should().Be(1.0);
        vm.SubtitlesScale.Should().Be(1.0);
        vm.SubtitlesBold.Should().BeFalse();
        vm.SensorBold.Should().BeFalse();
        vm.SensorTitleBackdrop.Should().BeFalse();
        vm.TextShadowEnabled.Should().BeTrue();
        vm.TextShadowStrength.Should().Be(40);
        vm.HeadersSizeLabel.Should().Be("20 → 20");
        vm.SubtitlesSizeLabel.Should().Be("12 → 12");
    }

    [Fact]
    public void PickingAPalettePreset_DoesNotTouchTypography()
    {
        var (vm, layout, _) = MakeVm(Tuned);

        // The sheet's own "Reset to Theme Defaults" is a preset pick (ResetToDefault → SelectTheme("BaseDarkGlass"),
        // CustomizationViewModel.cs:1788-1789) and the only public entry into SelectTheme.
        vm.ResetToDefaultCommand.Execute(null);

        layout.CurrentProfile.Customization.Typography.Should().Be(Tuned, "a palette is colours (spec § Persistence)");
    }
}
