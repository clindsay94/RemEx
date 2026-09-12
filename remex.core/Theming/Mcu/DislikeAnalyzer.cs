// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/DislikeAnalyzer.java
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
/// Check and/or fix universally disliked colors.
///
/// Color science studies of color preference indicate universal distaste for dark yellow-greens,
/// and also show this is correlated to distaste for biological waste and rotting food.
///
/// See Palmer and Schloss, 2010 or Schloss and Palmer's Chapter 21 in Handbook of Color
/// Psychology (2015).
/// </summary>
public static class DislikeAnalyzer
{
    /// <summary>
    /// Returns true if color is disliked.
    ///
    /// Disliked is defined as a dark yellow-green that is not neutral.
    /// </summary>
    public static bool IsDisliked(Hct hct)
    {
        bool huePasses = (long)Math.Floor(hct.Hue + 0.5) >= 90.0 && (long)Math.Floor(hct.Hue + 0.5) <= 111.0;
        bool chromaPasses = (long)Math.Floor(hct.Chroma + 0.5) > 16.0;
        bool tonePasses = (long)Math.Floor(hct.Tone + 0.5) < 65.0;

        return huePasses && chromaPasses && tonePasses;
    }

    /// <summary>If color is disliked, lighten it to make it likable.</summary>
    public static Hct FixIfDisliked(Hct hct)
    {
        if (IsDisliked(hct))
        {
            return Hct.From(hct.Hue, hct.Chroma, 70.0);
        }

        return hct;
    }
}
