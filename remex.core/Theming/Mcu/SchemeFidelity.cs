// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/SchemeFidelity.java
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

/// <summary>A scheme that places the source color in Scheme.primaryContainer.</summary>
public sealed class SchemeFidelity : DynamicScheme
{
    public SchemeFidelity(Hct sourceColorHct, bool isDark, double contrastLevel)
        : base(
            sourceColorHct,
            SchemeVariant.Fidelity,
            isDark,
            contrastLevel,
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, sourceColorHct.Chroma),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, Math.Max(sourceColorHct.Chroma - 32.0, sourceColorHct.Chroma * 0.5)),
            TonalPalette.FromHct(DislikeAnalyzer.FixIfDisliked(new TemperatureCache(sourceColorHct).GetComplement())),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, sourceColorHct.Chroma / 8.0),
            TonalPalette.FromHueAndChroma(sourceColorHct.Hue, sourceColorHct.Chroma / 8.0 + 4.0))
    {
    }
}
