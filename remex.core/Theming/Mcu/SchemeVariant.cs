// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/Variant.java (enum Variant → SchemeVariant)
namespace Remex.Core.Theming.Mcu;

/// <summary>Themes for Dynamic Color. The nine Material 3 scheme variants, in the reference's declaration order.</summary>
public enum SchemeVariant { Monochrome, Neutral, TonalSpot, Vibrant, Expressive, Fidelity, Content, Rainbow, FruitSalad }

/// <summary>The phone's vocabulary for a variant (Android <c>SettingsManager.themeStyle</c>, <c>theme_sync.style</c>): exact, lowercase, snake_case.</summary>
public static class SchemeVariantWire
{
    public static IReadOnlyList<SchemeVariant> All { get; } = new[]
    {
        SchemeVariant.Monochrome, SchemeVariant.Neutral, SchemeVariant.TonalSpot, SchemeVariant.Vibrant, SchemeVariant.Expressive,
        SchemeVariant.Fidelity, SchemeVariant.Content, SchemeVariant.Rainbow, SchemeVariant.FruitSalad,
    };

    public static string ToWire(this SchemeVariant variant) => variant switch
    {
        SchemeVariant.Monochrome => "monochrome",
        SchemeVariant.Neutral => "neutral",
        SchemeVariant.TonalSpot => "tonal_spot",
        SchemeVariant.Vibrant => "vibrant",
        SchemeVariant.Expressive => "expressive",
        SchemeVariant.Fidelity => "fidelity",
        SchemeVariant.Content => "content",
        SchemeVariant.Rainbow => "rainbow",
        SchemeVariant.FruitSalad => "fruit_salad",
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
    };

    public static bool TryFromWire(string? wire, out SchemeVariant variant)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.ToWire(), wire, StringComparison.Ordinal)) { variant = candidate; return true; }
        }
        variant = SchemeVariant.TonalSpot;
        return false;
    }

    public static SchemeVariant FromWireOrDefault(string? wire) => TryFromWire(wire, out var v) ? v : SchemeVariant.TonalSpot;
}
