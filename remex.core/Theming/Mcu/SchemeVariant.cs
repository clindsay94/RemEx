// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/Variant.java (enum Variant → SchemeVariant)
/*
 * Copyright (C) 2022 The Android Open Source Project
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *      http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
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
