using System.Globalization;

namespace Remex.Desktop.Services;

/// <summary>
/// Orders file names the way a person expects rather than the way a byte comparison does
/// (RemEx-93bb).
/// </summary>
/// <remarks>
/// <para>
/// **PLAIN STRING COMPARISON PUTS <c>file10</c> BEFORE <c>file9</c>**, because it compares `1`
/// against `9` and stops. In a file manager that is not a cosmetic complaint: a user scanning a
/// numbered sequence for a gap sees a list that appears to skip and repeat, and clicking a sort
/// header makes it worse rather than better. Every OS file browser does natural ordering, so a
/// column header that sorts differently reads as broken.
/// </para>
/// <para>
/// Comparison of the letter runs is CULTURE-AWARE, which is the opposite of the rule in
/// <c>FileConflictNaming</c> and for the opposite reason: that one models a filesystem deciding
/// whether two files are the same, while this one models a person reading a list. A Swedish user
/// expects <c>ä</c> after <c>z</c>; an ordinal sort would file it after <c>Z</c> and before <c>a</c>,
/// which is nobody's alphabet.
/// </para>
/// </remarks>
public sealed class NaturalFileNameComparer : IComparer<string>
{
    /// <summary>Shared instance; the comparer holds no state.</summary>
    public static readonly NaturalFileNameComparer Instance = new();

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var i = 0;
        var j = 0;

        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var comparison = CompareNumberRun(x, ref i, y, ref j);
                if (comparison != 0) return comparison;
                continue;
            }

            var letters = string.Compare(
                x[i].ToString(), y[j].ToString(), CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);

            if (letters != 0) return letters;

            i++;
            j++;
        }

        // One is a prefix of the other: the shorter sorts first.
        if (i < x.Length) return 1;
        if (j < y.Length) return -1;

        // EQUAL UNDER THE DISPLAY RULES, SO FALL BACK TO AN ORDINAL TIEBREAK. Without this,
        // "File.txt" and "file.txt" compare equal and a sort may interleave them differently on
        // every refresh - a list that reshuffles under the user while nothing changed. The tiebreak
        // is arbitrary but it is STABLE, which is the property that matters.
        return string.CompareOrdinal(x, y);
    }

    /// <summary>
    /// Compares the digit runs starting at <paramref name="i"/> and <paramref name="j"/>, advancing
    /// both past them.
    /// </summary>
    /// <remarks>
    /// **THE RUNS ARE NOT PARSED INTO A NUMBER, AND THAT IS DELIBERATE.** The obvious implementation
    /// calls <c>int.Parse</c> or <c>long.Parse</c> on each run, which throws or silently overflows
    /// the moment a file is named with a 20-digit id — a timestamp in nanoseconds, a hash, a
    /// database key. Comparing significant-digit COUNT and then the digits themselves has no upper
    /// bound and no allocation.
    /// </remarks>
    private static int CompareNumberRun(string x, ref int i, string y, ref int j)
    {
        // Leading zeros carry no numeric weight: "007" and "7" are the same number, and treating
        // them as different puts a padded sequence in the wrong place.
        while (i < x.Length && x[i] == '0') i++;
        while (j < y.Length && y[j] == '0') j++;

        var xStart = i;
        var yStart = j;

        while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
        while (j < y.Length && char.IsAsciiDigit(y[j])) j++;

        var xDigits = i - xStart;
        var yDigits = j - yStart;

        // More significant digits means a larger number - no parsing, no overflow, any length.
        if (xDigits != yDigits) return xDigits - yDigits;

        return string.CompareOrdinal(x[xStart..i], y[yStart..j]);
    }
}
