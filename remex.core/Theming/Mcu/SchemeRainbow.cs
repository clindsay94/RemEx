// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/SchemeRainbow.java
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

/// <summary>A playful theme - the source color's hue does not appear in the theme.</summary>
public sealed class SchemeRainbow : DynamicScheme
{
    public SchemeRainbow(Hct sourceColorHct, bool isDark, double contrastLevel)
        : base(
            sourceColorHct,
            SchemeVariant.Rainbow,
            isDark,
            contrastLevel,
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 48.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 16.0),
            TonalPalette.FromHueAndChroma(MathUtils.SanitizeDegreesDouble(sourceColorHct.Hue + 60.0), 24.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 0.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, 0.0))
    {
    }
}
