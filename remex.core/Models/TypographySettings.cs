using System;
using System.Text.Json.Serialization;

namespace Remex.Core.Models;

/// <summary>
/// Personalize → Text (RemEx-jt6w5): per-section size multipliers and bold, the legibility halo
/// ("text shadow") and the sensor-title backdrop. Held as <see cref="CustomizationSettings.Typography"/>.
/// </summary>
/// <remarks>
/// A plain record of primitives so it stays NativeAOT-safe (Remex.Core is also the Android JNI
/// library) and serializes only through <c>RemexJsonSerializerContext</c>. Ranges are enforced by
/// <see cref="Normalize"/> at every READ site (view-model load, the desktop resolver), never by
/// rewriting the profile: an out-of-range value on disk is clamped for display and only replaced
/// once the user touches the section — the same contract <see cref="CustomizationSettings.UiScale"/>
/// has. Palette presets never read or write this record; a palette is colours.
/// </remarks>
public record TypographySettings
{
    public const double MinScale = 0.80;
    public const double MaxScale = 1.60;
    public const int MinShadowStrength = 0;
    public const int MaxShadowStrength = 100;

    /// <summary>Headers: Headline5/6, Subtitle1, page-title, card-title. Multiplier on each member's own size.</summary>
    [JsonPropertyName("headersScale")]
    public double HeadersScale { get; init; } = 1.0;

    /// <summary>Body: Body2, page-subtitle and untagged text.</summary>
    [JsonPropertyName("bodyScale")]
    public double BodyScale { get; init; } = 1.0;

    /// <summary>Small text: Caption and Overline.</summary>
    [JsonPropertyName("smallScale")]
    public double SmallScale { get; init; } = 1.0;

    /// <summary>Sensor text: card titles and dual-metric names, wherever sensor cards render.</summary>
    [JsonPropertyName("sensorScale")]
    public double SensorScale { get; init; } = 1.0;

    /// <summary>On = <c>FontWeight.Bold</c> for every member of the section; off = each member's own default weight.</summary>
    [JsonPropertyName("headersBold")]
    public bool HeadersBold { get; init; }

    [JsonPropertyName("bodyBold")]
    public bool BodyBold { get; init; }

    [JsonPropertyName("smallBold")]
    public bool SmallBold { get; init; }

    [JsonPropertyName("sensorBold")]
    public bool SensorBold { get; init; }

    /// <summary>The value-pill treatment drawn behind sensor-card titles.</summary>
    [JsonPropertyName("sensorTitleBackdrop")]
    public bool SensorTitleBackdrop { get; init; }

    /// <summary>The legibility halo. Its colour is derived from the theme surface, never chosen.</summary>
    [JsonPropertyName("shadowEnabled")]
    public bool ShadowEnabled { get; init; } = true;

    /// <summary>0–100; maps linearly to blur 1→8 px and opacity 0.35→0.90.</summary>
    [JsonPropertyName("shadowStrength")]
    public int ShadowStrength { get; init; } = 40;

    /// <summary>The spec's default row; also what <see cref="Normalize"/> returns for <c>null</c>.</summary>
    public static TypographySettings Default => new();

    /// <summary>
    /// A finite, positive scale clamped to [<see cref="MinScale"/>, <see cref="MaxScale"/>]; NaN,
    /// infinity, zero and negatives are "no value" and become 1.0 rather than the floor.
    /// </summary>
    public static double ClampScale(double scale) =>
        double.IsFinite(scale) && scale > 0 ? Math.Clamp(scale, MinScale, MaxScale) : 1.0;

    /// <summary>A copy with every ranged field clamped; <c>null</c> (an explicit JSON null) → <see cref="Default"/>.</summary>
    public static TypographySettings Normalize(TypographySettings? value)
    {
        if (value is null) return Default;

        return value with
        {
            HeadersScale = ClampScale(value.HeadersScale),
            BodyScale = ClampScale(value.BodyScale),
            SmallScale = ClampScale(value.SmallScale),
            SensorScale = ClampScale(value.SensorScale),
            ShadowStrength = Math.Clamp(value.ShadowStrength, MinShadowStrength, MaxShadowStrength),
        };
    }
}
