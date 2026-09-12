using System;

namespace Remex.Core.Theming.Mcu;

/// <summary>java.util.Random's 48-bit LCG, only as far as QuantizerWsmeans uses it (nextInt(bound)). Bit-identical to the JDK so cluster seeding matches the phone's library.</summary>
internal sealed class JavaRandom
{
    private const long Multiplier = 0x5DEECE66DL;
    private const long Addend = 0xBL;
    private const long Mask = (1L << 48) - 1;
    private long seed;

    public JavaRandom(long seed) => this.seed = (seed ^ Multiplier) & Mask;

    private int Next(int bits)
    {
        seed = (seed * Multiplier + Addend) & Mask;
        return (int)(seed >> (48 - bits));
    }

    public int NextInt(int bound)
    {
        if (bound <= 0) throw new ArgumentOutOfRangeException(nameof(bound), bound, "bound must be positive");
        if ((bound & -bound) == bound) return (int)((bound * (long)Next(31)) >> 31);
        int bits, val;
        do { bits = Next(31); val = bits % bound; } while (bits - val + (bound - 1) < 0);
        return val;
    }
}
