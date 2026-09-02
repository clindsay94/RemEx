using Avalonia.Media;
using System.Collections.Generic;
using System.Linq;
using MaterialColorUtilities.ColorAppearance;
using MaterialColorUtilities.Palettes;
using MaterialColorUtilities.Schemes;

namespace Remex.Desktop.Services;

/// <summary>
/// Generates a full Material 3 tonal scheme from a single seed color using
/// the MaterialColorUtilities library (albi005 port).
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

    /// <summary>WCAG AAA for normal text. What contrast = 1.0 aims every foreground/background pair at.</summary>
    private const double WcagAaa = 7.0;

    /// <summary>
    /// The floor for reduced contrast: WCAG AA for LARGE text. A "lower contrast" slider that can
    /// produce unreadable text is a bug with a settings entry, so the reduction stops here.
    /// </summary>
    private const double ReducedContrastFloor = 3.0;

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
        Color BackgroundEnd);

    public static M3Palette Generate(Color seed, string variant = "TonalSpot", bool isDark = true, double contrast = 0.0)
    {
        var style = StyleFor(variant);
        var core = CoreFor(ToArgb(seed), style);
        var scheme = MapScheme(core, isDark);

        // Success and warning are separate schemes, not roles carved out of the user's seed: a
        // semantic colour that drifts with the accent stops being semantic. Same style, same mode,
        // same contrast — so they track the rest of the palette in every way except hue.
        var successCore = CoreFor(SuccessSeed, style);
        var successScheme = MapScheme(successCore, isDark);
        var warningCore = CoreFor(WarningSeed, style);
        var warningScheme = MapScheme(warningCore, isDark);

        var primary = ToColor(scheme.Primary);
        var secondary = ToColor(scheme.Secondary);
        var tertiary = ToColor(scheme.Tertiary);
        var primaryContainer = ToColor(scheme.PrimaryContainer);
        var secondaryContainer = ToColor(scheme.SecondaryContainer);
        var surface = ToColor(scheme.Surface);
        var surfaceVariant = ToColor(scheme.SurfaceVariant);
        var error = ToColor(scheme.Error);
        var success = ToColor(successScheme.Primary);
        var warning = ToColor(warningScheme.Primary);

        // CONTRAST IS APPLIED TO THE PAIRS, NOT THE ROLES. A foreground only has a contrast ratio
        // relative to something; raising "contrast" by darkening everything moves both halves of
        // every pair and changes nothing. Each foreground is walked along its own tonal palette
        // until it meets a target derived from what it measures TODAY, which is why contrast = 0
        // returns this scheme untouched rather than approximately untouched.
        return new M3Palette(
            Primary:              primary,
            OnPrimary:            Contrasted(core.Primary,        ToColor(scheme.OnPrimary),            primary,            contrast),
            PrimaryContainer:     primaryContainer,
            OnPrimaryContainer:   Contrasted(core.Primary,        ToColor(scheme.OnPrimaryContainer),   primaryContainer,   contrast),
            Secondary:            secondary,
            OnSecondary:          Contrasted(core.Secondary,      ToColor(scheme.OnSecondary),          secondary,          contrast),
            SecondaryContainer:   secondaryContainer,
            OnSecondaryContainer: Contrasted(core.Secondary,      ToColor(scheme.OnSecondaryContainer), secondaryContainer, contrast),
            Tertiary:             tertiary,
            OnTertiary:           Contrasted(core.Tertiary,       ToColor(scheme.OnTertiary),           tertiary,           contrast),
            Surface:              surface,
            SurfaceVariant:       surfaceVariant,
            SurfaceContainerLow:  ToColor(scheme.SurfaceContainerLow),
            SurfaceContainer:     ToColor(scheme.SurfaceContainer),
            SurfaceContainerHigh: ToColor(scheme.SurfaceContainerHigh),
            OnSurface:            Contrasted(core.Neutral,        ToColor(scheme.OnSurface),            surface,            contrast),
            OnSurfaceVariant:     Contrasted(core.NeutralVariant, ToColor(scheme.OnSurfaceVariant),     surfaceVariant,     contrast),
            Outline:              ToColor(scheme.Outline),
            OutlineVariant:       ToColor(scheme.OutlineVariant),
            Error:                error,
            OnError:              Contrasted(core.Error,          ToColor(scheme.OnError),              error,              contrast),
            Success:              success,
            OnSuccess:            Contrasted(successCore.Primary, ToColor(successScheme.OnPrimary),     success,            contrast),
            Warning:              warning,
            OnWarning:            Contrasted(warningCore.Primary, ToColor(warningScheme.OnPrimary),     warning,            contrast),
            // The shell's background wash. Taken as raw tones rather than named roles because no M3
            // role is "the third stop of a diagonal gradient".
            //
            // A GRADIENT NEEDS TWO AXES TO SEPARATE, AND THIS ONE HAD NEITHER (RemEx-bv9bu). The
            // first pass took Primary[10] / Neutral[8] / Neutral[0]: two tone steps between the
            // first two stops, and the last two both near-zero-chroma neutrals, so the whole
            // backdrop read as flat black with one faintly tinted corner. Restated in the terms of
            // the hand-authored backdrop it replaced (#1A0A2E → #0D1B2A → #000000), the mistake was
            // reading that as a tone sweep — it is not, all three sit near tone 8 — it is a HUE
            // sweep, violet to navy to black, and hue was the one thing dropped.
            //
            // So both axes are put back. MID MOVES OFF THE NEUTRAL PALETTE ONTO TERTIARY, which is
            // the tonal palette furthest from Primary in hue by construction, giving the sweep a
            // second colour instead of a second grey; and the tones are spaced far enough apart
            // (~10 in dark, ~9 in light) that the sweep survives the two-layer compositing in
            // DashboardBackgroundControl — a 0.8-opacity base with a 0.7–0.9 pulse over it.
            // END STAYS NEUTRAL on purpose: ThemeService takes the dark scrim from BackgroundEnd,
            // and a scrim's job is to darken what is behind it, not to tint it.
            //
            // Styles whose palettes carry no chroma at all (Spritz) lose the hue axis and are
            // carried by tone alone, which is why the spacing has to stand on its own.
            BackgroundStart:      ToColor(core.Primary [isDark ? 20u : 82u]),
            BackgroundMid:        ToColor(core.Tertiary[isDark ? 10u : 91u]),
            BackgroundEnd:        ToColor(core.Neutral [isDark ?  0u : 100u]));
    }

    /// <summary>The eleven tones the Material tonal scale is conventionally sampled at.</summary>
    private static readonly uint[] RampTones = { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };

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
        var style = StyleFor(variant);
        var core = CoreFor(ToArgb(seed), style);

        return new TonalRampSet(
            Primary:   RampFor(core.Primary),
            Secondary: RampFor(core.Secondary),
            Tertiary:  RampFor(core.Tertiary),
            Neutral:   RampFor(core.Neutral));
    }

    private static IReadOnlyList<(int Tone, Color Color)> RampFor(TonalPalette palette) =>
        RampTones.Select(tone => ((int)tone, ToColor(palette[tone]))).ToList();

    private static Style StyleFor(string variant) => variant switch
    {
        "Vibrant"    => Style.Vibrant,
        "Expressive" => Style.Expressive,
        "Rainbow"    => Style.Rainbow,
        "FruitSalad" => Style.FruitSalad,
        "Content"    => Style.Content,
        "Spritz"     => Style.Spritz,
        _            => Style.TonalSpot,
    };

    private static CorePalette CoreFor(uint argb, Style style)
    {
        var core = new CorePalette();
        core.Fill(argb, style);
        return core;
    }

    private static Scheme<uint> MapScheme(CorePalette core, bool isDark) => isDark
        ? new DarkSchemeMapper().Map(core)
        : new LightSchemeMapper().Map(core);

    /// <summary>
    /// Walks <paramref name="foreground"/> along its own tonal palette until it meets a contrast
    /// target derived from <paramref name="contrast"/>, keeping the hue and chroma the scheme chose.
    /// </summary>
    /// <remarks>
    /// The target is anchored on what the pair measures TODAY, so the function is continuous through
    /// zero and returns its input unchanged at zero — the property that lets contrast be added to a
    /// shipped, visually-verified palette without moving it for users who never touch the slider.
    /// Positive contrast pulls toward AAA and never lowers the ratio; negative pushes back toward
    /// <see cref="ReducedContrastFloor"/> and never below it.
    /// </remarks>
    private static Color Contrasted(TonalPalette palette, Color foreground, Color background, double contrast)
    {
        contrast = Math.Clamp(contrast, -1.0, 1.0);
        if (Math.Abs(contrast) < 0.001) return foreground;

        double current = ContrastRatio(foreground, background);
        double fgTone = Math.Round(Hct.FromInt(ToArgb(foreground)).Tone);
        double bgTone = Hct.FromInt(ToArgb(background)).Tone;

        // Which way is "more contrast". A tie means foreground and background sit at the same tone,
        // i.e. the pair is already unreadable; break toward whichever end of the scale is further.
        int away = fgTone > bgTone ? 1 : fgTone < bgTone ? -1 : (bgTone < 50 ? 1 : -1);
        int step = contrast > 0 ? away : -away;

        // Collect the reachable tones in the direction of travel, nearest first.
        var reachable = new List<(Color Color, double Ratio)>();
        for (double tone = fgTone + step; tone >= 0 && tone <= 100; tone += step)
        {
            var candidate = ToColor(palette[(uint)tone]);
            reachable.Add((candidate, ContrastRatio(candidate, background)));
        }

        if (reachable.Count == 0) return foreground;

        // THE FAR END OF THE SLIDER IS THE BEST THIS PAIR CAN DO, NOT A FIXED RATIO. Anchoring
        // maximum contrast on AAA looked reasonable and was nearly a no-op in practice: M3's "on"
        // roles already clear 7:1 almost everywhere, so the slider would have moved one pair in ten
        // and read as broken. Interpolating toward what is actually achievable makes every position
        // on the slider do something, and still leaves contrast = 0 exactly where it was.
        double extreme = contrast > 0 ? reachable.Max(c => c.Ratio) : ReducedContrastFloor;
        double target = current + Math.Abs(contrast) * (extreme - current) * (contrast > 0 ? 1 : -1);

        if (contrast > 0)
        {
            if (target <= current) return foreground;

            (Color Color, double Ratio) best = (foreground, current);
            foreach (var candidate in reachable)
            {
                // Monotone in principle, not in practice: a tonal palette's chroma varies with tone,
                // so a step "away" can measure very slightly worse. Keep the best seen rather than
                // trusting the direction, and stop as soon as the target is met.
                if (candidate.Ratio > best.Ratio) best = candidate;
                if (candidate.Ratio >= target) return candidate.Color;
            }

            return best.Color;
        }

        // Reducing contrast is the dangerous direction — its whole purpose is to make text harder
        // to read — so the floor is enforced on the RESULT, not just on the target. Taking the first
        // tone at or under the target would sail straight past it; the last tone still above the
        // floor is the answer.
        if (current <= ReducedContrastFloor) return foreground;

        var softest = foreground;
        foreach (var candidate in reachable)
        {
            if (candidate.Ratio < ReducedContrastFloor) break;
            softest = candidate.Color;
            if (candidate.Ratio <= target) break;
        }

        return softest;
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
