using System;
using System.Collections.Generic;
using System.Linq;

namespace Remex.Desktop.Models;

/// <summary>
/// One entry in the preset gallery: a seed, the scheme it is generated with, and the handful of
/// shape/glass numbers that make it feel like a theme rather than a recolour.
/// </summary>
/// <remarks>
/// A PRESET IS DATA NOW, NOT A PALETTE. Each of the four named themes used to be a ~100-line
/// resource dictionary; picking one swapped the dictionary. Since RemEx-07jij every colour comes out
/// of <c>DynamicColorGenerator</c> from a single seed, so a preset is only the inputs to that
/// generator plus the geometry — which is what this record is. Adding a preset is adding a row to
/// <see cref="SeedPresetCatalog.All"/> and a name to the nine .resx files; nothing else.
/// </remarks>
/// <param name="Id">
/// Stable identifier, persisted as <c>CustomizationSettings.ThemeId</c>. The four homage ids match
/// the old <see cref="AppTheme"/> member names EXACTLY, because existing profiles on disk carry
/// those strings and must keep resolving to the same preset.
/// <para>
/// THE ID IS NOT THE NAME, AND THAT SPLIT IS WHY THE RENAME COST NOTHING. Cyber-NOC became Neon,
/// Solar-Flare became Ember, Monolith became Slate and Standard Glass became Glass — all four are
/// still stored as the id they always were, so no profile needed migrating and no upgrade path
/// could silently reset someone's theme. Rename display names freely; NEVER rename an id.
/// </para>
/// </param>
/// <param name="NameKey">Localization key for the display name.</param>
/// <param name="BaseTheme">
/// Which <c>Themes/*.axaml</c> supplies the structural (non-colour) resources. New presets all sit
/// on <see cref="AppTheme.BaseDarkGlass"/> — the four theme files are near-identical since the
/// palettes moved out, and the remaining differences are geometry this record already carries.
/// </param>
/// <param name="Seed">
/// The accent hex the palette is generated from, or <c>null</c> for "keep whatever seed the user
/// already has". Only Dynamic uses null: it is the preset that means "my own colour".
/// </param>
/// <param name="SchemeVariant">
/// Material scheme variant, or <c>null</c> to leave the user's choice alone (Dynamic again).
/// </param>
/// <param name="IsLight">
/// Light or dark, or <c>null</c> to leave the user's choice alone. SELECTING A PRESET IS SELECTING
/// ITS MODE — see <c>CustomizationSettings.UseLightPalette</c> for why this had to become explicit.
/// </param>
/// <param name="Contrast">Contrast target, -1.0 to 1.0, or <c>null</c> to leave it alone.</param>
/// <param name="SplashStyle">Splash sequence to switch to, or <c>null</c> to leave it alone.</param>
public sealed record SeedPreset(
    string Id,
    string NameKey,
    AppTheme BaseTheme,
    string? Seed,
    string? SchemeVariant,
    bool? IsLight,
    double? Contrast,
    double CornerRadius,
    double RemoteCardCornerRadius,
    double GlowStrength,
    double GlassOpacity,
    string? SplashStyle = null);

/// <summary>
/// Every preset the gallery offers, in gallery order.
/// </summary>
public static class SeedPresetCatalog
{
    /// <summary>The id every unknown or absent <c>ThemeId</c> resolves to.</summary>
    public const string DefaultId = "BaseDarkGlass";

    /// <summary>The id whose preset keeps the user's own seed rather than writing one.</summary>
    public const string DynamicId = "Dynamic";

    public static IReadOnlyList<SeedPreset> All { get; } = new SeedPreset[]
    {
        // ── The four homages ─────────────────────────────────────────────────────────────────
        // Geometry and seed are copied verbatim from the switch arms these replaced, so a profile
        // that already names one of these renders identically apart from the variant, which the
        // presets never used to write at all. Each variant below is the one that reproduces the
        // retired dictionary's character: hard cyan on near-black wants Vibrant, graphite wants
        // Neutral (the low-chroma style), and the two that were always ordinary stay on TonalSpot.
        //
        // The ids read Cyber-NOC / Solar-Flare / Monolith / BaseDarkGlass; the names read Neon,
        // Ember, Slate, Glass. Deliberate — see the Id remarks above.

        new("BaseDarkGlass", "Custom_PresetGlass", AppTheme.BaseDarkGlass,
            Seed: "#6C4CFF", SchemeVariant: "TonalSpot", IsLight: false, Contrast: 0.0,
            CornerRadius: 16, RemoteCardCornerRadius: 24, GlowStrength: 2, GlassOpacity: 0.1,
            SplashStyle: "CosmicZoom"),

        new("CyberNOC", "Custom_PresetNeon", AppTheme.CyberNOC,
            Seed: "#00F3FF", SchemeVariant: "Vibrant", IsLight: false, Contrast: 0.0,
            CornerRadius: 2, RemoteCardCornerRadius: 4, GlowStrength: 10, GlassOpacity: 0.05),

        new("SolarFlare", "Custom_PresetEmber", AppTheme.SolarFlare,
            Seed: "#FFB800", SchemeVariant: "TonalSpot", IsLight: true, Contrast: 0.0,
            CornerRadius: 24, RemoteCardCornerRadius: 48, GlowStrength: 2, GlassOpacity: 0.8),

        new("Monolith", "Custom_PresetSlate", AppTheme.Monolith,
            Seed: "#0A84FF", SchemeVariant: "Neutral", IsLight: false, Contrast: 0.0,
            CornerRadius: 8, RemoteCardCornerRadius: 12, GlowStrength: 0, GlassOpacity: 1.0),

        // ── The ones only worth shipping now that generation is free ──────────────────────────
        // None of these could have existed as a hand-authored dictionary; each is one seed plus a
        // variant. Daybreak is the light mode the app never actually had (SolarFlare is light, but
        // it is amber-on-cream, not neutral). Voltage is what Expressive is for. Sorbet is Neutral
        // in light mode, which is the only way to get pastel out of a generator that derives
        // everything from one hue.

        new("Daybreak", "Custom_PresetDaybreak", AppTheme.BaseDarkGlass,
            Seed: "#4C6FFF", SchemeVariant: "TonalSpot", IsLight: true, Contrast: 0.15,
            CornerRadius: 16, RemoteCardCornerRadius: 24, GlowStrength: 0, GlassOpacity: 0.65),

        new("Voltage", "Custom_PresetVoltage", AppTheme.BaseDarkGlass,
            Seed: "#FF2D95", SchemeVariant: "Expressive", IsLight: false, Contrast: 0.2,
            CornerRadius: 20, RemoteCardCornerRadius: 28, GlowStrength: 8, GlassOpacity: 0.12),

        new("Sorbet", "Custom_PresetSorbet", AppTheme.BaseDarkGlass,
            Seed: "#FF9E80", SchemeVariant: "Neutral", IsLight: true, Contrast: 0.0,
            CornerRadius: 28, RemoteCardCornerRadius: 36, GlowStrength: 1, GlassOpacity: 0.5),

        // ── The user's own ────────────────────────────────────────────────────────────────────
        // LAST, AND WITH THREE NULLS. Dynamic is not a look, it is "whatever I built in the Palette
        // Studio". Writing a seed, a variant or a mode here would silently overwrite the thing the
        // user opened this panel to keep, which is exactly the bug the nullable fields exist to
        // make impossible.
        new(DynamicId, "Custom_PresetDynamic", AppTheme.Dynamic,
            Seed: null, SchemeVariant: null, IsLight: null, Contrast: null,
            CornerRadius: 24, RemoteCardCornerRadius: 24, GlowStrength: 4, GlassOpacity: 0.4),
    };

    /// <summary>The preset an unknown id falls back to.</summary>
    public static SeedPreset Default { get; } = All.First(p => p.Id == DefaultId);

    /// <summary>
    /// Resolves a persisted <c>ThemeId</c>. Case-insensitive, because <see cref="AppTheme"/> parsing
    /// was and profiles written by older builds are not guaranteed to match casing.
    /// </summary>
    public static bool TryGet(string? id, out SeedPreset preset)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            foreach (var candidate in All)
            {
                if (string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    preset = candidate;
                    return true;
                }
            }
        }

        preset = Default;
        return false;
    }

    /// <summary>Resolves a persisted <c>ThemeId</c>, falling back to <see cref="Default"/>.</summary>
    public static SeedPreset Resolve(string? id)
    {
        TryGet(id, out var preset);
        return preset;
    }
}
