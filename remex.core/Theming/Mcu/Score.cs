// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/Score.java
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
using System;
using System.Collections.Generic;
using System.Linq;

namespace Remex.Core.Theming.Mcu;

/// <summary>
/// Given a large set of colors, remove colors that are unsuitable for a UI theme, and rank the rest
/// based on suitability.
///
/// Enables use of a high cluster count for image quantization, thus ensuring colors aren't
/// muddied, while curating the high cluster count to a much smaller number of appropriate choices.
/// </summary>
public static class Score
{
    private const double TargetChroma = 48.0; // A1 Chroma
    private const double WeightProportion = 0.7;
    private const double WeightChromaAbove = 0.3;
    private const double WeightChromaBelow = 0.1;
    private const double CutoffChroma = 5.0;
    private const double CutoffExcitedProportion = 0.01;
    private const uint Blue500 = 0xFF4285F4u;
    private const int MaxColorCount = 4;

    public static List<uint> ScoreColors(Dictionary<uint, int> colorsToPopulation)
    {
        // Fallback color is Google Blue.
        return ScoreColors(colorsToPopulation, MaxColorCount, Blue500, true);
    }

    public static List<uint> ScoreColors(Dictionary<uint, int> colorsToPopulation, int maxColorCount)
    {
        return ScoreColors(colorsToPopulation, maxColorCount, Blue500, true);
    }

    public static List<uint> ScoreColors(Dictionary<uint, int> colorsToPopulation, int maxColorCount, uint fallbackColorArgb)
    {
        return ScoreColors(colorsToPopulation, maxColorCount, fallbackColorArgb, true);
    }

    /// <summary>
    /// Given a map with keys of colors and values of how often the color appears, rank the colors
    /// based on suitability for being used for a UI theme.
    /// </summary>
    /// <param name="colorsToPopulation">
    /// map with keys of colors and values of how often the color appears, usually from a source image.
    /// </param>
    /// <param name="maxColorCount">max count of colors to be returned in the list.</param>
    /// <param name="fallbackColorArgb">color to be returned if no other options available.</param>
    /// <param name="filter">whether to filter out undesirable combinations.</param>
    /// <returns>
    /// Colors sorted by suitability for a UI theme. The most suitable color is the first item,
    /// the least suitable is the last. There will always be at least one color returned. If all
    /// the input colors were not suitable for a theme, a default fallback color will be provided,
    /// Google Blue.
    /// </returns>
    public static List<uint> ScoreColors(
        Dictionary<uint, int> colorsToPopulation,
        int maxColorCount,
        uint fallbackColorArgb,
        bool filter)
    {
        // Get the HCT color for each Argb value, while finding the per hue count and
        // total count.
        List<Hct> colorsHct = new();
        int[] huePopulation = new int[360];
        double populationSum = 0.0;
        foreach (var entry in colorsToPopulation)
        {
            Hct hct = Hct.FromInt(entry.Key);
            colorsHct.Add(hct);
            int hue = (int)Math.Floor(hct.Hue);
            int population = entry.Value;
            huePopulation[hue] += population;
            populationSum += population;
        }

        // Hues with more usage in neighboring 30 degree slice get a larger number.
        double[] hueExcitedProportions = new double[360];
        for (int hue = 0; hue < 360; hue++)
        {
            double proportion = huePopulation[hue] / populationSum;
            for (int i = hue - 14; i < hue + 16; i++)
            {
                int neighborHue = MathUtils.SanitizeDegreesInt(i);
                hueExcitedProportions[neighborHue] += proportion;
            }
        }

        // Scores each HCT color based on usage and chroma, while optionally
        // filtering out values that do not have enough chroma or usage.
        List<ScoredHct> scoredHcts = new();
        foreach (var hct in colorsHct)
        {
            int hue = MathUtils.SanitizeDegreesInt((int)Math.Floor(hct.Hue + 0.5));
            double proportion = hueExcitedProportions[hue];
            if (filter && (hct.Chroma < CutoffChroma || proportion <= CutoffExcitedProportion))
            {
                continue;
            }

            double proportionScore = proportion * 100.0 * WeightProportion;
            double chromaWeight =
                hct.Chroma < TargetChroma ? WeightChromaBelow : WeightChromaAbove;
            double chromaScore = (hct.Chroma - TargetChroma) * chromaWeight;
            double score = proportionScore + chromaScore;
            scoredHcts.Add(new ScoredHct(hct, score));
        }
        // Sorted so that colors with higher scores come first.
        scoredHcts = scoredHcts.OrderByDescending(s => s.Score).ToList();

        // Iterates through potential hue differences in degrees in order to select
        // the colors with the largest distribution of hues possible. Starting at
        // 90 degrees(maximum difference for 4 colors) then decreasing down to a
        // 15 degree minimum.
        List<Hct> chosenColors = new();
        for (int differenceDegrees = 90; differenceDegrees >= 15; differenceDegrees--)
        {
            chosenColors.Clear();
            foreach (var entry in scoredHcts)
            {
                Hct hct = entry.Hct;
                bool hasDuplicateHue = false;
                foreach (var chosenHct in chosenColors)
                {
                    if (MathUtils.DifferenceDegrees(hct.Hue, chosenHct.Hue) < differenceDegrees)
                    {
                        hasDuplicateHue = true;
                        break;
                    }
                }
                if (!hasDuplicateHue)
                {
                    chosenColors.Add(hct);
                }
                if (chosenColors.Count >= maxColorCount)
                {
                    break;
                }
            }
            if (chosenColors.Count >= maxColorCount)
            {
                break;
            }
        }
        List<uint> colors = new();
        if (chosenColors.Count == 0)
        {
            colors.Add(fallbackColorArgb);
            return colors;
        }
        foreach (var chosenHct in chosenColors)
        {
            colors.Add(chosenHct.ToInt());
        }
        return colors;
    }

    private sealed class ScoredHct
    {
        public readonly Hct Hct;
        public readonly double Score;

        public ScoredHct(Hct hct, double score)
        {
            Hct = hct;
            Score = score;
        }
    }
}
