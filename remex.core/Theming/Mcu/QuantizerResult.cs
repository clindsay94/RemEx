// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/QuantizerResult.java
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

/// <summary>Represents result of a quantizer run.</summary>
public sealed class QuantizerResult
{
    public Dictionary<uint, int> ColorToCount { get; }

    public QuantizerResult(Dictionary<uint, int> colorToCount)
    {
        ColorToCount = colorToCount;
    }
}
