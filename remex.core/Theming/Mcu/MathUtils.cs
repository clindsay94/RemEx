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
    // Log1p / Expm1 — transcribed from OpenJDK's jdk.internal.math.FdLibm (java.base/java/lang/
    // FdLibm.java, Temurin 21.0.x, java/lang/FdLibm.java lines ~2795-2891 and ~2987-3099), the
    // exact algorithm java.lang.Math/StrictMath.log1p/expm1 (and, by lineage, Android bionic
    // libm/ART) use — itself a transcription of Sun Microsystems' 1993 fdlibm s_log1p.c/s_expm1.c.
    // Verified against the FreeBSD msun REWRITE of the same fdlibm source is a DIFFERENT algorithm
    // (even/odd polynomial split, single left-to-right sum, different short-circuit thresholds) and
    // is bit-INCOMPATIBLE on ~0.05% of inputs — see the round-1 review finding (RemEx-4kv0g.8,
    // 2026-09-12): comparing the musl/FreeBSD port against JDK 21 StrictMath.log1p over 736,019
    // inputs found 349 one-ulp mismatches, 207 of them in the CAM16-UCS domain 0.0228*m. This port
    // is transcribed from FdLibm.Log1p.compute/FdLibm.Expm1.compute directly and is bit-exact
    // against StrictMath.log1p/expm1 (verified: 0/736,019 mismatches for both functions, fix round
    // 2). .NET has no Log1p/Expm1 API (its LogP1/ExpM1 are naive log(1+x)/[exp(x)-1]).
    // ===========================================================================================

    private const double Log1pLn2Hi = 6.93147180369123816490e-01;
    private const double Log1pLn2Lo = 1.90821492927058770002e-10;
    private const double Log1pLp1 = 6.666666666666735130e-01;
    private const double Log1pLp2 = 3.999999999940941908e-01;
    private const double Log1pLp3 = 2.857142874366239149e-01;
    private const double Log1pLp4 = 2.222219843214978396e-01;
    private const double Log1pLp5 = 1.818357216161805012e-01;
    private const double Log1pLp6 = 1.531383769920937332e-01;
    private const double Log1pLp7 = 1.479819860511658591e-01;
    private const double Two54 = 1.80143985094819840000e+16; // 0x1.0p54

    private const int SignBit = unchecked((int)0x8000_0000);
    private const int ExpBits = 0x7ff0_0000;
    private const int ExpSignifBits = 0x7fff_ffff;

    /// <summary>High 32 bits of the IEEE-754 representation, as a signed int (matches FdLibm's __HI).</summary>
    private static int Hi(double x) => (int)(BitConverter.DoubleToInt64Bits(x) >> 32);

    /// <summary>Low 32 bits of the IEEE-754 representation, as a signed int (matches FdLibm's __LO).</summary>
    private static int Lo(double x) => (int)BitConverter.DoubleToInt64Bits(x);

    /// <summary>Returns x with its high 32 bits replaced by <paramref name="high"/> (matches FdLibm's __HI(x, high)).</summary>
    private static double WithHi(double x, int high)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        long newBits = ((long)(uint)high << 32) | (bits & 0xffff_ffffL);
        return BitConverter.Int64BitsToDouble(newBits);
    }

    /// <summary>Returns the natural logarithm of 1+x, bit-exact with java.lang.Math.log1p (FdLibm.Log1p.compute).</summary>
    public static double Log1p(double x)
    {
        double hfsq, f = 0, c = 0, s, z, r, u;
        int k, hu = 0;

        int hx = Hi(x);
        int ax = hx & ExpSignifBits;

        k = 1;
        if (hx < 0x3FDA_827A)
        {
            // x < 0.41422
            if (ax >= 0x3ff0_0000)
            {
                // x <= -1.0
                if (x == -1.0)
                {
                    return double.NegativeInfinity; // log1p(-1) = -inf
                }
                else
                {
                    return double.NaN; // log1p(x < -1) = NaN
                }
            }

            if (ax < 0x3e20_0000)
            {
                // |x| < 2**-29
                if (Two54 + x > 0.0 // raise inexact
                    && ax < 0x3c90_0000) // |x| < 2**-54
                {
                    return x;
                }
                else
                {
                    return x - x * x * 0.5;
                }
            }

            if (hx > 0 || hx <= unchecked((int)0xbfd2_bec3))
            {
                // -0.2929 < x < 0.41422
                k = 0;
                f = x;
                hu = 1;
            }
        }

        if (hx >= ExpBits)
        {
            return x + x;
        }

        if (k != 0)
        {
            if (hx < 0x4340_0000)
            {
                u = 1.0 + x;
                hu = Hi(u); // high word of u
                k = (hu >> 20) - 1023;
                c = (k > 0) ? 1.0 - (u - x) : x - (u - 1.0); // correction term
                c /= u;
            }
            else
            {
                u = x;
                hu = Hi(u); // high word of u
                k = (hu >> 20) - 1023;
                c = 0;
            }
            hu &= 0x000f_ffff;
            if (hu < 0x6_a09e)
            {
                u = WithHi(u, hu | 0x3ff0_0000); // normalize u
            }
            else
            {
                k += 1;
                u = WithHi(u, hu | 0x3fe0_0000); // normalize u/2
                hu = (0x0010_0000 - hu) >> 2;
            }
            f = u - 1.0;
        }

        hfsq = 0.5 * f * f;
        if (hu == 0)
        {
            // |f| < 2**-20
            if (f == 0.0)
            {
                if (k == 0)
                {
                    return 0.0;
                }
                else
                {
                    c += k * Log1pLn2Lo;
                    return k * Log1pLn2Hi + c;
                }
            }
            r = hfsq * (1.0 - 0.66666666666666666 * f);
            if (k == 0)
            {
                return f - r;
            }
            else
            {
                return k * Log1pLn2Hi - ((r - (k * Log1pLn2Lo + c)) - f);
            }
        }
        s = f / (2.0 + f);
        z = s * s;
        r = z * (Log1pLp1 + z * (Log1pLp2 + z * (Log1pLp3 + z * (Log1pLp4 + z * (Log1pLp5 + z * (Log1pLp6 + z * Log1pLp7))))));
        if (k == 0)
        {
            return f - (hfsq - s * (hfsq + r));
        }
        else
        {
            return k * Log1pLn2Hi - ((hfsq - (s * (hfsq + r) + (k * Log1pLn2Lo + c))) - f);
        }
    }

    private const double Expm1Huge = 1.0e+300;
    private const double Expm1Tiny = 1.0e-300;
    private const double Expm1OThreshold = 7.09782712893383973096e+02;
    private const double Expm1Ln2Hi = 6.93147180369123816490e-01;
    private const double Expm1Ln2Lo = 1.90821492927058770002e-10;
    private const double Expm1InvLn2 = 1.44269504088896338700e+00;
    private const double Expm1Q1 = -3.33333333333331316428e-02;
    private const double Expm1Q2 = 1.58730158725481460165e-03;
    private const double Expm1Q3 = -7.93650757867487942473e-05;
    private const double Expm1Q4 = 4.00821782732936239552e-06;
    private const double Expm1Q5 = -2.01099218183624371326e-07;

    /// <summary>Returns exp(x)-1, bit-exact with java.lang.Math.expm1 (FdLibm.Expm1.compute).</summary>
    public static double Expm1(double x)
    {
        double y, hi, lo, c = 0, t, e, hxs, hfx, r1;
        int k;

        int hx = Hi(x); // high word of x
        int xsb = hx & SignBit; // sign bit of x
        y = Math.Abs(x);
        hx &= ExpSignifBits; // high word of |x|

        // filter out huge and non-finite argument
        if (hx >= 0x4043_687A)
        {
            // if |x| >= 56*ln2
            if (hx >= 0x4086_2E42)
            {
                // if |x| >= 709.78...
                if (hx >= 0x7ff_00000)
                {
                    if (((hx & 0xf_ffff) | Lo(x)) != 0)
                    {
                        return x + x; // NaN
                    }
                    else
                    {
                        return (xsb == 0) ? x : -1.0; // exp(+-inf)={inf,-1}
                    }
                }
                if (x > Expm1OThreshold)
                {
                    return Expm1Huge * Expm1Huge; // overflow
                }
            }
            if (xsb != 0)
            {
                // x < -56*ln2, return -1.0 with inexact
                if (x + Expm1Tiny < 0.0) // raise inexact
                {
                    return Expm1Tiny - 1.0; // return -1
                }
            }
        }

        // argument reduction
        if (hx > 0x3fd6_2e42)
        {
            // if |x| > 0.5 ln2
            if (hx < 0x3FF0_A2B2)
            {
                // and |x| < 1.5 ln2
                if (xsb == 0)
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
                k = (int)(Expm1InvLn2 * x + ((xsb == 0) ? 0.5 : -0.5));
                t = k;
                hi = x - t * Expm1Ln2Hi; // t*ln2_hi is exact here
                lo = t * Expm1Ln2Lo;
            }
            x = hi - lo;
            c = (hi - x) - lo;
        }
        else if (hx < 0x3c90_0000)
        {
            // when |x| < 2**-54, return x
            t = Expm1Huge + x; // return x with inexact flags when x != 0
            return x - (t - (Expm1Huge + x));
        }
        else
        {
            k = 0;
        }

        // x is now in primary range
        hfx = 0.5 * x;
        hxs = x * hfx;
        r1 = 1.0 + hxs * (Expm1Q1 + hxs * (Expm1Q2 + hxs * (Expm1Q3 + hxs * (Expm1Q4 + hxs * Expm1Q5))));
        t = 3.0 - r1 * hfx;
        e = hxs * ((r1 - t) / (6.0 - x * t));
        if (k == 0)
        {
            return x - (x * e - hxs); // c is 0
        }
        else
        {
            e = (x * (e - c) - c);
            e -= hxs;
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
                else
                {
                    return 1.0 + 2.0 * (x - e);
                }
            }
            if (k <= -2 || k > 56)
            {
                // suffice to return exp(x) - 1
                y = 1.0 - (e - x);
                y = WithHi(y, Hi(y) + (k << 20)); // add k to y's exponent
                return y - 1.0;
            }
            t = 1.0;
            if (k < 20)
            {
                t = WithHi(t, 0x3ff0_0000 - (0x2_00000 >> k)); // t = 1-2^-k
                y = t - (e - x);
                y = WithHi(y, Hi(y) + (k << 20)); // add k to y's exponent
            }
            else
            {
                t = WithHi(t, (0x3ff - k) << 20); // 2^-k
                y = x - (e + t);
                y += 1.0;
                y = WithHi(y, Hi(y) + (k << 20)); // add k to y's exponent
            }
        }
        return y;
    }
}
