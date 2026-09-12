// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/QuantizerCelebi.java
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
using System.Collections.Generic;

namespace Remex.Core.Theming.Mcu;

/// <summary>
/// An image quantizer that improves on the quality of a standard K-Means algorithm by setting the
/// K-Means initial state to the output of a Wu quantizer, instead of random centroids. Improves on
/// speed by several optimizations, as implemented in Wsmeans, or Weighted Square Means, K-Means with
/// those optimizations.
///
/// This algorithm was designed by M. Emre Celebi, and was found in their 2011 paper, Improving
/// the Performance of K-Means for Color Quantization. https://arxiv.org/abs/1101.0395
/// </summary>
public static class QuantizerCelebi
{
    /// <summary>
    /// Reduce the number of colors needed to represented the input, minimizing the difference between
    /// the original image and the recolored image.
    /// </summary>
    /// <param name="pixels">Colors in ARGB format.</param>
    /// <param name="maxColors">
    /// The number of colors to divide the image into. A lower number of colors may be returned.
    /// </param>
    /// <returns>
    /// Map with keys of colors in ARGB format, and values of number of pixels in the original
    /// image that correspond to the color in the quantized image.
    /// </returns>
    public static Dictionary<uint, int> Quantize(uint[] pixels, int maxColors)
    {
        QuantizerWu wu = new QuantizerWu();
        QuantizerResult wuResult = wu.Quantize(pixels, maxColors);

        var resultMap = wuResult.ColorToCount;
        uint[] wuClusters = new uint[resultMap.Count];
        int index = 0;
        foreach (var argb in resultMap.Keys)
        {
            wuClusters[index++] = argb;
        }

        return QuantizerWsmeans.Quantize(pixels, wuClusters, maxColors);
    }
}
