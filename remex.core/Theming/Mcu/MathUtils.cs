// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/MathUtils.java
/*
 * Copyright (C) 2021 The Android Open Source Project
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

/// <summary>Utility methods for mathematical operations.</summary>
public static class MathUtils
{
    /// <summary>The signum function. Returns 1 if num &gt; 0, -1 if num &lt; 0, and 0 if num = 0.</summary>
    public static int Signum(double num)
    {
        if (num < 0)
        {
            return -1;
        }
        else if (num == 0)
        {
            return 0;
        }
        else
        {
            return 1;
        }
    }

    /// <summary>Java Math.signum(double) (rule 4): returns 1.0/-1.0/0.0 as doubles, NaN-safe (Math.Sign throws on NaN).</summary>
    public static double SignumDouble(double num) => num > 0 ? 1.0 : num < 0 ? -1.0 : 0.0;

    /// <summary>The linear interpolation function. Returns start if amount = 0 and stop if amount = 1.</summary>
    public static double Lerp(double start, double stop, double amount)
    {
        return (1.0 - amount) * start + amount * stop;
    }

    /// <summary>Clamps an integer between two integers. Returns input when min &lt;= input &lt;= max, and either min or max otherwise.</summary>
    public static int ClampInt(int min, int max, int input)
    {
        if (input < min)
        {
            return min;
        }
        else if (input > max)
        {
            return max;
        }

        return input;
    }

    /// <summary>Clamps an integer between two floating-point numbers. Returns input when min &lt;= input &lt;= max, and either min or max otherwise.</summary>
    public static double ClampDouble(double min, double max, double input)
    {
        if (input < min)
        {
            return min;
        }
        else if (input > max)
        {
            return max;
        }

        return input;
    }

    /// <summary>Sanitizes a degree measure as an integer. Returns a degree measure between 0 (inclusive) and 360 (exclusive).</summary>
    public static int SanitizeDegreesInt(int degrees)
    {
        degrees = degrees % 360;
        if (degrees < 0)
        {
            degrees = degrees + 360;
        }
        return degrees;
    }

    /// <summary>Sanitizes a degree measure as a floating-point number. Returns a degree measure between 0.0 (inclusive) and 360.0 (exclusive).</summary>
    public static double SanitizeDegreesDouble(double degrees)
    {
        degrees = degrees % 360.0;
        if (degrees < 0)
        {
            degrees = degrees + 360.0;
        }
        return degrees;
    }

    /// <summary>
    /// Sign of direction change needed to travel from one angle to another.
    /// For angles that are 180 degrees apart from each other, both directions have the same travel
    /// distance, so either direction is shortest. The value 1.0 is returned in this case.
    /// </summary>
    /// <param name="from">The angle travel starts from, in degrees.</param>
    /// <param name="to">The angle travel ends at, in degrees.</param>
    /// <returns>-1 if decreasing from leads to the shortest travel distance, 1 if increasing from leads to the shortest travel distance.</returns>
    public static double RotationDirection(double from, double to)
    {
        double increasingDifference = SanitizeDegreesDouble(to - from);
        return increasingDifference <= 180.0 ? 1.0 : -1.0;
    }

    /// <summary>Distance of two points on a circle, represented using degrees.</summary>
    public static double DifferenceDegrees(double a, double b)
    {
        return 180.0 - Math.Abs(Math.Abs(a - b) - 180.0);
    }

    /// <summary>Multiplies a 1x3 row vector with a 3x3 matrix.</summary>
    public static double[] MatrixMultiply(double[] row, double[][] matrix)
    {
        double a = row[0] * matrix[0][0] + row[1] * matrix[0][1] + row[2] * matrix[0][2];
        double b = row[0] * matrix[1][0] + row[1] * matrix[1][1] + row[2] * matrix[1][2];
        double c = row[0] * matrix[2][0] + row[1] * matrix[2][1] + row[2] * matrix[2][2];
        return new double[] { a, b, c };
    }
}
