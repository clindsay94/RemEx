// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/TemperatureCache.java
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
/// Design utilities using color temperature theory.
///
/// Analogous colors, complementary color, and cache to efficiently, lazily, generate data for
/// calculations when needed.
/// </summary>
public sealed class TemperatureCache
{
    private readonly Hct input;

    private Hct? precomputedComplement;
    private List<Hct>? precomputedHctsByTemp;
    private List<Hct>? precomputedHctsByHue;
    // Map<Hct, Double> is keyed by reference identity in Java (Hct overrides neither equals nor
    // hashCode) — Dictionary<Hct, double> uses reference equality here for the same reason
    // (transcription-rules.md rule 5): Hct must never override Equals/GetHashCode.
    private Dictionary<Hct, double>? precomputedTempsByHct;

    /// <summary>Create a cache that allows calculation of ex. complementary and analogous colors.</summary>
    /// <param name="input">Color to find complement/analogous colors of. Any colors will have the same tone,
    /// and chroma as the input color, modulo any restrictions due to the other hues having lower
    /// limits on chroma.</param>
    public TemperatureCache(Hct input)
    {
        this.input = input;
    }

    /// <summary>
    /// A color that complements the input color aesthetically.
    ///
    /// In art, this is usually described as being across the color wheel. History of this shows
    /// intent as a color that is just as cool-warm as the input color is warm-cool.
    /// </summary>
    public Hct GetComplement()
    {
        if (precomputedComplement != null)
        {
            return precomputedComplement;
        }

        double coldestHue = GetColdest().Hue;
        double coldestTemp = GetTempsByHct()[GetColdest()];

        double warmestHue = GetWarmest().Hue;
        double warmestTemp = GetTempsByHct()[GetWarmest()];
        double range = warmestTemp - coldestTemp;
        bool startHueIsColdestToWarmest = IsBetween(input.Hue, coldestHue, warmestHue);
        double startHue = startHueIsColdestToWarmest ? warmestHue : coldestHue;
        double endHue = startHueIsColdestToWarmest ? coldestHue : warmestHue;
        double directionOfRotation = 1.0;
        double smallestError = 1000.0;
        Hct answer = GetHctsByHue()[(int)Math.Floor(input.Hue + 0.5)];

        double complementRelativeTemp = (1.0 - GetRelativeTemperature(input));
        // Find the color in the other section, closest to the inverse percentile
        // of the input color. This is the complement.
        for (double hueAddend = 0.0; hueAddend <= 360.0; hueAddend += 1.0)
        {
            double hue = MathUtils.SanitizeDegreesDouble(startHue + directionOfRotation * hueAddend);
            if (!IsBetween(hue, startHue, endHue))
            {
                continue;
            }
            Hct possibleAnswer = GetHctsByHue()[(int)Math.Floor(hue + 0.5)];
            double relativeTemp = (GetTempsByHct()[possibleAnswer] - coldestTemp) / range;
            double error = Math.Abs(complementRelativeTemp - relativeTemp);
            if (error < smallestError)
            {
                smallestError = error;
                answer = possibleAnswer;
            }
        }
        precomputedComplement = answer;
        return precomputedComplement;
    }

    /// <summary>
    /// 5 colors that pair well with the input color.
    ///
    /// The colors are equidistant in temperature and adjacent in hue.
    /// </summary>
    public List<Hct> GetAnalogousColors()
    {
        return GetAnalogousColors(5, 12);
    }

    /// <summary>
    /// A set of colors with differing hues, equidistant in temperature.
    ///
    /// In art, this is usually described as a set of 5 colors on a color wheel divided into 12
    /// sections. This method allows provision of either of those values.
    ///
    /// Behavior is undefined when count or divisions is 0. When divisions &lt; count, colors repeat.
    /// </summary>
    /// <param name="count">The number of colors to return, includes the input color.</param>
    /// <param name="divisions">The number of divisions on the color wheel.</param>
    public List<Hct> GetAnalogousColors(int count, int divisions)
    {
        // The starting hue is the hue of the input color.
        int startHue = (int)Math.Floor(input.Hue + 0.5);
        Hct startHct = GetHctsByHue()[startHue];
        double lastTemp = GetRelativeTemperature(startHct);

        List<Hct> allColors = new List<Hct>();
        allColors.Add(startHct);

        double absoluteTotalTempDelta = 0.0;
        for (int i = 0; i < 360; i++)
        {
            int hue = MathUtils.SanitizeDegreesInt(startHue + i);
            Hct hct = GetHctsByHue()[hue];
            double temp = GetRelativeTemperature(hct);
            double tempDelta = Math.Abs(temp - lastTemp);
            lastTemp = temp;
            absoluteTotalTempDelta += tempDelta;
        }

        int hueAddend = 1;
        double tempStep = absoluteTotalTempDelta / (double)divisions;
        double totalTempDelta = 0.0;
        lastTemp = GetRelativeTemperature(startHct);
        while (allColors.Count < divisions)
        {
            int hue = MathUtils.SanitizeDegreesInt(startHue + hueAddend);
            Hct hct = GetHctsByHue()[hue];
            double temp = GetRelativeTemperature(hct);
            double tempDelta = Math.Abs(temp - lastTemp);
            totalTempDelta += tempDelta;

            double desiredTotalTempDeltaForIndex = (allColors.Count * tempStep);
            bool indexSatisfied = totalTempDelta >= desiredTotalTempDeltaForIndex;
            int indexAddend = 1;
            // Keep adding this hue to the answers until its temperature is
            // insufficient. This ensures consistent behavior when there aren't
            // `divisions` discrete steps between 0 and 360 in hue with `tempStep`
            // delta in temperature between them.
            //
            // For example, white and black have no analogues: there are no other
            // colors at T100/T0. Therefore, they should just be added to the array
            // as answers.
            while (indexSatisfied && allColors.Count < divisions)
            {
                allColors.Add(hct);
                desiredTotalTempDeltaForIndex = ((allColors.Count + indexAddend) * tempStep);
                indexSatisfied = totalTempDelta >= desiredTotalTempDeltaForIndex;
                indexAddend++;
            }
            lastTemp = temp;
            hueAddend++;

            if (hueAddend > 360)
            {
                while (allColors.Count < divisions)
                {
                    allColors.Add(hct);
                }
                break;
            }
        }

        List<Hct> answers = new List<Hct>();
        answers.Add(input);

        int ccwCount = (int)Math.Floor(((double)count - 1.0) / 2.0);
        for (int i = 1; i < (ccwCount + 1); i++)
        {
            int index = 0 - i;
            while (index < 0)
            {
                index = allColors.Count + index;
            }
            if (index >= allColors.Count)
            {
                index = index % allColors.Count;
            }
            answers.Insert(0, allColors[index]);
        }

        int cwCount = count - ccwCount - 1;
        for (int i = 1; i < (cwCount + 1); i++)
        {
            int index = i;
            while (index < 0)
            {
                index = allColors.Count + index;
            }
            if (index >= allColors.Count)
            {
                index = index % allColors.Count;
            }
            answers.Add(allColors[index]);
        }

        return answers;
    }

    /// <summary>Temperature relative to all colors with the same chroma and tone.</summary>
    /// <param name="hct">HCT to find the relative temperature of.</param>
    /// <returns>Value on a scale from 0 to 1.</returns>
    public double GetRelativeTemperature(Hct hct)
    {
        double range = GetTempsByHct()[GetWarmest()] - GetTempsByHct()[GetColdest()];
        double differenceFromColdest = GetTempsByHct()[hct] - GetTempsByHct()[GetColdest()];
        // Handle when there's no difference in temperature between warmest and
        // coldest: for example, at T100, only one color is available, white.
        if (range == 0.0)
        {
            return 0.5;
        }
        return differenceFromColdest / range;
    }

    /// <summary>
    /// Value representing cool-warm factor of a color. Values below 0 are considered cool, above,
    /// warm.
    ///
    /// Color science has researched emotion and harmony, which art uses to select colors. Warm-cool
    /// is the foundation of analogous and complementary colors. See: - Li-Chen Ou's Chapter 19 in
    /// Handbook of Color Psychology (2015). - Josef Albers' Interaction of Color chapters 19 and 21.
    ///
    /// Implementation of Ou, Woodcock and Wright's algorithm, which uses Lab/LCH color space.
    /// Return value has these properties:
    /// - Values below 0 are cool, above 0 are warm.
    /// - Lower bound: -9.66. Chroma is infinite. Assuming max of Lab chroma 130.
    /// - Upper bound: 8.61. Chroma is infinite. Assuming max of Lab chroma 130.
    /// </summary>
    public static double RawTemperature(Hct color)
    {
        double[] lab = ColorUtils.LabFromArgb(color.ToInt());
        double hue = MathUtils.SanitizeDegreesDouble(MathUtils.ToDegrees(Math.Atan2(lab[2], lab[1])));
        double chroma = double.Hypot(lab[1], lab[2]);
        return -0.5
            + 0.02
                * Math.Pow(chroma, 1.07)
                * Math.Cos(MathUtils.ToRadians(MathUtils.SanitizeDegreesDouble(hue - 50.0)));
    }

    /// <summary>Coldest color with same chroma and tone as input.</summary>
    private Hct GetColdest()
    {
        return GetHctsByTemp()[0];
    }

    /// <summary>
    /// HCTs for all colors with the same chroma/tone as the input.
    ///
    /// Sorted by hue, ex. index 0 is hue 0.
    /// </summary>
    private List<Hct> GetHctsByHue()
    {
        if (precomputedHctsByHue != null)
        {
            return precomputedHctsByHue;
        }
        List<Hct> hcts = new List<Hct>();
        for (double hue = 0.0; hue <= 360.0; hue += 1.0)
        {
            Hct colorAtHue = Hct.From(hue, input.Chroma, input.Tone);
            hcts.Add(colorAtHue);
        }
        precomputedHctsByHue = hcts;
        return precomputedHctsByHue;
    }

    /// <summary>
    /// HCTs for all colors with the same chroma/tone as the input.
    ///
    /// Sorted from coldest first to warmest last.
    /// </summary>
    private List<Hct> GetHctsByTemp()
    {
        if (precomputedHctsByTemp != null)
        {
            return precomputedHctsByTemp;
        }

        List<Hct> hcts = new List<Hct>(GetHctsByHue());
        hcts.Add(input);
        // rule 5: Collections.sort with a comparator is stable in Java — transcribe with LINQ
        // OrderBy (stable), never List<T>.Sort (which is not guaranteed stable).
        hcts = hcts.OrderBy(arg => GetTempsByHct()[arg]).ToList();
        precomputedHctsByTemp = hcts;
        return precomputedHctsByTemp;
    }

    /// <summary>Keys of HCTs in GetHctsByTemp, values of raw temperature.</summary>
    private Dictionary<Hct, double> GetTempsByHct()
    {
        if (precomputedTempsByHct != null)
        {
            return precomputedTempsByHct;
        }

        List<Hct> allHcts = new List<Hct>(GetHctsByHue());
        allHcts.Add(input);

        Dictionary<Hct, double> temperaturesByHct = new Dictionary<Hct, double>();
        foreach (Hct hct in allHcts)
        {
            temperaturesByHct[hct] = RawTemperature(hct);
        }

        precomputedTempsByHct = temperaturesByHct;
        return precomputedTempsByHct;
    }

    /// <summary>Warmest color with same chroma and tone as input.</summary>
    private Hct GetWarmest()
    {
        return GetHctsByTemp()[GetHctsByTemp().Count - 1];
    }

    /// <summary>Determines if an angle is between two other angles, rotating clockwise.</summary>
    private static bool IsBetween(double angle, double a, double b)
    {
        if (a < b)
        {
            return a <= angle && angle <= b;
        }
        return a <= angle || angle <= b;
    }
}
