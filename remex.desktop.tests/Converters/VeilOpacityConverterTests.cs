using System.Globalization;
using Avalonia;
using Avalonia.Styling;
using FluentAssertions;
using Remex.Desktop.Converters;
using Xunit;

namespace Remex.Desktop.Tests.Converters;

/// <summary>
/// VeilOpacityConverter is MultiplyConverter plus a Light-mode floor (RemEx-4kv0g.5.1). Dark
/// must reproduce MultiplyConverter's numbers exactly — those ceilings were measured
/// (DashboardBackdropTintTests) and a heavier dark veil is the bug that made Mica look dead.
/// Light lifts the result to <see cref="VeilOpacityConverter.LightFloor"/> so the solved light
/// Surface actually covers a dark wallpaper or backdrop instead of leaving dark onSurface ink
/// on a dark ground.
/// </summary>
public class VeilOpacityConverterTests
{
    private readonly VeilOpacityConverter _converter = VeilOpacityConverter.Instance;
    private readonly CultureInfo _culture = CultureInfo.InvariantCulture;

    private object? Convert(object? knob, object? variant, object? parameter) =>
        _converter.Convert(new[] { knob, variant }, typeof(double), parameter, _culture);

    // ── The floor is a named constant, and it is the number the audit settled on ────────
    [Fact] public void LightFloor_IsTheAgreedValue() =>
        VeilOpacityConverter.LightFloor.Should().Be(0.55,
            "the floor is what makes Light readable over a dark wallpaper; change it by " +
            "re-measuring, not by editing this assertion");

    // ── Dark: exactly MultiplyConverter, no floor ──────────────────────────────────────
    [Fact] public void Dark_Clear_ScalesDown() =>
        Convert(0.01, ThemeVariant.Dark, "0.25").Should().Be(0.0025);

    [Fact] public void Dark_Frosted_ReproducesCeiling() =>
        Convert(1.0, ThemeVariant.Dark, "0.25").Should().Be(0.25);

    [Fact] public void Dark_Midpoint_ScalesLinearly() =>
        Convert(0.5, ThemeVariant.Dark, "0.25").Should().Be(0.125);

    [Fact] public void Dark_WallpaperKnob_PassesThroughAtFactorOne() =>
        Convert(0.4, ThemeVariant.Dark, "1.0").Should().Be(0.4);

    // ── Light: max(knob × ceiling, floor) ──────────────────────────────────────────────
    [Fact] public void Light_Clear_RisesToFloor() =>
        Convert(0.01, ThemeVariant.Light, "0.25").Should().Be(VeilOpacityConverter.LightFloor);

    [Fact] public void Light_Frosted_StillRisesToFloor_BecauseCeilingIsBelowIt() =>
        Convert(1.0, ThemeVariant.Light, "0.25").Should().Be(VeilOpacityConverter.LightFloor);

    [Fact] public void Light_WallpaperKnobBelowFloor_RisesToFloor() =>
        Convert(0.3, ThemeVariant.Light, "1.0").Should().Be(VeilOpacityConverter.LightFloor);

    [Fact] public void Light_WallpaperKnobAboveFloor_KeepsTheKnob() =>
        Convert(0.9, ThemeVariant.Light, "1.0").Should().Be(0.9,
            "the floor is a minimum, not an override — a user who asked for a heavier veil gets it");

    // ── Clamping (both modes) ──────────────────────────────────────────────────────────
    [Fact] public void Dark_ResultAboveOne_ClampsToOne() =>
        Convert(2.0, ThemeVariant.Dark, "1.0").Should().Be(1.0);

    [Fact] public void Light_ResultAboveOne_ClampsToOne() =>
        Convert(2.0, ThemeVariant.Light, "1.0").Should().Be(1.0);

    [Fact] public void Dark_NegativeResult_ClampsToZero() =>
        Convert(-1.0, ThemeVariant.Dark, "0.25").Should().Be(0.0);

    // ── Bad input: fall to NO veil in Dark; Light still gets its floor ────────────────
    // An unresolved knob binding used to be handled by FallbackValue=0 on the single
    // Binding. With a MultiBinding the converter itself is the fallback, so it must return
    // 'no veil' rather than leave Opacity at its default of 1.0 (an opaque sheet).
    [Fact] public void NonDoubleKnob_Dark_ReturnsZero() =>
        Convert("not a double", ThemeVariant.Dark, "0.25").Should().Be(0.0);

    [Fact] public void UnsetKnob_Dark_ReturnsZero() =>
        Convert(AvaloniaProperty.UnsetValue, ThemeVariant.Dark, "0.25").Should().Be(0.0);

    [Fact] public void NullKnob_Dark_ReturnsZero() =>
        Convert(null, ThemeVariant.Dark, "0.25").Should().Be(0.0);

    [Fact] public void UnsetKnob_Light_StillGetsFloor() =>
        Convert(AvaloniaProperty.UnsetValue, ThemeVariant.Light, "0.25")
            .Should().Be(VeilOpacityConverter.LightFloor,
                "Light ink over a dark backdrop is the audit finding — a missing knob must " +
                "not reintroduce it");

    [Fact] public void UnsetVariant_BehavesAsDark() =>
        Convert(0.5, AvaloniaProperty.UnsetValue, "0.25").Should().Be(0.125,
            "no theme information means no floor — Dark's measured numbers are the safe default");

    [Fact] public void NullParameter_ReturnsZero_InDark() =>
        Convert(1.0, ThemeVariant.Dark, null).Should().Be(0.0);

    [Fact] public void UnparseableParameter_ReturnsZero_InDark() =>
        Convert(1.0, ThemeVariant.Dark, "not a number").Should().Be(0.0);

    [Fact] public void SingleValue_NoVariantSlot_BehavesAsDark() =>
        _converter.Convert(new object?[] { 0.5 }, typeof(double), "0.25", _culture).Should().Be(0.125,
            "a single knob with no variant slot is Dark by default, not an error");
}
