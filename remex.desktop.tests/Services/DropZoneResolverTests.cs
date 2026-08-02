using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins what a dropped file becomes (RemEx-wbhc).
/// </summary>
/// <remarks>
/// The two outcomes are not symmetric. Upload writes into the PC's shared root, where a mistaken
/// file is silently added to a folder the phone can browse; send-to-phone raises a transfer the user
/// watches complete. That asymmetry decides every default here.
/// </remarks>
public class DropZoneResolverTests
{
    private const double Height = 400;

    [Fact]
    public void TheTopHalfSendsToThePhoneAndTheBottomHalfUploads()
    {
        Assert.Equal(FileTransferQueueKind.SendToPhone, DropZoneResolver.ResolveSplit(50, Height));
        Assert.Equal(FileTransferQueueKind.Upload, DropZoneResolver.ResolveSplit(350, Height));
    }

    [Fact]
    public void ADropExactlyOnTheBoundaryResolvesTheSameWayEveryTime()
    {
        // The boundary belongs to the top zone by rule, rather than depending on a floating-point
        // comparison landing either side - which would make the same drop do different things.
        Assert.Equal(FileTransferQueueKind.SendToPhone, DropZoneResolver.ResolveSplit(200, Height));
        Assert.Equal(
            DropZoneResolver.ResolveSplit(200, Height),
            DropZoneResolver.ResolveSplit(200, Height));
    }

    [Fact]
    public void TheSplitFractionMovesTheBoundary()
    {
        // 0.25 puts the send-to-phone zone in the top quarter.
        Assert.Equal(FileTransferQueueKind.SendToPhone, DropZoneResolver.ResolveSplit(90, Height, 0.25));
        Assert.Equal(FileTransferQueueKind.Upload, DropZoneResolver.ResolveSplit(110, Height, 0.25));
    }

    [Fact]
    public void AnOutOfRangeSplitFractionIsClampedRatherThanInverting()
    {
        // THIS TEST HAD TO BE REBUILT BECAUSE THE FIRST VERSION COULD NOT FAIL. It asserted
        // ResolveSplit(399, 400, 5) is SendToPhone and ResolveSplit(1, 400, -5) is Upload - both of
        // which hold whether or not the fraction is clamped, because 399 is below an unclamped
        // boundary of 2000 and 1 is above an unclamped boundary of -2000. Removing the clamp passed
        // all ten tests.
        //
        // The discriminating case is the TOP EDGE with a negative fraction. Clamped, the boundary
        // is 0 and a drop at y=0 is on it, so it belongs to the top zone: SendToPhone. Unclamped,
        // the boundary is -2000 and y=0 sits below it, inverting the surface: Upload. That
        // inversion is the failure worth guarding - every drop would do the opposite of what the
        // zones say.
        Assert.Equal(FileTransferQueueKind.SendToPhone, DropZoneResolver.ResolveSplit(0, Height, -5));

        // And the bottom edge with an oversized fraction: clamped the boundary is the full height,
        // so the last pixel is still SendToPhone rather than the surface silently becoming one
        // Upload zone.
        Assert.Equal(FileTransferQueueKind.SendToPhone, DropZoneResolver.ResolveSplit(Height, Height, 5));
        Assert.Equal(FileTransferQueueKind.Upload, DropZoneResolver.ResolveSplit(Height + 1, Height, 5));
    }

    [Fact]
    public void ADegenerateSurfaceResolvesToTheRecoverableAnswer()
    {
        // THE ASYMMETRY THAT DECIDES THIS. Upload silently adds a file to a folder the phone can
        // browse; send-to-phone raises a transfer the user watches complete. If the geometry is
        // unusable, the visible and recoverable answer is the right default.
        Assert.Equal(FileTransferQueueKind.SendToPhone, DropZoneResolver.ResolveSplit(50, 0));
        Assert.Equal(FileTransferQueueKind.SendToPhone, DropZoneResolver.ResolveSplit(50, -100));
        Assert.Equal(FileTransferQueueKind.SendToPhone, DropZoneResolver.ResolveSplit(double.NaN, Height));
    }

    [Fact]
    public void ASurfaceWithNoChoiceSendsToThePhone()
    {
        // HomeView and the main window accept nothing today. A split there would be a hidden mode:
        // the user has no reason to expect that WHERE they release the mouse changes what happens.
        Assert.Equal(FileTransferQueueKind.SendToPhone, DropZoneResolver.ResolveSingleZone());
    }

    [Fact]
    public void AnEmptyDropEnqueuesNothing()
    {
        // The OS can deliver a drop that carried no file data. That is not an error to report, but
        // it must not enqueue a transfer of nothing, which surfaces as a zero-byte entry the user
        // cannot explain.
        Assert.False(DropZoneResolver.HasAnythingToSend(null));
        Assert.False(DropZoneResolver.HasAnythingToSend([]));
        Assert.False(DropZoneResolver.HasAnythingToSend(["", "   "]));
    }

    [Fact]
    public void ADropWithAtLeastOneRealPathIsWorthEnqueueing()
    {
        Assert.True(DropZoneResolver.HasAnythingToSend([@"C:\reports\q3.pdf"]));
        Assert.True(DropZoneResolver.HasAnythingToSend(["", @"C:\reports\q3.pdf"]));
    }

    [Fact]
    public void EveryPointOnTheSurfaceResolvesToOneOfTheTwoKinds()
    {
        // Swept: a gap would leave a band of the surface doing nothing on drop, which reads as the
        // window having ignored the file rather than as a dead zone.
        for (double y = -10; y <= Height + 10; y += 1)
        {
            var kind = DropZoneResolver.ResolveSplit(y, Height);

            Assert.True(kind is FileTransferQueueKind.SendToPhone or FileTransferQueueKind.Upload,
                $"y={y} resolved to {kind}");
        }
    }

    [Fact]
    public void NoDropEverResolvesToDownload()
    {
        // Download is the phone-to-PC direction and is not something a local file drop can mean.
        // Offering it would enqueue a transfer with no source.
        for (double y = 0; y <= Height; y += 25)
        {
            Assert.NotEqual(FileTransferQueueKind.Download, DropZoneResolver.ResolveSplit(y, Height));
        }

        Assert.NotEqual(FileTransferQueueKind.Download, DropZoneResolver.ResolveSingleZone());
    }
}
