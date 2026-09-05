using System;
using System.Collections.Generic;

namespace Remex.Desktop.Models;

/// <summary>
/// The seven palette strategies the sheet offers — Android's seven, in Android's order
/// (<c>remex.android/.../ui/theme/Theme.kt</c>) — and the one place a persisted strategy string
/// is brought onto that list.
/// </summary>
/// <remarks>
/// NEUTRAL AND MONOCHROME HAVE NO STYLE OF THEIR OWN in MaterialColorUtilities 0.3.0 (its
/// <c>Style</c> enum is Spritz, TonalSpot, Vibrant, Expressive, Rainbow, FruitSalad, Content).
/// Neutral is the library's Spritz — the low-chroma style, which is what Android's Neutral is —
/// and Monochrome is built by <c>DynamicColorGenerator</c> zeroing every tonal palette's chroma.
/// Content is retired as a user-facing name; Spritz is retired as a NAME but not as a look.
/// <para>
/// Persisted strings stay English; the sheet localises them through <c>Custom_Scheme_*</c>.
/// </para>
/// </remarks>
public static class SchemeVariants
{
    public const string TonalSpot = "TonalSpot";
    public const string Expressive = "Expressive";
    public const string FruitSalad = "FruitSalad";
    public const string Rainbow = "Rainbow";
    public const string Vibrant = "Vibrant";
    public const string Neutral = "Neutral";
    public const string Monochrome = "Monochrome";

    /// <summary>Android's order. This is the order the strategy chips render in.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        TonalSpot, Expressive, FruitSalad, Rainbow, Vibrant, Neutral, Monochrome,
    };

    /// <summary>
    /// The strategy a persisted string means: Spritz → Neutral, Content → TonalSpot, anything
    /// not on <see cref="All"/> (case-sensitive, like every other persisted name) → TonalSpot.
    /// </summary>
    public static string Normalize(string? variant)
    {
        if (string.Equals(variant, "Spritz", StringComparison.Ordinal)) return Neutral;
        foreach (var known in All)
            if (string.Equals(known, variant, StringComparison.Ordinal)) return known;
        return TonalSpot;
    }
}
