using System.Text.RegularExpressions;
using Remex.Desktop.Services.FileTransfer;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The PC classifies throughput and time-remaining the way the phone already does (RemEx-kh6p4).
/// </summary>
/// <remarks>
/// Ported as a shape from <c>remex.android</c>'s <c>TransferProgressFormat.kt</c>, on RemEx-4lcq's
/// own instruction to reuse those decisions rather than re-derive them "because none of them was
/// obvious". These assertions are the decisions, not the implementation.
/// </remarks>
public class TransferProgressFormatTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    public void AnUnusableRateIsUnknownRatherThanRendered(double? bytesPerSecond)
    {
        // NOT FINITE MEANS NOT KNOWN. This is fed from a division, and "NaN MB/s" would be a defect
        // visible in the one place the user is already anxious. Negative is equally meaningless and
        // equally reachable from a clock that went backwards.
        Assert.IsType<TransferRate.Unknown>(TransferProgressFormat.Rate(bytesPerSecond));
    }

    [Theory]
    [InlineData(512.0, TransferRateUnit.BytesPerSecond, 512.0)]
    [InlineData(1024.0, TransferRateUnit.KilobytesPerSecond, 1.0)]
    [InlineData(1536.0, TransferRateUnit.KilobytesPerSecond, 1.5)]
    [InlineData(1048576.0, TransferRateUnit.MegabytesPerSecond, 1.0)]
    [InlineData(1073741824.0, TransferRateUnit.GigabytesPerSecond, 1.0)]
    public void EachUnitTakesOverAtItsOwnBoundary(double bytes, TransferRateUnit unit, double amount)
    {
        // The boundaries are inclusive on the way UP, so exactly 1024 B/s reads as 1 KB/s rather than
        // as 1024 B/s. Asserting the exact boundary is what stops a later >= drifting to >.
        var rate = Assert.IsType<TransferRate.Known>(TransferProgressFormat.Rate(bytes));

        Assert.Equal(unit, rate.Unit);
        Assert.Equal(amount, rate.Amount, precision: 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(-1.0)]
    public void AnUnusableEtaIsUnknown(double? seconds) =>
        Assert.IsType<TransferEta.Unknown>(TransferProgressFormat.Eta(seconds));

    [Fact]
    public void UnderASecondSaysFinishingRatherThanAnyNumber()
    {
        // "0 seconds left" reads as finished and "1 second" rounds something smaller up. Neither is
        // true, so the state says what is.
        Assert.IsType<TransferEta.Finishing>(TransferProgressFormat.Eta(0.4));
        Assert.IsType<TransferEta.Finishing>(TransferProgressFormat.Eta(0.999));
    }

    [Fact]
    public void AnAbsurdEtaIsRefusedRatherThanShown()
    {
        // A number that large is fiction the user would nevertheless plan around. The cap is 23 hours
        // rather than 24 so the top bucket is never a quantity nobody writes, and so the refusal
        // boundary is not also a displayed value.
        Assert.IsType<TransferEta.Unknown>(
            TransferProgressFormat.Eta(TransferProgressFormat.MaximumMeaningfulSeconds + 1));

        var atCap = Assert.IsType<TransferEta.Remaining>(
            TransferProgressFormat.Eta(TransferProgressFormat.MaximumMeaningfulSeconds));

        Assert.Equal(TransferEtaUnit.Hours, atCap.Unit);
        Assert.Equal(23, atCap.Amount);
    }

    [Fact]
    public void RoundingHappensBEFOREBucketing()
    {
        // THE WHOLE SUBTLETY, and the case that proves the order. 59.6s is under a minute before
        // rounding and over one after it: bucketing first renders "60 seconds left", a unit nobody
        // writes. The correct answer is one minute.
        var eta = Assert.IsType<TransferEta.Remaining>(TransferProgressFormat.Eta(59.6));

        Assert.Equal(TransferEtaUnit.Minutes, eta.Unit);
        Assert.Equal(1, eta.Amount);
    }

    [Theory]
    [InlineData(1.0, TransferEtaUnit.Seconds, 1)]
    [InlineData(59.0, TransferEtaUnit.Seconds, 59)]
    [InlineData(60.0, TransferEtaUnit.Minutes, 1)]
    [InlineData(61.0, TransferEtaUnit.Minutes, 2)]
    [InlineData(3600.0, TransferEtaUnit.Hours, 1)]
    [InlineData(3601.0, TransferEtaUnit.Hours, 2)]
    public void TheAmountIsAlwaysRoundedUp(double seconds, TransferEtaUnit unit, int amount)
    {
        // Rounding up rather than to nearest keeps the figure from ever claiming less time than
        // remains - so the display never counts to zero and then sits there, which reads as a hang.
        var eta = Assert.IsType<TransferEta.Remaining>(TransferProgressFormat.Eta(seconds));

        Assert.Equal(unit, eta.Unit);
        Assert.Equal(amount, eta.Amount);
    }

    [Fact]
    public void TheTwoPlatformsAgreeOnTheCap()
    {
        // THE PARITY THAT MATTERS, because this is a PORT and a port drifts silently. If the phone
        // raises its cap and the PC does not, the same transfer reports a time on one device and
        // nothing on the other, and neither side is obviously wrong to whoever is looking.
        //
        // Reads the Kotlin as text, which is the only option across languages - the same approach
        // RemEx-jc4q used from the other direction.
        var kotlin = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "remex.android", "app", "src", "main", "java", "com", "clindsay94", "remex",
            "service", "TransferProgressFormat.kt"));

        var declared = Regex.Match(kotlin, @"MaximumMeaningfulSeconds:\s*Double\s*=\s*([0-9.]+)\s*\*\s*([0-9.]+)\s*\*\s*([0-9.]+)");
        Assert.True(declared.Success, "the Kotlin cap moved or changed shape; re-check the parity by hand");

        var kotlinSeconds =
            double.Parse(declared.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            * double.Parse(declared.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)
            * double.Parse(declared.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);

        // The constant goes first because it is the compile-time one; the analyzer enforces that, and
        // the direction does not change what is being compared.
        Assert.Equal(TransferProgressFormat.MaximumMeaningfulSeconds, kotlinSeconds);
    }
}
