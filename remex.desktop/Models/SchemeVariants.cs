using System;
using System.Collections.Generic;
using Remex.Core.Theming.Mcu;

namespace Remex.Desktop.Models;

/// <summary>
/// The nine palette strategies the sheet offers — Android's nine, in Android's order
/// (<c>remex.android/.../ui/theme/Theme.kt</c>, <c>PersonalizationScreen.kt:458-472</c>) — and the
/// one place a persisted strategy string is brought onto that list.
/// </summary>
/// <remarks>
/// THE ENGINE IS NOW <c>Remex.Core.Theming.Mcu</c> (RemEx-4kv0g.12), the ported MCU library —
/// Android's own <c>DynamicScheme</c>/<c>MaterialDynamicColors</c> reproduced bit-exact, not the
/// albi005 approximation this used to run through. Content and Fidelity are real variants now,
/// each with its own <see cref="SchemeVariant"/>; Spritz is still retired as a NAME (it maps onto
/// Neutral) but not as a look — MCU's <c>SchemeNeutral</c> is the low-chroma style Spritz used to be.
/// <para>
/// Persisted strings stay English (PascalCase, unchanged for the original seven); the sheet
/// localises them through <c>Custom_Scheme_*</c>.
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
    public const string Fidelity = "Fidelity";
    public const string Content = "Content";

    /// <summary>Android's nine, in the phone's chip order (PersonalizationScreen.kt) — the order the strips render in.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        TonalSpot, Expressive, FruitSalad, Rainbow, Vibrant, Neutral, Monochrome, Fidelity, Content,
    };

    /// <summary>
    /// The strategy a persisted string means: Spritz → Neutral, anything on <see cref="All"/>
    /// (case-sensitive, like every other persisted name) → itself, else → TonalSpot.
    /// </summary>
    public static string Normalize(string? variant)
    {
        if (string.Equals(variant, "Spritz", StringComparison.Ordinal)) return Neutral;
        foreach (var known in All)
            if (string.Equals(known, variant, StringComparison.Ordinal)) return known;
        return TonalSpot;
    }

    /// <summary>The engine's variant for a persisted name — Normalize first, so every rule about retired names lives in one place.</summary>
    public static SchemeVariant ToMcu(string? variant) => Normalize(variant) switch
    {
        Expressive => SchemeVariant.Expressive,
        FruitSalad => SchemeVariant.FruitSalad,
        Rainbow => SchemeVariant.Rainbow,
        Vibrant => SchemeVariant.Vibrant,
        Neutral => SchemeVariant.Neutral,
        Monochrome => SchemeVariant.Monochrome,
        Fidelity => SchemeVariant.Fidelity,
        Content => SchemeVariant.Content,
        _ => SchemeVariant.TonalSpot,
    };

    public static string FromMcu(SchemeVariant variant) => variant switch
    {
        SchemeVariant.Expressive => Expressive,
        SchemeVariant.FruitSalad => FruitSalad,
        SchemeVariant.Rainbow => Rainbow,
        SchemeVariant.Vibrant => Vibrant,
        SchemeVariant.Neutral => Neutral,
        SchemeVariant.Monochrome => Monochrome,
        SchemeVariant.Fidelity => Fidelity,
        SchemeVariant.Content => Content,
        _ => TonalSpot,
    };
}
