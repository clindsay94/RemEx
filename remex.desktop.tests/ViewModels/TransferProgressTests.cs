using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Pins the one place bytes become a percentage (RemEx-oiah).
/// </summary>
/// <remarks>
/// Every producer used to compute its own ratio and report only that, so the byte counts were
/// discarded at the boundary — lossy in a way nothing downstream can undo, since speed is bytes over
/// time and time-remaining is remaining-bytes over speed. It was also a correctness problem on its
/// own: with each producer converting, a producer that converted differently would be invisible.
/// </remarks>
public class TransferProgressTests
{
    [Fact]
    public void TheFractionIsDerivedFromTheCounts()
    {
        Assert.Equal(0.25, new TransferProgress(25, 100).Fraction, 6);
        Assert.Equal(0.0, new TransferProgress(0, 100).Fraction, 6);
        Assert.Equal(1.0, new TransferProgress(100, 100).Fraction, 6);
    }

    [Fact]
    public void AnUnknownTotalIsZero_NotAGuess()
    {
        // A streamed source has no length. Inventing a fraction would drive a progress bar that means
        // nothing; the caller shows an indeterminate state instead, which is what "we do not know how
        // far along this is" actually looks like.
        Assert.Equal(0.0, new TransferProgress(500, null).Fraction, 6);
        Assert.Equal(0.0, new TransferProgress(500, 0).Fraction, 6);
        Assert.Equal(0.0, new TransferProgress(500, -1).Fraction, 6);
    }

    [Fact]
    public void TheFractionIsClamped()
    {
        // A resumed transfer can report more bytes than the total it was told about. A bar past 100%
        // is a rendering artefact the user reads as a bug in the transfer.
        Assert.Equal(1.0, new TransferProgress(150, 100).Fraction, 6);
        Assert.Equal(0.0, new TransferProgress(-5, 100).Fraction, 6);
    }

    [Fact]
    public void TheCountsSurviveTheTrip_WhichIsTheWholePoint()
    {
        // The percentage is now DERIVED, so the bytes are still there for anything that needs them.
        // Speed and time-remaining are recoverable from these two numbers and a clock; from the
        // fraction alone, neither is.
        var p = new TransferProgress(1_048_576, 10_485_760);

        Assert.Equal(1_048_576, p.BytesTransferred);
        Assert.Equal(10_485_760, p.TotalBytes);
        Assert.Equal(0.1, p.Fraction, 6);
    }
}
