// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/ToneDeltaPair.java
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

/// <summary>Documents a constraint between two DynamicColors, in which their tones must have a certain distance from each other.</summary>
public sealed class ToneDeltaPair
{
    public DynamicColor RoleA { get; }
    public DynamicColor RoleB { get; }
    public double Delta { get; }
    public TonePolarity Polarity { get; }
    public bool StayTogether { get; }

    public ToneDeltaPair(DynamicColor roleA, DynamicColor roleB, double delta, TonePolarity polarity, bool stayTogether)
    {
        RoleA = roleA;
        RoleB = roleB;
        Delta = delta;
        Polarity = polarity;
        StayTogether = stayTogether;
    }
}
