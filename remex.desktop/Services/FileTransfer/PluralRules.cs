namespace Remex.Desktop.Services.FileTransfer;

/// <summary>The CLDR cardinal-plural category a count falls into.</summary>
public enum PluralCategory
{
    One,
    Few,
    Many,
    Other,
}

/// <summary>
/// Picks the CLDR cardinal-plural category for a count, in the nine locales RemEx ships (RemEx-4lcq).
/// </summary>
/// <remarks>
/// <para>
/// **.NET HAS NO EQUIVALENT OF ANDROID'S <c>&lt;plurals&gt;</c>.** A <c>.resx</c> lookup is a single
/// fixed string keyed by name; there is no ICU plural selector wired to it. Android's
/// <c>getQuantityString</c> does this lookup natively, which is why the platforms otherwise share
/// almost no display code but do share this problem in reverse: Android needed nothing built, and the
/// PC needs the smallest thing that is still CORRECT for its nine locales.
/// </para>
/// <para>
/// SMALLEST, NOT GENERIC. This is not a general ICU plural-rule engine — it hardcodes the rule for
/// exactly the languages RemEx ships, the way <see cref="TransferProgressFormat"/> hardcodes exactly
/// the unit thresholds it needs. A tenth locale is a tenth <c>case</c>, not a config file.
/// </para>
/// <para>
/// EVERY RULE HERE OPERATES ON THE DOMAIN THIS BEAD ACTUALLY HAS: non-negative integer counts of
/// seconds/minutes/hours, always &gt;= 1 (<see cref="TransferEta.Remaining"/> never carries zero —
/// under a second is <see cref="TransferEta.Finishing"/> instead). Some CLDR rules below have a
/// clause for zero that can never fire here; it is kept anyway because leaving it out would make this
/// type quietly wrong the day something else calls it with a zero.
/// </para>
/// </remarks>
public static class PluralRules
{
    /// <summary>
    /// The category <paramref name="n"/> falls into for <paramref name="cultureTag"/>.
    /// </summary>
    /// <param name="cultureTag">
    /// One of the exact tags <c>SettingsViewModel.AvailableLanguages</c> offers — "en", "es", "fr",
    /// "hi", "id", "pl", "pt-BR", "tr", "uk". Matched case-insensitively; anything else (a culture
    /// RemEx does not ship) falls back to the English rule rather than throwing, the same way
    /// <see cref="LocalizationService"/> falls back to English on an unrecognised culture code.
    /// </param>
    /// <param name="n">The count being displayed. Must be non-negative.</param>
    public static PluralCategory Category(string cultureTag, int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), n, "Plural category is undefined for a negative count.");

        return cultureTag.ToLowerInvariant() switch
        {
            "pl" => Polish(n),
            "uk" => Ukrainian(n),
            "fr" => ZeroOrOne(n),
            "hi" => ZeroOrOne(n),
            // TURKISH AND INDONESIAN NEVER PRODUCE "ONE" HERE, DELIBERATELY, NOT BY OMISSION.
            // Android ships a "one" item for tr, but its text is byte-identical to "other" — Turkish
            // does not inflect a noun after a numeral, so the two categories would only ever hold
            // duplicate strings. Indonesian has no cardinal plural distinction at all in real CLDR
            // data. Modelling both as Other-only means the resx for these locales carries one string
            // per unit instead of a second copy of it under a different key.
            "tr" => PluralCategory.Other,
            "id" => PluralCategory.Other,
            // en, es, pt-BR (and anything unrecognised): the ordinary "singular at exactly one" rule.
            _ => n == 1 ? PluralCategory.One : PluralCategory.Other,
        };
    }

    /// <summary>French and Hindi: "one" covers zero as well as one (real CLDR "i = 0 or n = 1").</summary>
    private static PluralCategory ZeroOrOne(int n) => n is 0 or 1 ? PluralCategory.One : PluralCategory.Other;

    /// <summary>
    /// Polish: one/few/many/other, keyed off the last one and two digits rather than fixed ranges —
    /// which is why the CLDR sample set cycles (1 one; 2-4 few; 5-21 many; 22-24 few again; 25 many
    /// again) instead of settling into bands.
    /// </summary>
    private static PluralCategory Polish(int n)
    {
        if (n == 1) return PluralCategory.One;

        var lastDigit = n % 10;
        var lastTwoDigits = n % 100;

        if (lastDigit is >= 2 and <= 4 && lastTwoDigits is not (>= 12 and <= 14))
            return PluralCategory.Few;

        // "n != 1 and last digit is 0 or 1" is its own clause, separate from "last digit 5-9" — losing
        // the "or 1" half was a real bug caught by the CLDR sample set: it put 11, 21, 31, ... in
        // "other" instead of "many".
        if ((lastDigit is 0 or 1 && lastTwoDigits is not (>= 12 and <= 14))
            || lastDigit is >= 5 and <= 9
            || lastTwoDigits is >= 12 and <= 14)
            return PluralCategory.Many;

        // Unreachable for any non-negative integer: every last digit is covered by "one" (1), "few"
        // (2-4), or "many" (0, 1, 5-9, or 12-14) above. Kept because .NET's plural rules for a
        // fractional count (v != 0) would land here, and Other has to mean something rather than
        // never being returned.
        return PluralCategory.Other;
    }

    /// <summary>
    /// Ukrainian: one/few/many/other, the same digit-based shape as Polish but with "eleven" carved
    /// out of "one" into "many" (i % 100 = 11 is many, not one, even though i % 10 = 1).
    /// </summary>
    private static PluralCategory Ukrainian(int n)
    {
        var lastDigit = n % 10;
        var lastTwoDigits = n % 100;

        if (lastDigit == 1 && lastTwoDigits != 11)
            return PluralCategory.One;

        if (lastDigit is >= 2 and <= 4 && lastTwoDigits is not (>= 12 and <= 14))
            return PluralCategory.Few;

        if (lastDigit == 0 || lastDigit is >= 5 and <= 9 || lastTwoDigits is >= 11 and <= 14)
            return PluralCategory.Many;

        return PluralCategory.Other;
    }
}
