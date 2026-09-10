using Remex.Agent.Services.FileTransfer;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins when a reply authorises this PC to push somebody's files (RemEx-y7my).
/// </summary>
/// <remarks>
/// This runs on the SENDING side, so a wrong "yes" means transmitting a file the person holding the
/// phone did not agree to receive. Every test here is about refusing.
/// </remarks>
public class FilePushNegotiationTests
{
    private static FilePushResponse Reply(string pushId, bool accepted, params string[] ids) => new()
    {
        PushId = pushId,
        Accepted = accepted,
        TransferIds = ids.Length == 0 ? null : ids,
    };

    [Fact]
    public void AMatchingAcceptanceWithOneIdPerFileIsHonoured()
    {
        var outcome = FilePushNegotiation.Interpret("push-1", 2, Reply("push-1", true, "a", "b"));

        Assert.True(outcome.Accepted);
        Assert.Equal(["a", "b"], outcome.TransferIds);
        Assert.Null(outcome.RefusedReason);
    }

    [Fact]
    public void NoReplyMeansNoPush()
    {
        // The phone's consent prompt can simply time out, or the socket can drop mid-negotiation.
        var outcome = FilePushNegotiation.Interpret("push-1", 1, null);

        Assert.False(outcome.Accepted);
        Assert.Empty(outcome.TransferIds);
        Assert.Contains("no reply", outcome.RefusedReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADenialIsHonoured()
    {
        var outcome = FilePushNegotiation.Interpret("push-1", 1, Reply("push-1", false));

        Assert.False(outcome.Accepted);
        Assert.Empty(outcome.TransferIds);
    }

    [Fact]
    public void AReplyToADIFFERENTOfferIsNotConsent()
    {
        // Two pushes can be in flight - a screenshot while a share-sheet send is negotiating - and
        // consent to send one thing is not consent to send another. Accepting a mismatched id would
        // let a "yes" for a small text file authorise pushing whatever the other offer contained.
        var outcome = FilePushNegotiation.Interpret("push-1", 1, Reply("push-2", true, "a"));

        Assert.False(outcome.Accepted);
        Assert.Contains("push-2", outcome.RefusedReason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, new[] { "only-one" })]
    [InlineData(1, new[] { "one", "two" })]
    [InlineData(3, new[] { "a", "b" })]
    public void ACOUNTMismatchIsRefused_BecauseIdsMapToFilesByPositionAlone(int files, string[] ids)
    {
        // THE GUARD THIS TYPE EXISTS FOR. The phone mints one id per offered file and nothing in the
        // wire format ties an id to a name - they correspond by index and by nothing else. Pushing
        // anyway would send file N under file M's id, so the receiver files one document's bytes
        // under another's name and BOTH sides report success. A failed transfer is recoverable;
        // silently mixing up two files is not.
        var outcome = FilePushNegotiation.Interpret("push-1", files, Reply("push-1", true, ids));

        Assert.False(outcome.Accepted);
        Assert.Empty(outcome.TransferIds);
        Assert.Contains("transfer ids", outcome.RefusedReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptedWithNOIdsAtAllIsRefused()
    {
        // A malformed accept - the field omitted entirely - must not read as "accepted, zero files".
        var outcome = FilePushNegotiation.Interpret("push-1", 1, Reply("push-1", true));

        Assert.False(outcome.Accepted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankIdIsRefusedEvenWhenTheCountMatches(string blank)
    {
        // The count check alone would pass this. A blank id cannot address a transfer, so the push
        // would begin and then fail somewhere less obvious, after the consent prompt had already been
        // answered - the user would believe the file was on its way.
        var outcome = FilePushNegotiation.Interpret("push-1", 2, Reply("push-1", true, "good", blank));

        Assert.False(outcome.Accepted);
        Assert.Contains("blank", outcome.RefusedReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheOfferCarriesTheNameAndSizeTheReceiverWillShow()
    {
        // The phone's consent prompt is built from these - describePushFiles reads name and size - so
        // they are what the user actually sees before deciding.
        var offer = FilePushNegotiation.TryOfferOne("push-1", "RemEx_2026-08-02_14-05-09.png", 4096);

        Assert.NotNull(offer);
        Assert.Equal("push-1", offer!.PushId);
        var file = Assert.Single(offer.Files);
        Assert.Equal("RemEx_2026-08-02_14-05-09.png", file.Name);
        Assert.Equal(4096, file.Size);
    }

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("sub/dir.png")]
    [InlineData("has\\separator.png")]
    [InlineData("")]
    [InlineData("   ")]
    public void ANameTheRECEIVERCouldNotFileIsNeverOffered(string bad)
    {
        // REVIEW CAUGHT THE FIRST VERSION DOCUMENTING THIS VALIDATION WITHOUT DOING IT. This is the
        // name the receiver files the bytes under AND the name its consent prompt shows, so offering
        // something it must reject wastes the user's decision - they would answer a prompt for a
        // transfer that could never complete. It goes through the same check the download path uses.
        Assert.Null(FilePushNegotiation.TryOfferOne("push-1", bad, 10));
    }

    [Fact]
    public void RefusalNEVERCarriesIds()
    {
        // A caller that read TransferIds without checking Accepted would push on a denial. Making the
        // list empty on every refusal removes the shape of that bug rather than documenting it.
        foreach (var response in new FilePushResponse?[]
                 {
                     null,
                     Reply("push-1", false, "a"),
                     Reply("other", true, "a"),
                     Reply("push-1", true, "a", "b"),
                 })
        {
            var outcome = FilePushNegotiation.Interpret("push-1", 1, response);

            Assert.False(outcome.Accepted);
            Assert.Empty(outcome.TransferIds);
            Assert.False(string.IsNullOrWhiteSpace(outcome.RefusedReason));
        }
    }
}
