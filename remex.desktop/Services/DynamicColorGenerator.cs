using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using Remex.Core.Theming.Mcu;
using Remex.Desktop.Models;

namespace Remex.Desktop.Services;

/// <summary>
/// Generates a full Material 3 tonal scheme from a single seed color using the ported MCU engine
/// (<see cref="McuScheme"/>, <c>Remex.Core.Theming.Mcu</c>) — Android's own
/// <c>DynamicScheme</c>/<c>MaterialDynamicColors</c> reproduced bit-exact, not an approximation of
/// it. <c>mcu-vectors.json</c> (RemEx-4kv0g.6) is the proof: the phone's 62 role colours, byte-exact,
/// for every (seed, variant, mode, contrast) tuple <c>EveryMaterialRoleIsExactlyTheVectorScheme</c>
/// in <c>SeedPaletteTests</c> walks.
/// </summary>
/// <remarks>
/// <para>
/// THE SEED IS THE WHOLE PALETTE. Every colour the desktop shell paints comes out of this type.
/// That is the point: four hand-tuned theme dictionaries that agreed on 46 of their 53 keys were
/// four copies of one theme, and the seven keys they disagreed on were the only thing a user could
/// actually change.
/// </para>
/// <para>
/// SUCCESS AND WARNING ARE SEMANTIC AND KEEP THEIR OWN SEEDS. Green must stay green whatever the
/// user picks, so they run as separate schemes through the same variant, mode and contrast rather
/// than being derived from the user's seed. The success seed is byte-identical to Android's
/// (<c>Theme.kt:110</c>) so the two platforms agree on what "success" looks like.
/// </para>
/// </remarks>
public static class DynamicColorGenerator
{
    /// <summary>
    /// Android's success seed, verbatim from <c>remex.android/.../ui/theme/Theme.kt:110</c>.
    /// Changing it here without changing it there splits the two platforms' idea of "success".
    /// </summary>
    private const uint SuccessSeed = 0xFF386A20;

    /// <summary>
    /// The warning seed. No Android counterpart exists to copy — Android has no warning role — so
    /// this is the amber the four PC theme dictionaries already agreed on, kept so that seeding
    /// warning does not change what warning looks like today.
    /// </summary>
    private const uint WarningSeed = 0xFFF59E0B;

    public record M3Palette(
        Color Primary,
        Color OnPrimary,
        Color PrimaryContainer,
        Color OnPrimaryContainer,
        Color Secondary,
        Color OnSecondary,
        Color SecondaryContainer,
        Color OnSecondaryContainer,
        Color Tertiary,
        Color OnTertiary,
        Color Surface,
        Color SurfaceVariant,
        Color SurfaceContainerLow,
        Color SurfaceContainer,
        Color SurfaceContainerHigh,
        Color OnSurface,
        Color OnSurfaceVariant,
        Color Outline,
        Color OutlineVariant,
        Color Error,
        Color OnError,
        Color Success,
        Color OnSuccess,
        Color Warning,
        Color OnWarning,
        Color BackgroundStart,
        Color BackgroundMid,
        Color BackgroundEnd,
        MaterialRoles Roles);

    public static M3Palette Generate(Color seed, string variant = "TonalSpot", bool isDark = true, double contrast = 0.0)
    {
        contrast = Math.Clamp(contrast, -1.0, 1.0);
        var scheme = McuScheme.Create(ToArgb(seed), SchemeVariants.ToMcu(variant), isDark, contrast);
        var roles = MaterialRoles.From(scheme);

        // Success and warning are separate schemes, not roles carved out of the user's seed: a semantic colour
        // that drifts with the accent stops being semantic. Same mode and contrast as the rest of the palette —
        // but the VARIANT is always TonalSpot, matching Android (Theme.kt:109-118), regardless of the user's
        // chosen SchemeVariant (RemEx-gw3ad, gate decision 2026-09-07). Contrast now reaches these roles through
        // MCU's ContrastCurves, exactly as customColorsForScheme passes it on the phone.
        var success = McuScheme.Build(SuccessSeed, SchemeVariant.TonalSpot, isDark, contrast);
        var warning = McuScheme.Build(WarningSeed, SchemeVariant.TonalSpot, isDark, contrast);

        return new M3Palette(
            Primary:              ToColor(roles.Primary),
            OnPrimary:            ToColor(roles.OnPrimary),
            PrimaryContainer:     ToColor(roles.PrimaryContainer),
            OnPrimaryContainer:   ToColor(roles.OnPrimaryContainer),
            Secondary:            ToColor(roles.Secondary),
            OnSecondary:          ToColor(roles.OnSecondary),
            SecondaryContainer:   ToColor(roles.SecondaryContainer),
            OnSecondaryContainer: ToColor(roles.OnSecondaryContainer),
            Tertiary:             ToColor(roles.Tertiary),
            OnTertiary:           ToColor(roles.OnTertiary),
            Surface:              ToColor(roles.Surface),
            SurfaceVariant:       ToColor(roles.SurfaceVariant),
            SurfaceContainerLow:  ToColor(roles.SurfaceContainerLow),
            SurfaceContainer:     ToColor(roles.SurfaceContainer),
            SurfaceContainerHigh: ToColor(roles.SurfaceContainerHigh),
            OnSurface:            ToColor(roles.OnSurface),
            OnSurfaceVariant:     ToColor(roles.OnSurfaceVariant),
            Outline:              ToColor(roles.Outline),
            OutlineVariant:       ToColor(roles.OutlineVariant),
            Error:                ToColor(roles.Error),
            OnError:              ToColor(roles.OnError),
            Success:              ToColor(success.Primary),
            OnSuccess:            ToColor(success.OnPrimary),
            Warning:              ToColor(warning.Primary),
            OnWarning:            ToColor(warning.OnPrimary),
            // The shell's background wash — raw tones off the scheme's own palettes (RemEx-bv9bu: a hue sweep,
            // Primary → Tertiary → Neutral, not a tone sweep; see the original RemEx-bv9bu comment in history for
            // the fuller rationale). END stays neutral on purpose: ThemeService takes the dark scrim from
            // BackgroundEnd, and a scrim's job is to darken what is behind it, not to tint it.
            BackgroundStart:      ToColor(scheme.PrimaryPalette.Tone(isDark ? 20 : 82)),
            BackgroundMid:        ToColor(scheme.TertiaryPalette.Tone(isDark ? 10 : 91)),
            BackgroundEnd:        ToColor(scheme.NeutralPalette.Tone(isDark ? 0 : 100)),
            Roles:                roles);
    }

    /// <summary>The eleven tones the Material tonal scale is conventionally sampled at.</summary>
    private static readonly int[] RampTones = { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };

    /// <summary>
    /// A palette's tonal palette rendered at <see cref="RampTones"/>, for a UI that wants to show the
    /// scale itself rather than just the roles the scheme mapper picked off it.
    /// </summary>
    public record TonalRampSet(
        IReadOnlyList<(int Tone, Color Color)> Primary,
        IReadOnlyList<(int Tone, Color Color)> Secondary,
        IReadOnlyList<(int Tone, Color Color)> Tertiary,
        IReadOnlyList<(int Tone, Color Color)> Neutral);

    /// <summary>
    /// Samples the primary/secondary/tertiary/neutral tonal palettes at 0,10,…,100 for the given seed
    /// and variant. Independent of light/dark mode and contrast — a tonal palette is the raw scale a
    /// scheme mapper picks roles off, and neither mode nor contrast change what tones exist on it.
    /// </summary>
    public static TonalRampSet GenerateTonalRamps(Color seed, string variant = "TonalSpot")
    {
        // Mode and contrast do not change what tones exist on a palette, so any scheme of this seed + variant will do.
        var scheme = McuScheme.Create(ToArgb(seed), SchemeVariants.ToMcu(variant), isDark: true, contrastLevel: 0.0);
        return new TonalRampSet(
            Primary:   RampFor(scheme.PrimaryPalette),
            Secondary: RampFor(scheme.SecondaryPalette),
            Tertiary:  RampFor(scheme.TertiaryPalette),
            Neutral:   RampFor(scheme.NeutralPalette));
    }

    private static IReadOnlyList<(int Tone, Color Color)> RampFor(TonalPalette palette) =>
        RampTones.Select(tone => (tone, ToColor(palette.Tone(tone)))).ToList();

    /// <summary>The three blob colours the Aurora background mesh paints with.</summary>
    public record AuroraSet(Color Primary, Color Secondary, Color Tertiary);

    /// <summary>
    /// Aurora's colours straight off the tonal ramp (spec section 6): the primary, secondary and
    /// tertiary palettes at tone 30 over a dark surface, tone 90 over a light one — the same
    /// tones Material's containers sit at, so the mesh reads as part of the palette rather than
    /// as chrome laid over it. Contrast does not apply: these are raw tones, not role pairs.
    /// </summary>
    public static AuroraSet AuroraColors(Color seed, string variant, bool isLight)
    {
        var ramps = GenerateTonalRamps(seed, variant);
        var tone = isLight ? 90 : 30;
        return new AuroraSet(
            Primary:   ramps.Primary.First(t => t.Tone == tone).Color,
            Secondary: ramps.Secondary.First(t => t.Tone == tone).Color,
            Tertiary:  ramps.Tertiary.First(t => t.Tone == tone).Color);
    }

    /// <summary>WCAG 2.x relative-luminance contrast ratio. Alpha is ignored; every role is opaque.</summary>
    internal static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static uint ToArgb(Color c) =>
        ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static Color ToColor(uint argb) =>
        Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8)  & 0xFF),
            (byte)(argb         & 0xFF));
}
