using System.Globalization;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins settings search across the eight shipped languages (RemEx-2y8s).
/// </summary>
/// <remarks>
/// The failure that matters is a row the user cannot find. Search that is too strict looks like the
/// setting does not exist, which sends them to support for something that was on screen all along.
/// </remarks>
public class SettingsSearchMatcherTests
{
    [Fact]
    public void AnEmptyQueryShowsEverything()
    {
        // A search box that blanks the page before a character is typed reads as broken, and the
        // user cannot tell an empty query from no results.
        Assert.True(SettingsSearchMatcher.Matches(null, "Connection"));
        Assert.True(SettingsSearchMatcher.Matches("", "Connection"));
        Assert.True(SettingsSearchMatcher.Matches("   ", "Connection"));
    }

    [Fact]
    public void APlainSubstringMatches()
    {
        Assert.True(SettingsSearchMatcher.Matches("conn", "Connection"));
        Assert.False(SettingsSearchMatcher.Matches("printer", "Connection"));
    }

    [Fact]
    public void CaseDoesNotMatter()
    {
        Assert.True(SettingsSearchMatcher.Matches("CONNECTION", "connection"));
        Assert.True(SettingsSearchMatcher.Matches("connection", "CONNECTION"));
    }

    [Theory]
    [InlineData("energie", "Énergie")]          // French
    [InlineData("polaczenie", "Połączenie")]    // Polish
    [InlineData("baglanti", "Bağlantı")]        // Turkish
    [InlineData("recuperacao", "Recuperação")]  // Portuguese
    [InlineData("conexion", "Conexión")]        // Spanish
    public void ADiacriticDoesNotHaveToBeTyped(string typed, string label)
    {
        // THE FEATURE, not a nicety. This app ships in eight languages, and producing an accented
        // letter is slower than typing the word - so a user WILL type the unaccented form. A search
        // that refuses it makes the setting look absent.
        Assert.True(SettingsSearchMatcher.Matches(typed, label));
    }

    [Fact]
    public void EveryWordMustMatchSomething_ButNotNecessarilyTheSameField()
    {
        // "dark theme" should find a row labelled Theme whose description mentions dark. Requiring
        // one field to contain the whole phrase would miss it; requiring only SOME word to match
        // would return half of Settings for a two-word query.
        Assert.True(SettingsSearchMatcher.Matches("dark theme", "Theme", "Choose a light or dark appearance"));
        Assert.False(SettingsSearchMatcher.Matches("dark printer", "Theme", "Choose a light or dark appearance"));
    }

    [Fact]
    public void KeywordSynonymsAreSearchableWithoutBeingDisplayed()
    {
        // The row says "Appearance"; the user searches "colour". Synonyms are why search finds
        // settings whose official label is not the word anyone reaches for.
        Assert.True(SettingsSearchMatcher.Matches("colour", "Appearance", null, "colour color palette theme"));
    }

    [Fact]
    public void ANullOrBlankHaystackIsSkippedRatherThanCountedAsAMatch()
    {
        // Callers pass an optional description without pre-filtering, so nulls arrive routinely. A
        // null must not become an empty string that every query trivially contains.
        Assert.False(SettingsSearchMatcher.Matches("anything", null, null));
        Assert.False(SettingsSearchMatcher.Matches("anything", "", "   "));
    }

    [Fact]
    public void TurkishCasingDoesNotHideLatinLabelsFromTurkishUsers()
    {
        // THE TRAP, and it is the OPPOSITE of the one in FileConflictNaming. There, ordinal
        // comparison is required because the comparison models a FILESYSTEM. Here it models a
        // PERSON - but the fold is still INVARIANT, because Turkish casing maps ASCII "I" to
        // dotless "ı". A culture-aware fold under tr-TR would stop "I" matching "i", so any label
        // containing a Latin I - product names and acronyms stay Latin in every translation -
        // becomes unfindable for a user whose UI language is Turkish.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            Assert.True(SettingsSearchMatcher.Matches("ip", "IP address"));
            Assert.True(SettingsSearchMatcher.Matches("IP", "ip address"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void LettersUnicodeCannotDecomposeAreFoldedExplicitly()
    {
        // THE FINDING THIS TEST PRODUCED, and it is why the diacritic cases above are a Theory over
        // real languages rather than one French example. Polish "ł" is a distinct letter with a
        // stroke - not "l" plus a combining mark - and Turkish "ı" is its own letter rather than
        // "i" with the dot removed, so FormD leaves both untouched and decomposition alone silently
        // failed for two of the eight shipped languages.
        Assert.Equal("l", SettingsSearchMatcher.Fold("ł"));
        Assert.Equal("i", SettingsSearchMatcher.Fold("ı"));
        Assert.Equal("i", SettingsSearchMatcher.Fold("İ"));

        // Mapping the two Turkish i-letters together makes them interchangeable FOR SEARCH, which
        // is the right trade here and would be wrong almost anywhere else: the cost is a few extra
        // rows, and the alternative cost is a setting the user cannot find. In FileConflictNaming
        // the same conflation would overwrite a file.
        Assert.True(SettingsSearchMatcher.Matches("baglanti", "BAĞLANTI"));
    }

    [Fact]
    public void CyrillicIsFoldedByCaseButNotMangled()
    {
        // Ukrainian ships. Its letters are not decorated Latin ones, so nothing may be stripped -
        // only lowercased.
        Assert.True(SettingsSearchMatcher.Matches("з'єднання", "З'єднання"));
        Assert.Equal("енергія", SettingsSearchMatcher.Fold("Енергія"));
    }

    [Fact]
    public void ExtraWhitespaceBetweenWordsIsIgnored()
    {
        Assert.True(SettingsSearchMatcher.Matches("  dark    theme  ", "Theme", "light or dark"));
    }
}
