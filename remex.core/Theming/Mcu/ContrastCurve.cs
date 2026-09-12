// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/ContrastCurve.java
/*
 * Copyright (C) 2023 The Android Open Source Project
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

/// <summary>A value that changes with the contrast level. The four values correspond to contrast levels -1.0, 0.0, 0.5, and 1.0.</summary>
public sealed class ContrastCurve
{
    private readonly double low;
    private readonly double normal;
    private readonly double medium;
    private readonly double high;

    public ContrastCurve(double low, double normal, double medium, double high)
    {
        this.low = low;
        this.normal = normal;
        this.medium = medium;
        this.high = high;
    }

    public double Get(double contrastLevel)
    {
        if (contrastLevel <= -1.0) return low;
        else if (contrastLevel < 0.0) return MathUtils.Lerp(low, normal, (contrastLevel - -1) / 1);
        else if (contrastLevel < 0.5) return MathUtils.Lerp(normal, medium, (contrastLevel - 0) / 0.5);
        else if (contrastLevel < 1.0) return MathUtils.Lerp(medium, high, (contrastLevel - 0.5) / 0.5);
        else return high;
    }
}
