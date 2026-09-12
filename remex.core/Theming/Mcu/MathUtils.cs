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

    /// <summary>
    /// java.lang.Math.toRadians(double) (rule 4): AOSP ojluni / JDK 9+ multiply by the constant
    /// 0.017453292519943295 rather than computing (d * PI) / 180 as .NET's double.DegreesToRadians
    /// does — the two differ by up to 1 ulp on a fraction of inputs. Transcribed from the JDK constant,
    /// not from a division.
    /// </summary>
    public static double ToRadians(double angdeg) => angdeg * 0.017453292519943295;

    /// <summary>
    /// java.lang.Math.toDegrees(double) (rule 4): AOSP ojluni / JDK 9+ multiply by the constant
    /// 57.29577951308232 rather than computing (r * 180) / PI as .NET's double.RadiansToDegrees does.
    /// </summary>
    public static double ToDegrees(double angrad) => angrad * 57.29577951308232;

    // ===========================================================================================
    // Log1p / Expm1 — transcribed from fdlibm (Sun Microsystems, 1993), the same algorithm
    // java.lang.Math/StrictMath (jdk.internal.math.FdLibm) and Android bionic libm use for
    // log1p/expm1. Reference: FreeBSD /usr/src/lib/msun/src/s_log1p.c and s_expm1.c, as vendored by
    // musl libc (https://github.com/kraj/musl/blob/master/src/math/log1p.c and expm1.c) — fetched
    // 2026-09-12 since this repo has no local fdlibm/OpenJDK checkout. .NET has no Log1p/Expm1 API
    // (its LogP1/ExpM1 are naive log(1+x)/[exp(x)-1] and diverge from fdlibm on ~40% of the small
    // inputs CAM16-UCS conversion uses); this transcription reproduces java.lang.Math bit-for-bit.
    // ===========================================================================================

    private const double Log1pLn2Hi = 6.93147180369123816490e-01;
    private const double Log1pLn2Lo = 1.90821492927058770002e-10;
    private const double Log1pLg1 = 6.666666666666735130e-01;
    private const double Log1pLg2 = 3.999999999940941908e-01;
    private const double Log1pLg3 = 2.857142874366239149e-01;
    private const double Log1pLg4 = 2.222219843214978396e-01;
    private const double Log1pLg5 = 1.818357216161805012e-01;
    private const double Log1pLg6 = 1.531383769920937332e-01;
    private const double Log1pLg7 = 1.479819860511658591e-01;

    /// <summary>Returns the natural logarithm of 1+x, bit-exact with java.lang.Math.log1p (fdlibm s_log1p.c).</summary>
    public static double Log1p(double x)
    {
        double f = 0.0, c = 0.0;
        ulong ix = BitConverter.DoubleToUInt64Bits(x);
        uint hx = (uint)(ix >> 32);
        int k = 1;
        if (hx < 0x3fda827au || (hx >> 31) != 0)
        {
            // 1+x < sqrt(2)+
            if (hx >= 0xbff00000u)
            {
                // x <= -1.0
                if (x == -1.0)
                {
                    return x / 0.0; // log1p(-1) = -inf
                }
                return (x - x) / 0.0; // log1p(x < -1) = NaN
            }
            if ((hx << 1) < (0x3ca00000u << 1))
            {
                // |x| < 2**-53 — underflow if subnormal (no FORCE_EVAL side effect needed in C#)
                return x;
            }
            if (hx <= 0xbfd2bec4u)
            {
                // sqrt(2)/2- <= 1+x < sqrt(2)+
                k = 0;
                c = 0;
                f = x;
            }
        }
        else if (hx >= 0x7ff00000u)
        {
            return x;
        }
        if (k != 0)
        {
            double u = 1 + x;
            ulong ui = BitConverter.DoubleToUInt64Bits(u);
            uint hu = (uint)(ui >> 32);
            hu += 0x3ff00000u - 0x3fe6a09eu;
            k = (int)(hu >> 20) - 0x3ff;
            // correction term ~ log(1+x)-log(u), avoid underflow in c/u
            if (k < 54)
            {
                c = k >= 2 ? 1 - (u - x) : x - (u - 1);
                c /= u;
            }
            else
            {
                c = 0;
            }
            // reduce u into [sqrt(2)/2, sqrt(2)]
            hu = (hu & 0x000fffffu) + 0x3fe6a09eu;
            ui = ((ulong)hu << 32) | (ui & 0xffffffffu);
            u = BitConverter.UInt64BitsToDouble(ui);
            f = u - 1;
        }
        double hfsq = 0.5 * f * f;
        double s = f / (2.0 + f);
        double z = s * s;
        double w = z * z;
        double t1 = w * (Log1pLg2 + w * (Log1pLg4 + w * Log1pLg6));
        double t2 = z * (Log1pLg1 + w * (Log1pLg3 + w * (Log1pLg5 + w * Log1pLg7)));
        double r = t2 + t1;
        double dk = k;
        return s * (hfsq + r) + (dk * Log1pLn2Lo + c) - hfsq + f + dk * Log1pLn2Hi;
    }

    private const double Expm1OThreshold = 7.09782712893383973096e+02;
    private const double Expm1Ln2Hi = 6.93147180369123816490e-01;
    private const double Expm1Ln2Lo = 1.90821492927058770002e-10;
    private const double Expm1InvLn2 = 1.44269504088896338700e+00;
    private const double Expm1Q1 = -3.33333333333331316428e-02;
    private const double Expm1Q2 = 1.58730158725481460165e-03;
    private const double Expm1Q3 = -7.93650757867487942473e-05;
    private const double Expm1Q4 = 4.00821782732936239552e-06;
    private const double Expm1Q5 = -2.01099218183624371326e-07;

    /// <summary>Returns exp(x)-1, bit-exact with java.lang.Math.expm1 (fdlibm s_expm1.c).</summary>
    public static double Expm1(double x)
    {
        ulong ix = BitConverter.DoubleToUInt64Bits(x);
        uint hx = (uint)((ix >> 32) & 0x7fffffffu);
        int sign = (int)(ix >> 63);

        // filter out huge and non-finite argument
        if (hx >= 0x4043687Au)
        {
            // if |x| >= 56*ln2
            if (double.IsNaN(x))
            {
                return x;
            }
            if (sign != 0)
            {
                return -1;
            }
            if (x > Expm1OThreshold)
            {
                x *= Math.ScaleB(1.0, 1023); // 0x1p1023
                return x;
            }
        }

        double hi, lo, c = 0.0, t;
        int k;
        // argument reduction
        if (hx > 0x3fd62e42u)
        {
            // if |x| > 0.5 ln2
            if (hx < 0x3FF0A2B2u)
            {
                // and |x| < 1.5 ln2
                if (sign == 0)
                {
                    hi = x - Expm1Ln2Hi;
                    lo = Expm1Ln2Lo;
                    k = 1;
                }
                else
                {
                    hi = x + Expm1Ln2Hi;
                    lo = -Expm1Ln2Lo;
                    k = -1;
                }
            }
            else
            {
                k = (int)(Expm1InvLn2 * x + (sign != 0 ? -0.5 : 0.5));
                t = k;
                hi = x - t * Expm1Ln2Hi; // t*ln2_hi is exact here
                lo = t * Expm1Ln2Lo;
            }
            x = hi - lo;
            c = (hi - x) - lo;
        }
        else if (hx < 0x3c900000u)
        {
            // |x| < 2**-54, return x (no FORCE_EVAL side effect needed in C#)
            return x;
        }
        else
        {
            k = 0;
        }

        // x is now in primary range
        double hfx = 0.5 * x;
        double hxs = x * hfx;
        double r1 = 1.0 + hxs * (Expm1Q1 + hxs * (Expm1Q2 + hxs * (Expm1Q3 + hxs * (Expm1Q4 + hxs * Expm1Q5))));
        t = 3.0 - r1 * hfx;
        double e = hxs * ((r1 - t) / (6.0 - x * t));
        if (k == 0)
        {
            // c is 0
            return x - (x * e - hxs);
        }
        e = x * (e - c) - c;
        e -= hxs;
        // exp(x) ~ 2^k (x_reduced - e + 1)
        if (k == -1)
        {
            return 0.5 * (x - e) - 0.5;
        }
        if (k == 1)
        {
            if (x < -0.25)
            {
                return -2.0 * (e - (x + 0.5));
            }
            return 1.0 + 2.0 * (x - e);
        }
        double twopk = BitConverter.UInt64BitsToDouble((ulong)(0x3ff + k) << 52); // 2^k
        double y;
        if (k < 0 || k > 56)
        {
            // suffice to return exp(x)-1
            y = x - e + 1.0;
            if (k == 1024)
            {
                y = y * 2.0 * Math.ScaleB(1.0, 1023); // 0x1p1023
            }
            else
            {
                y = y * twopk;
            }
            return y - 1.0;
        }
        double twoNegK = BitConverter.UInt64BitsToDouble((ulong)(0x3ff - k) << 52); // 2^-k
        if (k < 20)
        {
            y = (x - e + (1 - twoNegK)) * twopk;
        }
        else
        {
            y = (x - (e + twoNegK) + 1) * twopk;
        }
        return y;
    }
}
