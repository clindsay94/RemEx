using System.Globalization;
using Remex.Core.Models;
using Remex.Core.Validation;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins the name a pushed screenshot arrives under (RemEx-zvtr).
/// </summary>
/// <remarks>
/// The name lands in a gallery the user browses months later, so it has to sort chronologically as
/// TEXT and be writable on both hosts. Both of those are easy to get wrong in ways that only show up
/// on someone else's machine.
/// </remarks>
public class ScreenshotFileNameTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 2, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void TheNameIsPredictableAndCarriesTheTime()
    {
        Assert.Equal("RemEx_2026-08-02_12-34-56.png", ScreenshotFileName.ForTimestamp(Noon));
    }

    [Fact]
    public void ThereIsNoColonAnywhere()
    {
        // ISO-8601's TIME SEPARATOR IS A COLON, WHICH IS ILLEGAL IN A WINDOWS FILENAME. Reaching
        // for ToString("o") or "s" is the obvious move and produces a name the host cannot write.
        // On Linux a colon is legal, so it would work in development and fail on the platform most
        // users are on - the worst shape a bug can have.
        Assert.DoesNotContain(':', ScreenshotFileName.ForTimestamp(Noon));
        Assert.DoesNotContain(':', ScreenshotFileName.ForTimestamp(Noon, "DISPLAY1"));
    }

    [Fact]
    public void TheNameIsAcceptedByTheRepositoryFilenameValidator()
    {
        // The end-to-end check that matters: this name goes through the same file_push_offer flow
        // as any other transfer, so it has to satisfy the validator that flow uses.
        Assert.True(FilePathValidation.IsValidFileName(ScreenshotFileName.ForTimestamp(Noon), out _));
        Assert.True(FilePathValidation.IsValidFileName(
            ScreenshotFileName.ForTimestamp(Noon, @"\\.\DISPLAY1"), out _));
    }

    [Fact]
    public void NamesSortChronologicallyAsText()
    {
        // Galleries sort by name as often as by date, and a name that sorts wrongly makes a folder
        // of screenshots unusable exactly when there are enough of them to matter.
        var earlier = ScreenshotFileName.ForTimestamp(new DateTimeOffset(2026, 8, 2, 9, 5, 3, TimeSpan.Zero));
        var later = ScreenshotFileName.ForTimestamp(new DateTimeOffset(2026, 8, 2, 12, 34, 56, TimeSpan.Zero));
        var nextDay = ScreenshotFileName.ForTimestamp(new DateTimeOffset(2026, 8, 3, 1, 0, 0, TimeSpan.Zero));

        Assert.True(string.CompareOrdinal(earlier, later) < 0, $"{earlier} should sort before {later}");
        Assert.True(string.CompareOrdinal(later, nextDay) < 0, $"{later} should sort before {nextDay}");
    }

    [Fact]
    public void SingleDigitPartsAreZeroPaddedSoTheSortHolds()
    {
        // The failure mode of an unpadded format: "9-05-03" sorts after "12-34-56" as text, so a
        // morning screenshot appears at the end of the day.
        Assert.Equal("RemEx_2026-08-02_09-05-03.png",
            ScreenshotFileName.ForTimestamp(new DateTimeOffset(2026, 8, 2, 9, 5, 3, TimeSpan.Zero)));
    }

    [Fact]
    public void TheFormatDoesNotChangeWithTheUsersCulture()
    {
        // LOAD-BEARING, NOT TIDY. A culture-sensitive format under ar-SA renders Arabic-Indic
        // digits, and several locales substitute a non-Gregorian calendar - producing a name that
        // neither sorts with its siblings nor matches what the rest of the app expects, on the
        // machines of exactly the users least able to report it clearly.
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "ar-SA", "th-TH", "fa-IR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);

                Assert.Equal("RemEx_2026-08-02_12-34-56.png", ScreenshotFileName.ForTimestamp(Noon));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void TwoDisplaysInTheSameSecondGetDifferentNames()
    {
        // A multi-monitor host can push two shots within one second, and without the label they
        // would collide - which the conflict-naming path would then have to resolve, silently
        // renaming one to "(2)" and losing which screen it came from.
        var primary = ScreenshotFileName.ForTimestamp(Noon, "DISPLAY1");
        var secondary = ScreenshotFileName.ForTimestamp(Noon, "DISPLAY2");

        Assert.NotEqual(primary, secondary);
        Assert.Contains("DISPLAY1", primary);
    }

    [Fact]
    public void AMonitorNameWithPathCharactersCannotSmuggleThemIntoTheFileName()
    {
        // A display name comes from the OS and can be anything - \\.\DISPLAY1 on Windows, or a
        // manufacturer string full of punctuation. Passing it through would put a path separator
        // inside a file name, which is the traversal shape FilePathValidation refuses.
        var name = ScreenshotFileName.ForTimestamp(Noon, @"\\.\DISPLAY1");

        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain('/', name);
        Assert.Contains("DISPLAY1", name);
    }

    [Fact]
    public void ALabelThatSurvivesNothingIsDroppedRatherThanLeavingAStraySeparator()
    {
        // "***" sanitizes to empty. Appending it anyway would produce "RemEx_..._.png", which looks
        // like a truncation bug to anyone who sees it.
        Assert.Equal(ScreenshotFileName.ForTimestamp(Noon), ScreenshotFileName.ForTimestamp(Noon, "***"));
        Assert.Equal(ScreenshotFileName.ForTimestamp(Noon), ScreenshotFileName.ForTimestamp(Noon, "   "));
        Assert.Equal(ScreenshotFileName.ForTimestamp(Noon), ScreenshotFileName.ForTimestamp(Noon, null));
    }

    [Fact]
    public void AVerboseMonitorNameCannotDominateTheFileName()
    {
        var name = ScreenshotFileName.ForTimestamp(Noon, new string('m', 200));

        Assert.True(name.Length < 60, $"name was {name.Length} characters: {name}");
    }
}
