// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/DynamicScheme.java:32-104
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

/// <summary>
/// Provides important settings for creating colors dynamically, and 6 color palettes. Requires: a source color, a
/// theme (Variant), whether or not it's dark mode, and a contrast level (-1 to 1).
/// </summary>
public class DynamicScheme
{
    public uint SourceColorArgb { get; }
    public Hct SourceColorHct { get; }
    public SchemeVariant Variant { get; }
    public bool IsDark { get; }
    public double ContrastLevel { get; }

    public TonalPalette PrimaryPalette { get; }
    public TonalPalette SecondaryPalette { get; }
    public TonalPalette TertiaryPalette { get; }
    public TonalPalette NeutralPalette { get; }
    public TonalPalette NeutralVariantPalette { get; }
    public TonalPalette ErrorPalette { get; }

    public DynamicScheme(
        Hct sourceColorHct,
        SchemeVariant variant,
        bool isDark,
        double contrastLevel,
        TonalPalette primaryPalette,
        TonalPalette secondaryPalette,
        TonalPalette tertiaryPalette,
        TonalPalette neutralPalette,
        TonalPalette neutralVariantPalette)
    {
        SourceColorArgb = sourceColorHct.ToInt();
        SourceColorHct = sourceColorHct;
        Variant = variant;
        IsDark = isDark;
        ContrastLevel = contrastLevel;

        PrimaryPalette = primaryPalette;
        SecondaryPalette = secondaryPalette;
        TertiaryPalette = tertiaryPalette;
        NeutralPalette = neutralPalette;
        NeutralVariantPalette = neutralVariantPalette;
        ErrorPalette = TonalPalette.FromHueAndChroma(25.0, 84.0);
    }

    /// <summary>
    /// Given a set of hues and set of hue rotations, locate which hues the source color's hue is between, apply the
    /// rotation at the same index as the first hue in the range, and return the rotated hue.
    /// </summary>
    public static double GetRotatedHue(Hct sourceColorHct, double[] hues, double[] rotations)
    {
        double sourceHue = sourceColorHct.Hue;
        if (rotations.Length == 1) return MathUtils.SanitizeDegreesDouble(sourceHue + rotations[0]);
        int size = hues.Length;
        for (int i = 0; i <= size - 2; i++)
        {
            double thisHue = hues[i];
            double nextHue = hues[i + 1];
            if (thisHue < sourceHue && sourceHue < nextHue) return MathUtils.SanitizeDegreesDouble(sourceHue + rotations[i]);
        }
        // If this statement executes, something is wrong, there should have been a rotation found using the arrays.
        return sourceHue;
    }

    public Hct GetHct(DynamicColor dynamicColor) => dynamicColor.GetHct(this);
    public uint GetArgb(DynamicColor dynamicColor) => dynamicColor.GetArgb(this);
}
