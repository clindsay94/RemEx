using System.Globalization;
using System.Text;

namespace Remex.Desktop.Services;

/// <summary>
/// Decides whether a settings row matches what the user typed (RemEx-2y8s).
/// </summary>
/// <remarks>
/// <para>
/// Settings is a single eight-section scroll with no filter, and the two most-asked-for settings —
/// language and theme — are not even in it. Search is the cheapest fix, but it has to work in all
/// eight shipped languages, which is what makes the matching non-trivial.
/// </para>
/// <para>
/// SEARCHING IS THE OPPOSITE PROBLEM TO NAMING A FILE. `FileConflictNaming` compares ordinally,
/// because there the comparison models a FILESYSTEM. Here it models a PERSON, who is typing quickly,
/// on whatever keyboard they have, and who should not have to produce an accent to find a row that
/// carries one. So this folds diacritics and case, and that leniency is the feature.
/// </para>
/// </remarks>
public static class SettingsSearchMatcher
{
    /// <summary>
    /// Whether <paramref name="query"/> matches any of a row's searchable text.
    /// </summary>
    /// <param name="query">Raw text from the search box.</param>
    /// <param name="haystacks">
    /// The row's localized label, its description, and any keyword synonyms. Nulls are skipped, so a
    /// caller can pass an optional description without pre-filtering.
    /// </param>
    /// <remarks>
    /// EVERY TOKEN MUST MATCH SOMETHING, but they may match DIFFERENT fields. "dark theme" should
    /// find a row labelled "Theme" whose description mentions dark, and requiring one field to
    /// contain the whole phrase would miss it. Requiring only SOME token to match would instead
    /// return half of Settings for a two-word query, so neither extreme works.
    /// </remarks>
    public static bool Matches(string? query, params string?[] haystacks)
    {
        var tokens = Tokenize(query);

        // An empty query shows EVERYTHING, not nothing. A search box that blanks the page before a
        // character is typed reads as broken, and the user cannot tell an empty query from no
        // results.
        if (tokens.Count == 0) return true;

        var folded = new List<string>(haystacks.Length);
        foreach (var haystack in haystacks)
        {
            if (!string.IsNullOrWhiteSpace(haystack)) folded.Add(Fold(haystack));
        }

        if (folded.Count == 0) return false;

        foreach (var token in tokens)
        {
            var found = false;
            foreach (var haystack in folded)
            {
                if (haystack.Contains(token, StringComparison.Ordinal)) { found = true; break; }
            }

            if (!found) return false;
        }

        return true;
    }

    /// <summary>
    /// Splits a query into the words that all have to match.
    /// </summary>
    private static List<string> Tokenize(string? query)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return tokens;

        foreach (var raw in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var folded = Fold(raw);
            if (folded.Length > 0) tokens.Add(folded);
        }

        return tokens;
    }

    /// <summary>
    /// Reduces text to a form a hurried user can reproduce: lower case, no diacritics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DIACRITIC FOLDING IS THE POINT, not a nicety. This app ships in French, Polish, Turkish and
    /// Ukrainian; a French user looking for "Énergie" will type <c>energie</c>, and a Polish user
    /// looking for "Połączenie" will type <c>polaczenie</c>, because producing the accented letter
    /// is slower than typing the word. Decomposing to <see cref="NormalizationForm.FormD"/> and
    /// dropping the combining marks makes both work.
    /// </para>
    /// <para>
    /// LOWERCASING IS INVARIANT, NOT CULTURE-AWARE, and that is deliberate despite this being
    /// user-facing text. Turkish casing maps <c>I</c> to dotless <c>ı</c>, so under <c>tr-TR</c> a
    /// culture-aware fold would stop <c>I</c> from matching <c>i</c> — meaning an English or French
    /// label containing "I" becomes unfindable for a user whose UI language is Turkish, which is a
    /// far more common situation than it sounds, because product names and acronyms stay Latin in
    /// every translation.
    /// </para>
    /// <para>
    /// DECOMPOSITION ALONE IS NOT ENOUGH, AND A TEST IS WHAT PROVED IT. Two of the shipped
    /// languages use letters that Unicode does not decompose, because they are not decorated Latin
    /// letters at all: Polish <c>ł</c> is a distinct letter with a stroke rather than <c>l</c> plus
    /// a combining mark, and Turkish <c>ı</c> is its own letter rather than <c>i</c> with the dot
    /// removed. <see cref="NormalizationForm.FormD"/> leaves both untouched, so
    /// <c>polaczenie</c> did not find "Połączenie" and <c>baglanti</c> did not find "Bağlantı"
    /// until <see cref="NonDecomposable"/> mapped them explicitly.
    /// </para>
    /// <para>
    /// Mapping <c>ı</c> to <c>i</c> makes the two Turkish i-letters interchangeable FOR SEARCH, and
    /// that is the correct trade here even though it would be wrong almost anywhere else: the cost
    /// is a handful of extra rows, and the cost of the alternative is a setting the user cannot
    /// find. Compare <c>FileConflictNaming</c>, where conflating them would overwrite a file.
    /// </para>
    /// </remarks>
    internal static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            // Combining marks are the accents themselves once decomposed; dropping them is what
            // turns "é" into "e". Anything else keeps its own identity - notably Cyrillic and
            // Turkish dotless i, which are letters rather than decorated Latin ones.
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;

            var lower = char.ToLowerInvariant(ch);
            builder.Append(NonDecomposable.TryGetValue(lower, out var mapped) ? mapped : lower);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Letters that carry no combining mark to strip, so decomposition cannot reach them.
    /// </summary>
    /// <remarks>
    /// Deliberately short and justified by the SHIPPED languages rather than copied from a general
    /// transliteration table: every entry here is a decision that two letters are interchangeable
    /// when searching, and a long list nobody can audit is how one of those decisions turns out to
    /// be wrong in a language somebody actually reads.
    /// </remarks>
    private static readonly Dictionary<char, char> NonDecomposable = new()
    {
        ['ł'] = 'l',  // Polish
        ['ı'] = 'i',  // Turkish dotless i
        ['đ'] = 'd',  // appears in loanwords and place names
        ['ø'] = 'o'
    };
}
