// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/QuantizerWu.java
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

namespace Remex.Core.Theming.Mcu;

/// <summary>
/// An image quantizer that divides the image's pixels into clusters by recursively cutting an RGB
/// cube, based on the weight of pixels in each area of the cube.
///
/// The algorithm was described by Xiaolin Wu in Graphic Gems II, published in 1991.
/// </summary>
public sealed class QuantizerWu : Quantizer
{
    private int[] weights = Array.Empty<int>();
    private int[] momentsR = Array.Empty<int>();
    private int[] momentsG = Array.Empty<int>();
    private int[] momentsB = Array.Empty<int>();
    private double[] moments = Array.Empty<double>();
    private Box[] cubes = Array.Empty<Box>();

    // A histogram of all the input colors is constructed. It has the shape of a
    // cube. The cube would be too large if it contained all 16 million colors:
    // historical best practice is to use 5 bits  of the 8 in each channel,
    // reducing the histogram to a volume of ~32,000.
    private const int IndexBits = 5;
    private const int IndexCount = 33; // ((1 << INDEX_BITS) + 1)
    private const int TotalSize = 35937; // INDEX_COUNT * INDEX_COUNT * INDEX_COUNT

    public QuantizerResult Quantize(uint[] pixels, int colorCount)
    {
        QuantizerResult mapResult = new QuantizerMap().Quantize(pixels, colorCount);
        ConstructHistogram(mapResult.ColorToCount);
        CreateMoments();
        CreateBoxesResult createBoxesResult = CreateBoxes(colorCount);
        List<uint> colors = CreateResult(createBoxesResult.ResultCount);
        var resultMap = new Dictionary<uint, int>();
        foreach (var color in colors)
        {
            resultMap[color] = 0;
        }
        return new QuantizerResult(resultMap);
    }

    private static int GetIndex(int r, int g, int b)
    {
        return (r << (IndexBits * 2)) + (r << (IndexBits + 1)) + r + (g << IndexBits) + g + b;
    }

    private void ConstructHistogram(Dictionary<uint, int> pixels)
    {
        weights = new int[TotalSize];
        momentsR = new int[TotalSize];
        momentsG = new int[TotalSize];
        momentsB = new int[TotalSize];
        moments = new double[TotalSize];

        foreach (var pair in pixels)
        {
            uint pixel = pair.Key;
            int count = pair.Value;
            int red = ColorUtils.RedFromArgb(pixel);
            int green = ColorUtils.GreenFromArgb(pixel);
            int blue = ColorUtils.BlueFromArgb(pixel);
            int bitsToRemove = 8 - IndexBits;
            int iR = (red >> bitsToRemove) + 1;
            int iG = (green >> bitsToRemove) + 1;
            int iB = (blue >> bitsToRemove) + 1;
            int index = GetIndex(iR, iG, iB);
            weights[index] += count;
            momentsR[index] += red * count;
            momentsG[index] += green * count;
            momentsB[index] += blue * count;
            moments[index] += count * ((red * red) + (green * green) + (blue * blue));
        }
    }

    private void CreateMoments()
    {
        for (int r = 1; r < IndexCount; ++r)
        {
            int[] area = new int[IndexCount];
            int[] areaR = new int[IndexCount];
            int[] areaG = new int[IndexCount];
            int[] areaB = new int[IndexCount];
            double[] area2 = new double[IndexCount];

            for (int g = 1; g < IndexCount; ++g)
            {
                int line = 0;
                int lineR = 0;
                int lineG = 0;
                int lineB = 0;
                double line2 = 0.0;
                for (int b = 1; b < IndexCount; ++b)
                {
                    int index = GetIndex(r, g, b);
                    line += weights[index];
                    lineR += momentsR[index];
                    lineG += momentsG[index];
                    lineB += momentsB[index];
                    line2 += moments[index];

                    area[b] += line;
                    areaR[b] += lineR;
                    areaG[b] += lineG;
                    areaB[b] += lineB;
                    area2[b] += line2;

                    int previousIndex = GetIndex(r - 1, g, b);
                    weights[index] = weights[previousIndex] + area[b];
                    momentsR[index] = momentsR[previousIndex] + areaR[b];
                    momentsG[index] = momentsG[previousIndex] + areaG[b];
                    momentsB[index] = momentsB[previousIndex] + areaB[b];
                    moments[index] = moments[previousIndex] + area2[b];
                }
            }
        }
    }

    private CreateBoxesResult CreateBoxes(int maxColorCount)
    {
        cubes = new Box[maxColorCount];
        for (int i = 0; i < maxColorCount; i++)
        {
            cubes[i] = new Box();
        }
        double[] volumeVariance = new double[maxColorCount];
        Box firstBox = cubes[0];
        firstBox.R1 = IndexCount - 1;
        firstBox.G1 = IndexCount - 1;
        firstBox.B1 = IndexCount - 1;

        int generatedColorCount = maxColorCount;
        int next = 0;
        for (int i = 1; i < maxColorCount; i++)
        {
            if (Cut(cubes[next], cubes[i]))
            {
                volumeVariance[next] = cubes[next].Vol > 1 ? Variance(cubes[next]) : 0.0;
                volumeVariance[i] = cubes[i].Vol > 1 ? Variance(cubes[i]) : 0.0;
            }
            else
            {
                volumeVariance[next] = 0.0;
                i--;
            }

            next = 0;

            double temp = volumeVariance[0];
            for (int j = 1; j <= i; j++)
            {
                if (volumeVariance[j] > temp)
                {
                    temp = volumeVariance[j];
                    next = j;
                }
            }
            if (temp <= 0.0)
            {
                generatedColorCount = i + 1;
                break;
            }
        }

        return new CreateBoxesResult(maxColorCount, generatedColorCount);
    }

    private List<uint> CreateResult(int colorCount)
    {
        List<uint> colors = new();
        for (int i = 0; i < colorCount; ++i)
        {
            Box cube = cubes[i];
            int weight = Volume(cube, weights);
            if (weight > 0)
            {
                int r = Volume(cube, momentsR) / weight;
                int g = Volume(cube, momentsG) / weight;
                int b = Volume(cube, momentsB) / weight;
                uint color = 0xFF000000u | (((uint)r & 0x0ffu) << 16) | (((uint)g & 0x0ffu) << 8) | ((uint)b & 0x0ffu);
                colors.Add(color);
            }
        }
        return colors;
    }

    private double Variance(Box cube)
    {
        int dr = Volume(cube, momentsR);
        int dg = Volume(cube, momentsG);
        int db = Volume(cube, momentsB);
        double xx =
            moments[GetIndex(cube.R1, cube.G1, cube.B1)]
                - moments[GetIndex(cube.R1, cube.G1, cube.B0)]
                - moments[GetIndex(cube.R1, cube.G0, cube.B1)]
                + moments[GetIndex(cube.R1, cube.G0, cube.B0)]
                - moments[GetIndex(cube.R0, cube.G1, cube.B1)]
                + moments[GetIndex(cube.R0, cube.G1, cube.B0)]
                + moments[GetIndex(cube.R0, cube.G0, cube.B1)]
                - moments[GetIndex(cube.R0, cube.G0, cube.B0)];

        int hypotenuse = (dr * dr) + (dg * dg) + (db * db);
        int volume = Volume(cube, weights);
        return xx - (hypotenuse / (double)volume);
    }

    private bool Cut(Box one, Box two)
    {
        int wholeR = Volume(one, momentsR);
        int wholeG = Volume(one, momentsG);
        int wholeB = Volume(one, momentsB);
        int wholeW = Volume(one, weights);

        MaximizeResult maxRResult =
            Maximize(one, Direction.Red, one.R0 + 1, one.R1, wholeR, wholeG, wholeB, wholeW);
        MaximizeResult maxGResult =
            Maximize(one, Direction.Green, one.G0 + 1, one.G1, wholeR, wholeG, wholeB, wholeW);
        MaximizeResult maxBResult =
            Maximize(one, Direction.Blue, one.B0 + 1, one.B1, wholeR, wholeG, wholeB, wholeW);
        Direction cutDirection;
        double maxR = maxRResult.Maximum;
        double maxG = maxGResult.Maximum;
        double maxB = maxBResult.Maximum;
        if (maxR >= maxG && maxR >= maxB)
        {
            if (maxRResult.CutLocation < 0)
            {
                return false;
            }
            cutDirection = Direction.Red;
        }
        else if (maxG >= maxR && maxG >= maxB)
        {
            cutDirection = Direction.Green;
        }
        else
        {
            cutDirection = Direction.Blue;
        }

        two.R1 = one.R1;
        two.G1 = one.G1;
        two.B1 = one.B1;

        switch (cutDirection)
        {
            case Direction.Red:
                one.R1 = maxRResult.CutLocation;
                two.R0 = one.R1;
                two.G0 = one.G0;
                two.B0 = one.B0;
                break;
            case Direction.Green:
                one.G1 = maxGResult.CutLocation;
                two.R0 = one.R0;
                two.G0 = one.G1;
                two.B0 = one.B0;
                break;
            case Direction.Blue:
                one.B1 = maxBResult.CutLocation;
                two.R0 = one.R0;
                two.G0 = one.G0;
                two.B0 = one.B1;
                break;
        }

        one.Vol = (one.R1 - one.R0) * (one.G1 - one.G0) * (one.B1 - one.B0);
        two.Vol = (two.R1 - two.R0) * (two.G1 - two.G0) * (two.B1 - two.B0);

        return true;
    }

    private MaximizeResult Maximize(
        Box cube,
        Direction direction,
        int first,
        int last,
        int wholeR,
        int wholeG,
        int wholeB,
        int wholeW)
    {
        int bottomR = Bottom(cube, direction, momentsR);
        int bottomG = Bottom(cube, direction, momentsG);
        int bottomB = Bottom(cube, direction, momentsB);
        int bottomW = Bottom(cube, direction, weights);

        double max = 0.0;
        int cut = -1;

        int halfR = 0;
        int halfG = 0;
        int halfB = 0;
        int halfW = 0;
        for (int i = first; i < last; i++)
        {
            halfR = bottomR + Top(cube, direction, i, momentsR);
            halfG = bottomG + Top(cube, direction, i, momentsG);
            halfB = bottomB + Top(cube, direction, i, momentsB);
            halfW = bottomW + Top(cube, direction, i, weights);
            if (halfW == 0)
            {
                continue;
            }

            double tempNumerator = (halfR * halfR) + (halfG * halfG) + (halfB * halfB);
            double tempDenominator = halfW;
            double temp = tempNumerator / tempDenominator;

            halfR = wholeR - halfR;
            halfG = wholeG - halfG;
            halfB = wholeB - halfB;
            halfW = wholeW - halfW;
            if (halfW == 0)
            {
                continue;
            }

            tempNumerator = (halfR * halfR) + (halfG * halfG) + (halfB * halfB);
            tempDenominator = halfW;
            temp += tempNumerator / tempDenominator;

            if (temp > max)
            {
                max = temp;
                cut = i;
            }
        }
        return new MaximizeResult(cut, max);
    }

    private static int Volume(Box cube, int[] moment)
    {
        return moment[GetIndex(cube.R1, cube.G1, cube.B1)]
            - moment[GetIndex(cube.R1, cube.G1, cube.B0)]
            - moment[GetIndex(cube.R1, cube.G0, cube.B1)]
            + moment[GetIndex(cube.R1, cube.G0, cube.B0)]
            - moment[GetIndex(cube.R0, cube.G1, cube.B1)]
            + moment[GetIndex(cube.R0, cube.G1, cube.B0)]
            + moment[GetIndex(cube.R0, cube.G0, cube.B1)]
            - moment[GetIndex(cube.R0, cube.G0, cube.B0)];
    }

    private static int Bottom(Box cube, Direction direction, int[] moment)
    {
        switch (direction)
        {
            case Direction.Red:
                return -moment[GetIndex(cube.R0, cube.G1, cube.B1)]
                    + moment[GetIndex(cube.R0, cube.G1, cube.B0)]
                    + moment[GetIndex(cube.R0, cube.G0, cube.B1)]
                    - moment[GetIndex(cube.R0, cube.G0, cube.B0)];
            case Direction.Green:
                return -moment[GetIndex(cube.R1, cube.G0, cube.B1)]
                    + moment[GetIndex(cube.R1, cube.G0, cube.B0)]
                    + moment[GetIndex(cube.R0, cube.G0, cube.B1)]
                    - moment[GetIndex(cube.R0, cube.G0, cube.B0)];
            case Direction.Blue:
                return -moment[GetIndex(cube.R1, cube.G1, cube.B0)]
                    + moment[GetIndex(cube.R1, cube.G0, cube.B0)]
                    + moment[GetIndex(cube.R0, cube.G1, cube.B0)]
                    - moment[GetIndex(cube.R0, cube.G0, cube.B0)];
        }
        throw new ArgumentException("unexpected direction " + direction);
    }

    private static int Top(Box cube, Direction direction, int position, int[] moment)
    {
        switch (direction)
        {
            case Direction.Red:
                return moment[GetIndex(position, cube.G1, cube.B1)]
                    - moment[GetIndex(position, cube.G1, cube.B0)]
                    - moment[GetIndex(position, cube.G0, cube.B1)]
                    + moment[GetIndex(position, cube.G0, cube.B0)];
            case Direction.Green:
                return moment[GetIndex(cube.R1, position, cube.B1)]
                    - moment[GetIndex(cube.R1, position, cube.B0)]
                    - moment[GetIndex(cube.R0, position, cube.B1)]
                    + moment[GetIndex(cube.R0, position, cube.B0)];
            case Direction.Blue:
                return moment[GetIndex(cube.R1, cube.G1, position)]
                    - moment[GetIndex(cube.R1, cube.G0, position)]
                    - moment[GetIndex(cube.R0, cube.G1, position)]
                    + moment[GetIndex(cube.R0, cube.G0, position)];
        }
        throw new ArgumentException("unexpected direction " + direction);
    }

    private enum Direction
    {
        Red,
        Green,
        Blue,
    }

    private sealed class MaximizeResult
    {
        // < 0 if cut impossible
        public int CutLocation;
        public double Maximum;

        public MaximizeResult(int cut, double max)
        {
            CutLocation = cut;
            Maximum = max;
        }
    }

    private sealed class CreateBoxesResult
    {
        public int ResultCount;

        public CreateBoxesResult(int requestedCount, int resultCount)
        {
            ResultCount = resultCount;
        }
    }

    private sealed class Box
    {
        public int R0 = 0;
        public int R1 = 0;
        public int G0 = 0;
        public int G1 = 0;
        public int B0 = 0;
        public int B1 = 0;
        public int Vol = 0;
    }
}
