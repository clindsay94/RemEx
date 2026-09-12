// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/TonalPalette.java
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

/// <summary>A convenience class for retrieving colors that are constant in hue and chroma, but vary in tone.</summary>
public sealed class TonalPalette
{
    private Dictionary<int, uint> cache;
    private Hct keyColor;
    private double hue;
    private double chroma;

    /// <summary>Create tones using the HCT hue and chroma from a color.</summary>
    /// <param name="argb">ARGB representation of a color</param>
    /// <returns>Tones matching that color's hue and chroma.</returns>
    public static TonalPalette FromInt(uint argb)
    {
        return FromHct(Hct.FromInt(argb));
    }

    /// <summary>Create tones using a HCT color.</summary>
    /// <param name="hct">HCT representation of a color.</param>
    /// <returns>Tones matching that color's hue and chroma.</returns>
    public static TonalPalette FromHct(Hct hct)
    {
        return new TonalPalette(hct.Hue, hct.Chroma, hct);
    }

    /// <summary>Create tones from a defined HCT hue and chroma.</summary>
    /// <param name="hue">HCT hue</param>
    /// <param name="chroma">HCT chroma</param>
    /// <returns>Tones matching hue and chroma.</returns>
    public static TonalPalette FromHueAndChroma(double hue, double chroma)
    {
        Hct keyColor = new KeyColorFinder(hue, chroma).Create();
        return new TonalPalette(hue, chroma, keyColor);
    }

    private TonalPalette(double hue, double chroma, Hct keyColor)
    {
        cache = new Dictionary<int, uint>();
        this.hue = hue;
        this.chroma = chroma;
        this.keyColor = keyColor;
    }

    /// <summary>Create an ARGB color with HCT hue and chroma of this Tones instance, and the provided HCT tone.</summary>
    /// <param name="tone">HCT tone, measured from 0 to 100.</param>
    /// <returns>ARGB representation of a color with that tone.</returns>
    public uint Tone(int tone)
    {
        if (!cache.TryGetValue(tone, out uint color))
        {
            color = Hct.From(this.hue, this.chroma, tone).ToInt();
            cache[tone] = color;
        }
        return color;
    }

    /// <summary>Given a tone, use hue and chroma of palette to create a color, and return it as HCT.</summary>
    public Hct GetHct(double tone)
    {
        return Hct.From(this.hue, this.chroma, tone);
    }

    /// <summary>The chroma of the Tonal Palette, in HCT. Ranges from 0 to ~130 (for sRGB gamut).</summary>
    public double Chroma => this.chroma;

    /// <summary>The hue of the Tonal Palette, in HCT. Ranges from 0 to 360.</summary>
    public double Hue => this.hue;

    /// <summary>The key color is the first tone, starting from T50, that matches the palette's chroma.</summary>
    public Hct KeyColor => this.keyColor;

    /// <summary>
    /// Key color is a color that represents the hue and chroma of a tonal palette. Named
    /// KeyColorFinder, not KeyColor as in the Java reference: C# does not allow a member (the
    /// KeyColor property above) and a nested type to share a name in the same class scope.
    /// </summary>
    private sealed class KeyColorFinder
    {
        private readonly double hue;
        private readonly double requestedChroma;

        // Cache that maps tone to max chroma to avoid duplicated HCT calculation.
        private readonly Dictionary<int, double> chromaCache = new();
        private const double MaxChromaValue = 200.0;

        /// <summary>Key color is a color that represents the hue and chroma of a tonal palette</summary>
        public KeyColorFinder(double hue, double requestedChroma)
        {
            this.hue = hue;
            this.requestedChroma = requestedChroma;
        }

        /// <summary>
        /// Creates a key color from a [hue] and a [chroma]. The key color is the first tone, starting
        /// from T50, matching the given hue and chroma.
        /// </summary>
        /// <returns>Key color Hct</returns>
        public Hct Create()
        {
            // Pivot around T50 because T50 has the most chroma available, on
            // average. Thus it is most likely to have a direct answer.
            const int pivotTone = 50;
            const int toneStepSize = 1;
            // Epsilon to accept values slightly higher than the requested chroma.
            const double epsilon = 0.01;

            // Binary search to find the tone that can provide a chroma that is closest
            // to the requested chroma.
            int lowerTone = 0;
            int upperTone = 100;
            while (lowerTone < upperTone)
            {
                int midTone = (lowerTone + upperTone) / 2;
                bool isAscending = MaxChroma(midTone) < MaxChroma(midTone + toneStepSize);
                bool sufficientChroma = MaxChroma(midTone) >= requestedChroma - epsilon;

                if (sufficientChroma)
                {
                    // Either range [lowerTone, midTone] or [midTone, upperTone] has
                    // the answer, so search in the range that is closer the pivot tone.
                    if (Math.Abs(lowerTone - pivotTone) < Math.Abs(upperTone - pivotTone))
                    {
                        upperTone = midTone;
                    }
                    else
                    {
                        if (lowerTone == midTone)
                        {
                            return Hct.From(this.hue, this.requestedChroma, lowerTone);
                        }
                        lowerTone = midTone;
                    }
                }
                else
                {
                    // As there is no sufficient chroma in the midTone, follow the direction to the chroma
                    // peak.
                    if (isAscending)
                    {
                        lowerTone = midTone + toneStepSize;
                    }
                    else
                    {
                        // Keep midTone for potential chroma peak.
                        upperTone = midTone;
                    }
                }
            }

            return Hct.From(this.hue, this.requestedChroma, lowerTone);
        }

        // Find the maximum chroma for a given tone
        private double MaxChroma(int tone)
        {
            if (!chromaCache.TryGetValue(tone, out double newChroma))
            {
                newChroma = Hct.From(hue, MaxChromaValue, tone).Chroma;
                chromaCache[tone] = newChroma;
            }
            return chromaCache[tone];
        }
    }
}
