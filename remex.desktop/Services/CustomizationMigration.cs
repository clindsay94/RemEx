using System;
using Avalonia.Media;
using Remex.Core.Models;
using Remex.Desktop.Models;

namespace Remex.Desktop.Services;

/// <summary>
/// Brings a persisted <see cref="CustomizationSettings"/> forward to the current schema, once,
/// at load (RemEx-dbkzy).
/// </summary>
/// <remarks>
/// LOSING FOUR NAMED THEMES MUST NOT REPAINT ANYONE'S APP. Before the seed engine a theme was a
/// resource dictionary chosen by name, and the only colour a user could pick was the accent laid
/// over it. Now every surface is generated from one seed, so the same profile read literally hands
/// a 2.4 Cyber-NOC user a TonalSpot palette where they had a hand-tuned near-black one — visually a
/// different app, from an upgrade they did not ask for.
/// <para>
/// This runs on the deserialised record BEFORE the theme service ever sees it, so the app paints
/// migrated values on its first frame rather than painting wrong ones and correcting them.
/// </para>
/// <para>
/// IT MUST NEVER THROW. It sits on the startup path ahead of the first window; an exception here is
/// an app that does not open, for a reason that is a colour. Every branch below degrades to a
/// usable seed and reports the reason through <c>warning</c> instead.
/// </para>
/// </remarks>
public static class CustomizationMigration
{
    /// <summary>The schema this build writes.</summary>
    /// <remarks>
    /// Bump this ONLY together with a new migration arm, and never renumber an existing one — the
    /// value on disk is the only record of what a profile has already been through.
    /// </remarks>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The seed a profile falls back to when neither its own nor its preset's can be used.</summary>
    /// <remarks>
    /// The same constant <c>ThemeService.FallbackAccentSeed</c> uses, deliberately: two fallbacks
    /// that disagree mean the value written to disk and the value painted on screen are different
    /// colours, which is harder to diagnose than either being wrong alone.
    /// </remarks>
    public const string FallbackSeed = ThemeService.FallbackAccentSeed;

    /// <summary>
    /// WCAG AA for body text. Not a taste threshold: below this a surface and the text on it are
    /// not distinguishable, which is the failure being screened for rather than a mild one.
    /// </summary>
    private const double MinimumSurfaceContrast = 4.5;

    /// <summary>
    /// Returns <paramref name="settings"/> brought to <see cref="CurrentSchemaVersion"/>, or the
    /// same instance when it is already current.
    /// </summary>
    /// <param name="settings">The record as deserialised.</param>
    /// <param name="warning">
    /// A one-line description of anything that had to be repaired, or <c>null</c> when the profile
    /// migrated cleanly. LOGGED ONCE BY THE CALLER RATHER THAN HERE: the theme service's own
    /// unparseable-seed warning fires on every apply, i.e. on every drag of a slider, which buries
    /// the one occurrence carrying information under thousands that do not.
    /// </param>
    public static CustomizationSettings Migrate(CustomizationSettings? settings, out string? warning)
    {
        warning = null;

        if (settings is null) return new CustomizationSettings { SchemaVersion = CurrentSchemaVersion };

        // A profile from the future is not ours to rewrite. It happens by running an older build
        // over a newer build's settings file, and stamping our version onto it would destroy the
        // newer one's record of what it had already done.
        if (settings.SchemaVersion >= CurrentSchemaVersion) return settings;

        return FromPreSeedEngine(settings, ref warning) with { SchemaVersion = CurrentSchemaVersion };
    }

    /// <summary>
    /// Schema 0 → 1. A profile whose theme was a NAME becomes a profile whose theme is a seed.
    /// </summary>
    /// <remarks>
    /// EVERY FIELD FOLLOWS ONE RULE: the preset's value wins where the profile carries the record's
    /// own default, and the profile wins everywhere else. The default is the only value that cannot
    /// be told apart from "this key was never written", and before 2.4 the preset arms wrote none
    /// of these — a Cyber-NOC profile carried the default violet, the default TonalSpot and no mode
    /// at all, because the palette came from the dictionary rather than from any of them.
    /// <para>
    /// The rule misreads one shape: someone who explicitly picked a value that happens to equal the
    /// default — the default violet while on Monolith, say — is given Monolith's blue. Accepted
    /// knowingly. The alternative misreads every pre-2.4 profile, which is both the larger
    /// population and the louder failure, and there is no third signal to separate them by.
    /// </para>
    /// </remarks>
    private static CustomizationSettings FromPreSeedEngine(CustomizationSettings settings, ref string? warning)
    {
        var preset = SeedPresetCatalog.Resolve(settings.ThemeId);
        var defaults = new CustomizationSettings();

        // Dynamic pins nothing, and that is the whole of its meaning: it is the preset for a user
        // who built their own colour. Its nulls fall through to the profile's own values below, so
        // adopting from it is structurally impossible rather than merely avoided.
        var seed = ResolveSeed(settings, preset, defaults, ref warning);

        var variant = string.Equals(settings.SchemeVariant, defaults.SchemeVariant, StringComparison.Ordinal)
            ? preset.SchemeVariant ?? settings.SchemeVariant
            : settings.SchemeVariant;

        var contrast = settings.ThemeContrast == defaults.ThemeContrast
            ? preset.Contrast ?? settings.ThemeContrast
            : settings.ThemeContrast;

        // MODE BECOMES EXPLICIT HERE, and stamping it is the point rather than a side effect. Until
        // now "no useLightPalette key" was answered on every apply by looking at the preset's NAME,
        // so a user who changed their seed silently dragged the mode along with the name they were
        // no longer using. Written down once, the name stops being consulted.
        var isLight = settings.UseLightPalette ?? preset.IsLight ?? false;

        return settings with
        {
            AccentColor = seed,
            SchemeVariant = variant,
            UseLightPalette = isLight,
            ThemeContrast = Math.Clamp(contrast, -1.0, 1.0),
        };
    }

    /// <summary>
    /// The seed a legacy profile should carry: its own where that is a colour it could have chosen,
    /// the preset's where it is not.
    /// </summary>
    /// <remarks>
    /// DAMAGE IS CHECKED BEFORE DEFAULTS, because an unusable seed is not a choice to be preserved.
    /// 2.4's accent box validated the LENGTH of the hex rather than parsing it, so <c>#FF0O00</c> —
    /// a capital O for a zero — is seven characters, saveable, and survives a restart.
    /// </remarks>
    private static string ResolveSeed(
        CustomizationSettings settings, SeedPreset preset, CustomizationSettings defaults, ref string? warning)
    {
        if (!IsUsableSeed(settings.AccentColor))
        {
            var replacement = IsUsableSeed(preset.Seed) ? preset.Seed! : FallbackSeed;
            warning = $"accent '{settings.AccentColor}' is not a usable seed; fell back to {replacement}";
            return replacement;
        }

        return string.Equals(settings.AccentColor, defaults.AccentColor, StringComparison.OrdinalIgnoreCase)
            ? preset.Seed ?? settings.AccentColor
            : settings.AccentColor;
    }

    /// <summary>
    /// Whether a hex string is a seed the generator can make a readable palette from.
    /// </summary>
    /// <remarks>
    /// IN PRACTICE THIS IS A PARSE CHECK, AND THE CONTRAST HALF HAS NEVER REJECTED ANYTHING. That is
    /// worth stating plainly rather than leaving the next reader to assume it is load-bearing. It
    /// was written expecting fully transparent black to fail — <c>#00000000</c> parses, the
    /// generator reads only the RGB channels, so surely surface and its text collapse to the same
    /// colour in dark mode. Measured, they do not: a seed with no chroma builds a NEUTRAL tonal
    /// ramp, which still separates surface from on-surface. A 216-point sweep of the RGB cube
    /// rejects nothing (<c>CustomizationMigrationTests</c> pins the sweep).
    /// <para>
    /// It stays because of what it guards rather than what it filters. The legacy arm WRITES a seed
    /// into people's profiles, so a generator change that stopped guaranteeing a readable pair would
    /// have this migration stamping unreadable palettes onto users who never chose one — silently,
    /// on upgrade, with nothing throwing. Two Generate calls once per launch is a cheap price for
    /// that not being possible.
    /// </para>
    /// </remarks>
    public static bool IsUsableSeed(string? hex)
    {
        if (!Color.TryParse(hex, out var seed)) return false;

        try
        {
            // BOTH MODES, because the profile's mode is settled a few lines later and a seed that is
            // unusable in either one is a seed the user reaches by flipping a switch.
            foreach (var isDark in new[] { true, false })
            {
                var palette = DynamicColorGenerator.Generate(seed, isDark: isDark);
                if (DynamicColorGenerator.ContrastRatio(palette.Surface, palette.OnSurface) < MinimumSurfaceContrast)
                    return false;
            }
        }
        catch (Exception)
        {
            // A generator that throws on a seed is exactly the startup crash this class exists to
            // prevent, so that seed is unusable rather than the launch being unsurvivable.
            return false;
        }

        return true;
    }
}
