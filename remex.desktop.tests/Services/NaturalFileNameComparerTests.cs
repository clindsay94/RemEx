using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins the file-list ordering (RemEx-93bb).
/// </summary>
/// <remarks>
/// Every OS file browser sorts naturally, so a sortable column header that does not read as broken
/// rather than as different. The failure is not cosmetic: a user scanning a numbered sequence for a
/// gap sees a list that appears to skip and repeat.
/// </remarks>
public class NaturalFileNameComparerTests
{
    private static List<string> Sorted(params string[] names)
    {
        var list = new List<string>(names);
        list.Sort(NaturalFileNameComparer.Instance);
        return list;
    }

    [Fact]
    public void NumbersOrderNumericallyRatherThanLexicographically()
    {
        // THE WHOLE POINT. A plain string comparison puts file10 before file9, because it compares
        // '1' against '9' and stops.
        Assert.Equal(
            new[] { "file2.txt", "file9.txt", "file10.txt", "file100.txt" },
            Sorted("file100.txt", "file10.txt", "file2.txt", "file9.txt"));
    }

    [Fact]
    public void LeadingZerosDoNotChangeAFilesPlace()
    {
        // "007" and "7" are the same number. Treating them as different puts a zero-padded sequence
        // in the wrong place relative to an unpadded one - which is exactly what happens when a
        // camera and a screenshot tool write into the same folder.
        var sorted = Sorted("shot9.png", "shot010.png", "shot0008.png");

        Assert.Equal(new[] { "shot0008.png", "shot9.png", "shot010.png" }, sorted);
    }

    [Fact]
    public void AVeryLongDigitRunDoesNotOverflowOrThrow()
    {
        // THE TRAP IN THE OBVIOUS IMPLEMENTATION. int.Parse or long.Parse on the digit run throws
        // or silently overflows the moment a file is named with a 20-digit id - a nanosecond
        // timestamp, a hash, a database key. Comparing significant-digit count has no upper bound.
        var small = "id-99999999999999999999999999.dat";   // 26 digits
        var large = "id-99999999999999999999999999999.dat"; // 29 digits

        Assert.True(NaturalFileNameComparer.Instance.Compare(small, large) < 0);
        Assert.Equal(new[] { small, large }, Sorted(large, small));
    }

    [Fact]
    public void EqualLengthDigitRunsCompareByValue()
    {
        Assert.Equal(
            new[] { "v1.2.3", "v1.2.10", "v1.10.0" },
            Sorted("v1.10.0", "v1.2.10", "v1.2.3"));
    }

    [Fact]
    public void CaseDoesNotDecideThePrimaryOrder()
    {
        // A file browser does not put every capitalised name in its own block before the lowercase
        // ones - that is what an ordinal sort does and it looks like two separate lists.
        Assert.Equal(
            new[] { "apple.txt", "Banana.txt", "cherry.txt" },
            Sorted("cherry.txt", "apple.txt", "Banana.txt"));
    }

    [Fact]
    public void NamesEqualUnderDisplayRulesStillHaveAStableOrder()
    {
        // Without an ordinal tiebreak, "File.txt" and "file.txt" compare equal and a sort may
        // interleave them differently on every refresh - a list that reshuffles under the user
        // while nothing has changed. The tiebreak is arbitrary; being STABLE is what matters.
        var first = Sorted("file.txt", "File.txt");
        var second = Sorted("File.txt", "file.txt");

        Assert.Equal(first, second);
        Assert.Equal(0 == 0, first.SequenceEqual(second));
    }

    [Fact]
    public void APrefixSortsBeforeTheLongerName()
    {
        Assert.Equal(new[] { "report", "report.txt", "reports" }, Sorted("reports", "report.txt", "report"));
    }

    [Fact]
    public void NullsSortFirstAndDoNotThrow()
    {
        // A grid can hand a comparer a null cell while a row is still loading, and a sort that
        // throws takes the whole view down rather than misplacing one row.
        Assert.True(NaturalFileNameComparer.Instance.Compare(null, "a") < 0);
        Assert.True(NaturalFileNameComparer.Instance.Compare("a", null) > 0);
        Assert.Equal(0, NaturalFileNameComparer.Instance.Compare(null, null));
    }

    [Fact]
    public void TheComparerIsSelfConsistentAcrossAWholeRealisticList()
    {
        // A comparer that is not a total order makes List.Sort throw "IComparer.Compare() method
        // returns inconsistent results" - intermittently, and only on some inputs. Sorting a mixed
        // list is the cheapest way to catch that.
        var names = new List<string>
        {
            "file10.txt", "File2.txt", "file2.txt", "", "a", "10", "2", "007", "7",
            "img_20260802_120000.png", "img_20260802_120001.png", "zzz", "Éclair", "éclair"
        };

        var exception = Record.Exception(() => names.Sort(NaturalFileNameComparer.Instance));

        Assert.Null(exception);
    }
}
