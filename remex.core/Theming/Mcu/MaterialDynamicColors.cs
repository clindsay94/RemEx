// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/MaterialDynamicColors.java
namespace Remex.Core.Theming.Mcu;

/// <summary>Named colors, otherwise known as tokens, or roles, in the Material Design system.</summary>
public sealed class MaterialDynamicColors
{
    /// <summary>Optionally use fidelity on most color schemes.</summary>
    private readonly bool isExtendedFidelity;

    public MaterialDynamicColors()
    {
        isExtendedFidelity = false;
    }

    // Temporary constructor to support extended fidelity experiment.
    public MaterialDynamicColors(bool isExtendedFidelity)
    {
        this.isExtendedFidelity = isExtendedFidelity;
    }

    public DynamicColor HighestSurface(DynamicScheme s) => s.IsDark ? SurfaceBright() : SurfaceDim();

    // Compatibility Keys Colors for Android
    public DynamicColor PrimaryPaletteKeyColor()
        => DynamicColor.FromPalette("primary_palette_key_color", s => s.PrimaryPalette, s => s.PrimaryPalette.KeyColor.Tone);

    public DynamicColor SecondaryPaletteKeyColor()
        => DynamicColor.FromPalette("secondary_palette_key_color", s => s.SecondaryPalette, s => s.SecondaryPalette.KeyColor.Tone);

    public DynamicColor TertiaryPaletteKeyColor()
        => DynamicColor.FromPalette("tertiary_palette_key_color", s => s.TertiaryPalette, s => s.TertiaryPalette.KeyColor.Tone);

    public DynamicColor NeutralPaletteKeyColor()
        => DynamicColor.FromPalette("neutral_palette_key_color", s => s.NeutralPalette, s => s.NeutralPalette.KeyColor.Tone);

    public DynamicColor NeutralVariantPaletteKeyColor()
        => DynamicColor.FromPalette("neutral_variant_palette_key_color", s => s.NeutralVariantPalette, s => s.NeutralVariantPalette.KeyColor.Tone);

    public DynamicColor Background()
        => new("background", s => s.NeutralPalette, s => s.IsDark ? 6.0 : 98.0, isBackground: true, null, null, null, null);

    public DynamicColor OnBackground()
        => new("on_background", s => s.NeutralPalette, s => s.IsDark ? 90.0 : 10.0, isBackground: false,
            s => Background(), null, new ContrastCurve(3.0, 3.0, 4.5, 7.0), null);

    public DynamicColor Surface()
        => new("surface", s => s.NeutralPalette, s => s.IsDark ? 6.0 : 98.0, isBackground: true, null, null, null, null);

    public DynamicColor SurfaceDim()
        => new("surface_dim", s => s.NeutralPalette,
            s => s.IsDark ? 6.0 : new ContrastCurve(87.0, 87.0, 80.0, 75.0).Get(s.ContrastLevel),
            isBackground: true, null, null, null, null);

    public DynamicColor SurfaceBright()
        => new("surface_bright", s => s.NeutralPalette,
            s => s.IsDark ? new ContrastCurve(24.0, 24.0, 29.0, 34.0).Get(s.ContrastLevel) : 98.0,
            isBackground: true, null, null, null, null);

    public DynamicColor SurfaceContainerLowest()
        => new("surface_container_lowest", s => s.NeutralPalette,
            s => s.IsDark ? new ContrastCurve(4.0, 4.0, 2.0, 0.0).Get(s.ContrastLevel) : 100.0,
            isBackground: true, null, null, null, null);

    public DynamicColor SurfaceContainerLow()
        => new("surface_container_low", s => s.NeutralPalette,
            s => s.IsDark
                ? new ContrastCurve(10.0, 10.0, 11.0, 12.0).Get(s.ContrastLevel)
                : new ContrastCurve(96.0, 96.0, 96.0, 95.0).Get(s.ContrastLevel),
            isBackground: true, null, null, null, null);

    public DynamicColor SurfaceContainer()
        => new("surface_container", s => s.NeutralPalette,
            s => s.IsDark
                ? new ContrastCurve(12.0, 12.0, 16.0, 20.0).Get(s.ContrastLevel)
                : new ContrastCurve(94.0, 94.0, 92.0, 90.0).Get(s.ContrastLevel),
            isBackground: true, null, null, null, null);

    public DynamicColor SurfaceContainerHigh()
        => new("surface_container_high", s => s.NeutralPalette,
            s => s.IsDark
                ? new ContrastCurve(17.0, 17.0, 21.0, 25.0).Get(s.ContrastLevel)
                : new ContrastCurve(92.0, 92.0, 88.0, 85.0).Get(s.ContrastLevel),
            isBackground: true, null, null, null, null);

    public DynamicColor SurfaceContainerHighest()
        => new("surface_container_highest", s => s.NeutralPalette,
            s => s.IsDark
                ? new ContrastCurve(22.0, 22.0, 26.0, 30.0).Get(s.ContrastLevel)
                : new ContrastCurve(90.0, 90.0, 84.0, 80.0).Get(s.ContrastLevel),
            isBackground: true, null, null, null, null);

    public DynamicColor OnSurface()
        => new("on_surface", s => s.NeutralPalette, s => s.IsDark ? 90.0 : 10.0, isBackground: false,
            HighestSurface, null, new ContrastCurve(4.5, 7.0, 11.0, 21.0), null);

    public DynamicColor SurfaceVariant()
        => new("surface_variant", s => s.NeutralVariantPalette, s => s.IsDark ? 30.0 : 90.0, isBackground: true, null, null, null, null);

    public DynamicColor OnSurfaceVariant()
        => new("on_surface_variant", s => s.NeutralVariantPalette, s => s.IsDark ? 80.0 : 30.0, isBackground: false,
            HighestSurface, null, new ContrastCurve(3.0, 4.5, 7.0, 11.0), null);

    public DynamicColor InverseSurface()
        => new("inverse_surface", s => s.NeutralPalette, s => s.IsDark ? 90.0 : 20.0, isBackground: false, null, null, null, null);

    public DynamicColor InverseOnSurface()
        => new("inverse_on_surface", s => s.NeutralPalette, s => s.IsDark ? 20.0 : 95.0, isBackground: false,
            s => InverseSurface(), null, new ContrastCurve(4.5, 7.0, 11.0, 21.0), null);

    public DynamicColor Outline()
        => new("outline", s => s.NeutralVariantPalette, s => s.IsDark ? 60.0 : 50.0, isBackground: false,
            HighestSurface, null, new ContrastCurve(1.5, 3.0, 4.5, 7.0), null);

    public DynamicColor OutlineVariant()
        => new("outline_variant", s => s.NeutralVariantPalette, s => s.IsDark ? 30.0 : 80.0, isBackground: false,
            HighestSurface, null, new ContrastCurve(1.0, 1.0, 3.0, 4.5), null);

    public DynamicColor Shadow()
        => new("shadow", s => s.NeutralPalette, s => 0.0, isBackground: false, null, null, null, null);

    public DynamicColor Scrim()
        => new("scrim", s => s.NeutralPalette, s => 0.0, isBackground: false, null, null, null, null);

    public DynamicColor SurfaceTint()
        => new("surface_tint", s => s.PrimaryPalette, s => s.IsDark ? 80.0 : 40.0, isBackground: true, null, null, null, null);

    public DynamicColor Primary()
        => new("primary", s => s.PrimaryPalette,
            s => IsMonochrome(s) ? (s.IsDark ? 100.0 : 0.0) : (s.IsDark ? 80.0 : 40.0),
            isBackground: true, HighestSurface, null, new ContrastCurve(3.0, 4.5, 7.0, 7.0),
            s => new ToneDeltaPair(PrimaryContainer(), Primary(), 10.0, TonePolarity.Nearer, false));

    public DynamicColor OnPrimary()
        => new("on_primary", s => s.PrimaryPalette,
            s => IsMonochrome(s) ? (s.IsDark ? 10.0 : 90.0) : (s.IsDark ? 20.0 : 100.0),
            isBackground: false, s => Primary(), null, new ContrastCurve(4.5, 7.0, 11.0, 21.0), null);

    public DynamicColor PrimaryContainer()
        => new("primary_container", s => s.PrimaryPalette,
            s =>
            {
                if (IsFidelity(s)) return s.SourceColorHct.Tone;
                if (IsMonochrome(s)) return s.IsDark ? 85.0 : 25.0;
                return s.IsDark ? 30.0 : 90.0;
            },
            isBackground: true, HighestSurface, null, new ContrastCurve(1.0, 1.0, 3.0, 4.5),
            s => new ToneDeltaPair(PrimaryContainer(), Primary(), 10.0, TonePolarity.Nearer, false));

    public DynamicColor OnPrimaryContainer()
        => new("on_primary_container", s => s.PrimaryPalette,
            s =>
            {
                if (IsFidelity(s)) return DynamicColor.ForegroundTone(PrimaryContainer().Tone(s), 4.5);
                if (IsMonochrome(s)) return s.IsDark ? 0.0 : 100.0;
                return s.IsDark ? 90.0 : 30.0;
            },
            isBackground: false, s => PrimaryContainer(), null, new ContrastCurve(3.0, 4.5, 7.0, 11.0), null);

    public DynamicColor InversePrimary()
        => new("inverse_primary", s => s.PrimaryPalette, s => s.IsDark ? 40.0 : 80.0, isBackground: false,
            s => InverseSurface(), null, new ContrastCurve(3.0, 4.5, 7.0, 7.0), null);

    public DynamicColor Secondary()
        => new("secondary", s => s.SecondaryPalette, s => s.IsDark ? 80.0 : 40.0, isBackground: true,
            HighestSurface, null, new ContrastCurve(3.0, 4.5, 7.0, 7.0),
            s => new ToneDeltaPair(SecondaryContainer(), Secondary(), 10.0, TonePolarity.Nearer, false));

    public DynamicColor OnSecondary()
        => new("on_secondary", s => s.SecondaryPalette,
            s => IsMonochrome(s) ? (s.IsDark ? 10.0 : 100.0) : (s.IsDark ? 20.0 : 100.0),
            isBackground: false, s => Secondary(), null, new ContrastCurve(4.5, 7.0, 11.0, 21.0), null);

    public DynamicColor SecondaryContainer()
        => new("secondary_container", s => s.SecondaryPalette,
            s =>
            {
                double initialTone = s.IsDark ? 30.0 : 90.0;
                if (IsMonochrome(s)) return s.IsDark ? 30.0 : 85.0;
                if (!IsFidelity(s)) return initialTone;
                return FindDesiredChromaByTone(s.SecondaryPalette.Hue, s.SecondaryPalette.Chroma, initialTone, !s.IsDark);
            },
            isBackground: true, HighestSurface, null, new ContrastCurve(1.0, 1.0, 3.0, 4.5),
            s => new ToneDeltaPair(SecondaryContainer(), Secondary(), 10.0, TonePolarity.Nearer, false));

    public DynamicColor OnSecondaryContainer()
        => new("on_secondary_container", s => s.SecondaryPalette,
            s =>
            {
                if (IsMonochrome(s)) return s.IsDark ? 90.0 : 10.0;
                if (!IsFidelity(s)) return s.IsDark ? 90.0 : 30.0;
                return DynamicColor.ForegroundTone(SecondaryContainer().Tone(s), 4.5);
            },
            isBackground: false, s => SecondaryContainer(), null, new ContrastCurve(3.0, 4.5, 7.0, 11.0), null);

    public DynamicColor Tertiary()
        => new("tertiary", s => s.TertiaryPalette,
            s => IsMonochrome(s) ? (s.IsDark ? 90.0 : 25.0) : (s.IsDark ? 80.0 : 40.0),
            isBackground: true, HighestSurface, null, new ContrastCurve(3.0, 4.5, 7.0, 7.0),
            s => new ToneDeltaPair(TertiaryContainer(), Tertiary(), 10.0, TonePolarity.Nearer, false));

    public DynamicColor OnTertiary()
        => new("on_tertiary", s => s.TertiaryPalette,
            s => IsMonochrome(s) ? (s.IsDark ? 10.0 : 90.0) : (s.IsDark ? 20.0 : 100.0),
            isBackground: false, s => Tertiary(), null, new ContrastCurve(4.5, 7.0, 11.0, 21.0), null);

    public DynamicColor TertiaryContainer()
        => new("tertiary_container", s => s.TertiaryPalette,
            s =>
            {
                if (IsMonochrome(s)) return s.IsDark ? 60.0 : 49.0;
                if (!IsFidelity(s)) return s.IsDark ? 30.0 : 90.0;
                Hct proposedHct = s.TertiaryPalette.GetHct(s.SourceColorHct.Tone);
                return DislikeAnalyzer.FixIfDisliked(proposedHct).Tone;
            },
            isBackground: true, HighestSurface, null, new ContrastCurve(1.0, 1.0, 3.0, 4.5),
            s => new ToneDeltaPair(TertiaryContainer(), Tertiary(), 10.0, TonePolarity.Nearer, false));

    public DynamicColor OnTertiaryContainer()
        => new("on_tertiary_container", s => s.TertiaryPalette,
            s =>
            {
                if (IsMonochrome(s)) return s.IsDark ? 0.0 : 100.0;
                if (!IsFidelity(s)) return s.IsDark ? 90.0 : 30.0;
                return DynamicColor.ForegroundTone(TertiaryContainer().Tone(s), 4.5);
            },
            isBackground: false, s => TertiaryContainer(), null, new ContrastCurve(3.0, 4.5, 7.0, 11.0), null);

    public DynamicColor Error()
        => new("error", s => s.ErrorPalette, s => s.IsDark ? 80.0 : 40.0, isBackground: true,
            HighestSurface, null, new ContrastCurve(3.0, 4.5, 7.0, 7.0),
            s => new ToneDeltaPair(ErrorContainer(), Error(), 10.0, TonePolarity.Nearer, false));

    public DynamicColor OnError()
        => new("on_error", s => s.ErrorPalette, s => s.IsDark ? 20.0 : 100.0, isBackground: false,
            s => Error(), null, new ContrastCurve(4.5, 7.0, 11.0, 21.0), null);

    public DynamicColor ErrorContainer()
        => new("error_container", s => s.ErrorPalette, s => s.IsDark ? 30.0 : 90.0, isBackground: true,
            HighestSurface, null, new ContrastCurve(1.0, 1.0, 3.0, 4.5),
            s => new ToneDeltaPair(ErrorContainer(), Error(), 10.0, TonePolarity.Nearer, false));

    public DynamicColor OnErrorContainer()
        => new("on_error_container", s => s.ErrorPalette,
            s => IsMonochrome(s) ? (s.IsDark ? 90.0 : 10.0) : (s.IsDark ? 90.0 : 30.0),
            isBackground: false, s => ErrorContainer(), null, new ContrastCurve(3.0, 4.5, 7.0, 11.0), null);

    public DynamicColor PrimaryFixed()
        => new("primary_fixed", s => s.PrimaryPalette, s => IsMonochrome(s) ? 40.0 : 90.0, isBackground: true,
            HighestSurface, null, new ContrastCurve(1.0, 1.0, 3.0, 4.5),
            s => new ToneDeltaPair(PrimaryFixed(), PrimaryFixedDim(), 10.0, TonePolarity.Lighter, true));

    public DynamicColor PrimaryFixedDim()
        => new("primary_fixed_dim", s => s.PrimaryPalette, s => IsMonochrome(s) ? 30.0 : 80.0, isBackground: true,
            HighestSurface, null, new ContrastCurve(1.0, 1.0, 3.0, 4.5),
            s => new ToneDeltaPair(PrimaryFixed(), PrimaryFixedDim(), 10.0, TonePolarity.Lighter, true));

    public DynamicColor OnPrimaryFixed()
        => new("on_primary_fixed", s => s.PrimaryPalette, s => IsMonochrome(s) ? 100.0 : 10.0, isBackground: false,
            s => PrimaryFixedDim(), s => PrimaryFixed(), new ContrastCurve(4.5, 7.0, 11.0, 21.0), null);

    public DynamicColor OnPrimaryFixedVariant()
        => new("on_primary_fixed_variant", s => s.PrimaryPalette, s => IsMonochrome(s) ? 90.0 : 30.0, isBackground: false,
            s => PrimaryFixedDim(), s => PrimaryFixed(), new ContrastCurve(3.0, 4.5, 7.0, 11.0), null);

    public DynamicColor SecondaryFixed()
        => new("secondary_fixed", s => s.SecondaryPalette, s => IsMonochrome(s) ? 80.0 : 90.0, isBackground: true,
            HighestSurface, null, new ContrastCurve(1.0, 1.0, 3.0, 4.5),
            s => new ToneDeltaPair(SecondaryFixed(), SecondaryFixedDim(), 10.0, TonePolarity.Lighter, true));

    public DynamicColor SecondaryFixedDim()
        => new("secondary_fixed_dim", s => s.SecondaryPalette, s => IsMonochrome(s) ? 70.0 : 80.0, isBackground: true,
            HighestSurface, null, new ContrastCurve(1.0, 1.0, 3.0, 4.5),
            s => new ToneDeltaPair(SecondaryFixed(), SecondaryFixedDim(), 10.0, TonePolarity.Lighter, true));

    public DynamicColor OnSecondaryFixed()
        => new("on_secondary_fixed", s => s.SecondaryPalette, s => 10.0, isBackground: false,
            s => SecondaryFixedDim(), s => SecondaryFixed(), new ContrastCurve(4.5, 7.0, 11.0, 21.0), null);

    public DynamicColor OnSecondaryFixedVariant()
        => new("on_secondary_fixed_variant", s => s.SecondaryPalette, s => IsMonochrome(s) ? 25.0 : 30.0, isBackground: false,
            s => SecondaryFixedDim(), s => SecondaryFixed(), new ContrastCurve(3.0, 4.5, 7.0, 11.0), null);

    public DynamicColor TertiaryFixed()
        => new("tertiary_fixed", s => s.TertiaryPalette, s => IsMonochrome(s) ? 40.0 : 90.0, isBackground: true,
            HighestSurface, null, new ContrastCurve(1.0, 1.0, 3.0, 4.5),
            s => new ToneDeltaPair(TertiaryFixed(), TertiaryFixedDim(), 10.0, TonePolarity.Lighter, true));

    public DynamicColor TertiaryFixedDim()
        => new("tertiary_fixed_dim", s => s.TertiaryPalette, s => IsMonochrome(s) ? 30.0 : 80.0, isBackground: true,
            HighestSurface, null, new ContrastCurve(1.0, 1.0, 3.0, 4.5),
            s => new ToneDeltaPair(TertiaryFixed(), TertiaryFixedDim(), 10.0, TonePolarity.Lighter, true));

    public DynamicColor OnTertiaryFixed()
        => new("on_tertiary_fixed", s => s.TertiaryPalette, s => IsMonochrome(s) ? 100.0 : 10.0, isBackground: false,
            s => TertiaryFixedDim(), s => TertiaryFixed(), new ContrastCurve(4.5, 7.0, 11.0, 21.0), null);

    public DynamicColor OnTertiaryFixedVariant()
        => new("on_tertiary_fixed_variant", s => s.TertiaryPalette, s => IsMonochrome(s) ? 90.0 : 30.0, isBackground: false,
            s => TertiaryFixedDim(), s => TertiaryFixed(), new ContrastCurve(3.0, 4.5, 7.0, 11.0), null);

    // These colors were present in Android framework before Android U, and used by MDC controls.
    public DynamicColor ControlActivated()
        => DynamicColor.FromPalette("control_activated", s => s.PrimaryPalette, s => s.IsDark ? 30.0 : 90.0);

    public DynamicColor ControlNormal()
        => DynamicColor.FromPalette("control_normal", s => s.NeutralVariantPalette, s => s.IsDark ? 80.0 : 30.0);

    // Light mode: #1f000000, dark mode: #33ffffff. 1F hex = 31/255 = 12% alpha. 33 hex = 51/255 = 20% alpha.
    public DynamicColor ControlHighlight()
        => new("control_highlight", s => s.NeutralPalette, s => s.IsDark ? 100.0 : 0.0, isBackground: false,
            null, null, null, null, s => s.IsDark ? 0.20 : 0.12);

    public DynamicColor TextPrimaryInverse()
        => DynamicColor.FromPalette("text_primary_inverse", s => s.NeutralPalette, s => s.IsDark ? 10.0 : 90.0);

    public DynamicColor TextSecondaryAndTertiaryInverse()
        => DynamicColor.FromPalette("text_secondary_and_tertiary_inverse", s => s.NeutralVariantPalette, s => s.IsDark ? 30.0 : 80.0);

    public DynamicColor TextPrimaryInverseDisableOnly()
        => DynamicColor.FromPalette("text_primary_inverse_disable_only", s => s.NeutralPalette, s => s.IsDark ? 10.0 : 90.0);

    public DynamicColor TextSecondaryAndTertiaryInverseDisabled()
        => DynamicColor.FromPalette("text_secondary_and_tertiary_inverse_disabled", s => s.NeutralPalette, s => s.IsDark ? 10.0 : 90.0);

    public DynamicColor TextHintInverse()
        => DynamicColor.FromPalette("text_hint_inverse", s => s.NeutralPalette, s => s.IsDark ? 10.0 : 90.0);

    private bool IsFidelity(DynamicScheme scheme)
    {
        if (isExtendedFidelity && scheme.Variant != SchemeVariant.Monochrome && scheme.Variant != SchemeVariant.Neutral) return true;
        return scheme.Variant == SchemeVariant.Fidelity || scheme.Variant == SchemeVariant.Content;
    }

    private static bool IsMonochrome(DynamicScheme scheme) => scheme.Variant == SchemeVariant.Monochrome;

    internal static double FindDesiredChromaByTone(double hue, double chroma, double tone, bool byDecreasingTone)
    {
        double answer = tone;

        Hct closestToChroma = Hct.From(hue, chroma, tone);
        if (closestToChroma.Chroma < chroma)
        {
            double chromaPeak = closestToChroma.Chroma;
            while (closestToChroma.Chroma < chroma)
            {
                answer += byDecreasingTone ? -1.0 : 1.0;
                Hct potentialSolution = Hct.From(hue, chroma, answer);
                if (chromaPeak > potentialSolution.Chroma) break;
                if (Math.Abs(potentialSolution.Chroma - chroma) < 0.4) break;

                double potentialDelta = Math.Abs(potentialSolution.Chroma - chroma);
                double currentDelta = Math.Abs(closestToChroma.Chroma - chroma);
                if (potentialDelta < currentDelta) closestToChroma = potentialSolution;
                chromaPeak = Math.Max(chromaPeak, potentialSolution.Chroma);
            }
        }

        return answer;
    }
}
