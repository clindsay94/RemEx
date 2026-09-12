// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/DynamicColor.java
namespace Remex.Core.Theming.Mcu;

/// <summary>
/// A color that adjusts itself based on UI state, represented by DynamicScheme. Colors without backgrounds do not
/// change tone when contrast changes. Colors with backgrounds become closer to their background as contrast lowers,
/// and further when contrast increases.
/// </summary>
public sealed class DynamicColor
{
    public string Name { get; }
    public Func<DynamicScheme, TonalPalette> Palette { get; }
    public Func<DynamicScheme, double> Tone { get; }
    public bool IsBackground { get; }
    public Func<DynamicScheme, DynamicColor>? Background { get; }
    public Func<DynamicScheme, DynamicColor>? SecondBackground { get; }
    public ContrastCurve? ContrastCurve { get; }
    public Func<DynamicScheme, ToneDeltaPair>? ToneDeltaPair { get; }
    public Func<DynamicScheme, double>? Opacity { get; }

    private readonly Dictionary<DynamicScheme, Hct> hctCache = new(ReferenceEqualityComparer.Instance);

    public DynamicColor(
        string name,
        Func<DynamicScheme, TonalPalette> palette,
        Func<DynamicScheme, double> tone,
        bool isBackground,
        Func<DynamicScheme, DynamicColor>? background,
        Func<DynamicScheme, DynamicColor>? secondBackground,
        ContrastCurve? contrastCurve,
        Func<DynamicScheme, ToneDeltaPair>? toneDeltaPair)
        : this(name, palette, tone, isBackground, background, secondBackground, contrastCurve, toneDeltaPair, null)
    {
    }

    public DynamicColor(
        string name,
        Func<DynamicScheme, TonalPalette> palette,
        Func<DynamicScheme, double> tone,
        bool isBackground,
        Func<DynamicScheme, DynamicColor>? background,
        Func<DynamicScheme, DynamicColor>? secondBackground,
        ContrastCurve? contrastCurve,
        Func<DynamicScheme, ToneDeltaPair>? toneDeltaPair,
        Func<DynamicScheme, double>? opacity)
    {
        Name = name;
        Palette = palette;
        Tone = tone;
        IsBackground = isBackground;
        Background = background;
        SecondBackground = secondBackground;
        ContrastCurve = contrastCurve;
        ToneDeltaPair = toneDeltaPair;
        Opacity = opacity;
    }

    public static DynamicColor FromPalette(string name, Func<DynamicScheme, TonalPalette> palette, Func<DynamicScheme, double> tone)
        => new(name, palette, tone, isBackground: false, background: null, secondBackground: null, contrastCurve: null, toneDeltaPair: null);

    public static DynamicColor FromPalette(string name, Func<DynamicScheme, TonalPalette> palette, Func<DynamicScheme, double> tone, bool isBackground)
        => new(name, palette, tone, isBackground, background: null, secondBackground: null, contrastCurve: null, toneDeltaPair: null);

    public static DynamicColor FromArgb(string name, uint argb)
    {
        var hct = Hct.FromInt(argb);
        var palette = TonalPalette.FromInt(argb);
        return FromPalette(name, s => palette, s => hct.Tone);
    }

    /// <summary>Returns an ARGB integer (i.e. a hex code).</summary>
    public uint GetArgb(DynamicScheme scheme)
    {
        uint argb = GetHct(scheme).ToInt();
        if (Opacity == null) return argb;
        double percentage = Opacity(scheme);
        int alpha = MathUtils.ClampInt(0, 255, (int)(long)Math.Floor(percentage * 255 + 0.5));
        return (argb & 0x00FFFFFFu) | ((uint)alpha << 24);
    }

    /// <summary>Returns an HCT object.</summary>
    public Hct GetHct(DynamicScheme scheme)
    {
        if (hctCache.TryGetValue(scheme, out var cachedAnswer)) return cachedAnswer;

        // This is crucial for aesthetics: we aren't simply the taking the standard color
        // and changing its tone for contrast. Rather, we find the tone for contrast, then
        // use the specified chroma from the palette to construct a new color.
        double tone = GetTone(scheme);
        Hct answer = Palette(scheme).GetHct(tone);
        if (hctCache.Count > 4) hctCache.Clear();
        hctCache[scheme] = answer;
        return answer;
    }

    /// <summary>Returns the tone in HCT, ranging from 0 to 100, of the resolved color given scheme.</summary>
    public double GetTone(DynamicScheme scheme)
    {
        bool decreasingContrast = scheme.ContrastLevel < 0;

        // Case 1: dual foreground, pair of colors with delta constraint.
        if (ToneDeltaPair != null)
        {
            var toneDeltaPair = ToneDeltaPair(scheme);
            DynamicColor roleA = toneDeltaPair.RoleA;
            DynamicColor roleB = toneDeltaPair.RoleB;
            double delta = toneDeltaPair.Delta;
            TonePolarity polarity = toneDeltaPair.Polarity;
            bool stayTogether = toneDeltaPair.StayTogether;

            DynamicColor bg = Background!(scheme);
            double bgTone = bg.GetTone(scheme);

            bool aIsNearer =
                polarity == TonePolarity.Nearer
                || (polarity == TonePolarity.Lighter && !scheme.IsDark)
                || (polarity == TonePolarity.Darker && scheme.IsDark);
            DynamicColor nearer = aIsNearer ? roleA : roleB;
            DynamicColor farther = aIsNearer ? roleB : roleA;
            bool amNearer = Name == nearer.Name;
            double expansionDir = scheme.IsDark ? 1 : -1;

            // 1st round: solve to min, each
            double nContrast = nearer.ContrastCurve!.Get(scheme.ContrastLevel);
            double fContrast = farther.ContrastCurve!.Get(scheme.ContrastLevel);

            // If a color is good enough, it is not adjusted.
            double nInitialTone = nearer.Tone(scheme);
            double nTone = Contrast.RatioOfTones(bgTone, nInitialTone) >= nContrast
                ? nInitialTone
                : ForegroundTone(bgTone, nContrast);
            double fInitialTone = farther.Tone(scheme);
            double fTone = Contrast.RatioOfTones(bgTone, fInitialTone) >= fContrast
                ? fInitialTone
                : ForegroundTone(bgTone, fContrast);

            if (decreasingContrast)
            {
                nTone = ForegroundTone(bgTone, nContrast);
                fTone = ForegroundTone(bgTone, fContrast);
            }

            // If constraint is not satisfied, try another round.
            if ((fTone - nTone) * expansionDir < delta)
            {
                // 2nd round: expand farther to match delta.
                fTone = MathUtils.ClampDouble(0, 100, nTone + delta * expansionDir);
                if ((fTone - nTone) * expansionDir < delta)
                {
                    // 3rd round: contract nearer to match delta.
                    nTone = MathUtils.ClampDouble(0, 100, fTone - delta * expansionDir);
                }
            }

            // Avoids the 50-59 awkward zone.
            if (50 <= nTone && nTone < 60)
            {
                if (expansionDir > 0)
                {
                    nTone = 60;
                    fTone = Math.Max(fTone, nTone + delta * expansionDir);
                }
                else
                {
                    nTone = 49;
                    fTone = Math.Min(fTone, nTone + delta * expansionDir);
                }
            }
            else if (50 <= fTone && fTone < 60)
            {
                if (stayTogether)
                {
                    if (expansionDir > 0)
                    {
                        nTone = 60;
                        fTone = Math.Max(fTone, nTone + delta * expansionDir);
                    }
                    else
                    {
                        nTone = 49;
                        fTone = Math.Min(fTone, nTone + delta * expansionDir);
                    }
                }
                else
                {
                    if (expansionDir > 0) fTone = 60;
                    else fTone = 49;
                }
            }

            return amNearer ? nTone : fTone;
        }
        else
        {
            // Case 2: No contrast pair; just solve for itself.
            double answer = Tone(scheme);

            if (Background == null) return answer; // No adjustment for colors with no background.

            double bgTone = Background(scheme).GetTone(scheme);
            double desiredRatio = ContrastCurve!.Get(scheme.ContrastLevel);

            if (Contrast.RatioOfTones(bgTone, answer) >= desiredRatio)
            {
                // Don't "improve" what's good enough.
            }
            else
            {
                // Rough improvement.
                answer = ForegroundTone(bgTone, desiredRatio);
            }

            if (decreasingContrast) answer = ForegroundTone(bgTone, desiredRatio);

            if (IsBackground && 50 <= answer && answer < 60)
            {
                // Must adjust
                answer = Contrast.RatioOfTones(49, bgTone) >= desiredRatio ? 49 : 60;
            }

            if (SecondBackground != null)
            {
                // Case 3: Adjust for dual backgrounds.
                double bgTone1 = Background(scheme).GetTone(scheme);
                double bgTone2 = SecondBackground(scheme).GetTone(scheme);

                double upper = Math.Max(bgTone1, bgTone2);
                double lower = Math.Min(bgTone1, bgTone2);

                if (Contrast.RatioOfTones(upper, answer) >= desiredRatio
                    && Contrast.RatioOfTones(lower, answer) >= desiredRatio)
                {
                    return answer;
                }

                double lightOption = Contrast.Lighter(upper, desiredRatio);
                double darkOption = Contrast.Darker(lower, desiredRatio);

                var availables = new List<double>();
                if (lightOption != -1) availables.Add(lightOption);
                if (darkOption != -1) availables.Add(darkOption);

                bool prefersLight = TonePrefersLightForeground(bgTone1) || TonePrefersLightForeground(bgTone2);
                if (prefersLight) return lightOption == -1 ? 100 : lightOption;
                if (availables.Count == 1) return availables[0];
                return darkOption == -1 ? 0 : darkOption;
            }

            return answer;
        }
    }

    /// <summary>Given a background tone, find a foreground tone, while ensuring they reach a contrast ratio that is as close to ratio as possible.</summary>
    public static double ForegroundTone(double bgTone, double ratio)
    {
        double lighterTone = Contrast.LighterUnsafe(bgTone, ratio);
        double darkerTone = Contrast.DarkerUnsafe(bgTone, ratio);
        double lighterRatio = Contrast.RatioOfTones(lighterTone, bgTone);
        double darkerRatio = Contrast.RatioOfTones(darkerTone, bgTone);
        bool preferLighter = TonePrefersLightForeground(bgTone);

        if (preferLighter)
        {
            bool negligibleDifference =
                Math.Abs(lighterRatio - darkerRatio) < 0.1 && lighterRatio < ratio && darkerRatio < ratio;
            if (lighterRatio >= ratio || lighterRatio >= darkerRatio || negligibleDifference) return lighterTone;
            else return darkerTone;
        }
        else
        {
            return darkerRatio >= ratio || darkerRatio >= lighterRatio ? darkerTone : lighterTone;
        }
    }

    /// <summary>Adjust a tone down such that white has 4.5 contrast, if the tone is reasonably close to supporting it.</summary>
    public static double EnableLightForeground(double tone)
    {
        if (TonePrefersLightForeground(tone) && !ToneAllowsLightForeground(tone)) return 49.0;
        return tone;
    }

    /// <summary>People prefer white foregrounds on ~T60-70.</summary>
    public static bool TonePrefersLightForeground(double tone) => (long)Math.Floor(tone + 0.5) < 60;

    /// <summary>Tones less than ~T50 always permit white at 4.5 contrast.</summary>
    public static bool ToneAllowsLightForeground(double tone) => (long)Math.Floor(tone + 0.5) <= 49;
}
