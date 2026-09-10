using Remex.Agent.Services.FileTransfer;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins which surface is asked for file-transfer consent (RemEx-mneb, split by kind in RemEx-6bfyt).
/// </summary>
/// <remarks>
/// Two different questions travel through this one policy. Per-push consent is immediate and belongs
/// to the person holding the phone — putting it on the PC monitor left it waiting in front of nobody
/// until it timed out, which is what RemEx-mneb fixed. Full browse is a durable grant over the whole
/// filesystem, and belongs to whoever is sitting at the machine being handed out. Routing both by
/// what the client could RENDER collapsed them into one answer.
/// </remarks>
public class ConsentRoutePolicyTests
{
    [Fact]
    public void AConnectedPhoneThatCanPromptIsAskedItself()
    {
        // The whole point of RemEx-mneb, and it survives the kind split unchanged - per-push is
        // exactly the case the phone route exists for.
        Assert.Equal(ConsentRoute.Phone,
            ConsentRoutePolicy.Route(
                requestingClientConnected: true,
                clientSupportsPhonePrompt: true,
                consentKind: FileConsentKinds.IncomingPush));
    }

    [Fact]
    public void AFullBrowseGrantIsAuthorisedAtThePcEvenWhenThePhoneCouldAskInstead()
    {
        // CONNOR'S CALL, 2026-08-10 (RemEx-6bfyt). Capability is deliberately NOT consulted here:
        // the phone can render this prompt perfectly well, and it still does not get it. Handing out
        // the entire filesystem, durably, is a decision for whoever is at the machine.
        //
        // A version that kept routing on capability passes every other test in this file.
        Assert.Equal(ConsentRoute.Desktop,
            ConsentRoutePolicy.Route(
                requestingClientConnected: true,
                clientSupportsPhonePrompt: true,
                consentKind: FileConsentKinds.FullBrowse));
    }

    [Fact]
    public void ThePcRouteIsNotAppliedToEveryKind()
    {
        // ANTI-OVER-CORRECTION. The obvious wrong fix for the reported bug is to send consent back
        // to the PC wholesale, which silently reinstates the prompt-waiting-for-nobody failure that
        // RemEx-mneb existed to remove. Only full browse moved.
        //
        // BE HONEST ABOUT WHAT THIS CURRENTLY GUARDS. The host raises exactly one kind of consent
        // request today - FileTransferHandler builds only full_browse - so ConsentRoute.Phone is
        // presently unreachable in production and the first assertion below pins a case nothing
        // exercises. That is deliberate, not an oversight: the phone route and its Android sheet
        // (RemEx-220r, RemEx-vyhm) are dormant rather than deleted, and this is what stops a future
        // incoming_push prompt from silently inheriting the PC route when it is wired back up.
        Assert.Equal(ConsentRoute.Phone,
            ConsentRoutePolicy.Route(true, true, FileConsentKinds.IncomingPush));
        Assert.Equal(ConsentRoute.Desktop,
            ConsentRoutePolicy.Route(true, true, FileConsentKinds.FullBrowse));
    }

    [Fact]
    public void AnOlderPhoneFallsBackToThePcDialogRatherThanBeingDenied()
    {
        // THE BACKWARD-COMPATIBILITY REQUIREMENT, not a nicety. A phone built before this feature
        // cannot render the prompt, so routing its request to a surface it does not have would make
        // every transfer fail on an app the user has no reason to suspect - a silent break of a
        // working setup, caused entirely by updating the PC.
        //
        // The PC dialog is worse UX and it is REACHABLE, which beats better UX nobody can answer.
        // Unaffected by the kind split: full browse was already coming here for these clients.
        Assert.Equal(ConsentRoute.Desktop,
            ConsentRoutePolicy.Route(
                requestingClientConnected: true,
                clientSupportsPhonePrompt: false,
                consentKind: FileConsentKinds.IncomingPush));
    }

    [Fact]
    public void ARequestWhoseAskerIsGoneIsDeniedRatherThanShownOnThePc()
    {
        // Deny, NOT a desktop prompt. The asker is gone, so a PC dialog would ask about a transfer
        // that can no longer happen - and a user answering "allow" would be granting durable trust
        // to a device that is not there, on the strength of a request whose origin they cannot see.
        //
        // BOTH KINDS, because full browse now has its own route to the PC: a kind check placed
        // ahead of the connected check would turn this deny into a desktop prompt for exactly the
        // request where durable trust is at stake.
        foreach (var kind in new[] { FileConsentKinds.FullBrowse, FileConsentKinds.IncomingPush })
        {
            Assert.Equal(ConsentRoute.Deny,
                ConsentRoutePolicy.Route(requestingClientConnected: false, clientSupportsPhonePrompt: true, kind));
            Assert.Equal(ConsentRoute.Deny,
                ConsentRoutePolicy.Route(requestingClientConnected: false, clientSupportsPhonePrompt: false, kind));
        }
    }

    [Fact]
    public void NoRouteEverAsksASurfaceTheClientCannotReach()
    {
        // The invariant behind all the rules, swept rather than sampled: Phone is chosen only when
        // the client is connected, capable, AND the question is not a full-browse grant. One wrong
        // branch either sends a prompt into the void or puts the filesystem-wide grant on the phone,
        // and both fail as a hung transfer rather than as a routing bug.
        foreach (var connected in new[] { true, false })
        {
            foreach (var capable in new[] { true, false })
            {
                foreach (var kind in new[] { FileConsentKinds.FullBrowse, FileConsentKinds.IncomingPush })
                {
                    var route = ConsentRoutePolicy.Route(connected, capable, kind);

                    if (route == ConsentRoute.Phone)
                    {
                        Assert.True(connected && capable && kind != FileConsentKinds.FullBrowse,
                            $"routed to Phone with connected={connected}, capable={capable}, kind={kind}");
                    }
                }
            }
        }
    }

    [Fact]
    public void AnUnrecognisedKindKeepsTheOldCapabilityBehaviour()
    {
        // A kind this policy has never heard of must not fall into the full-browse branch by
        // accident. Only the exact full_browse token diverts to the PC; anything else routes as it
        // always did, so adding a kind to FileConsentKinds cannot silently change where it is asked.
        Assert.Equal(ConsentRoute.Phone, ConsentRoutePolicy.Route(true, true, "some_future_kind"));
        Assert.Equal(ConsentRoute.Desktop, ConsentRoutePolicy.Route(true, false, "some_future_kind"));

        // Ordinal, like every other token comparison in this class.
        Assert.Equal(ConsentRoute.Phone, ConsentRoutePolicy.Route(true, true, "FULL_BROWSE"));
    }

    [Fact]
    public void AMatchingResponseIsApplied()
    {
        Assert.True(ConsentRoutePolicy.ShouldApplyResponse("consent-42", "consent-42"));
    }

    [Fact]
    public void ALateAnswerCannotReviveAnAlreadyResolvedRequest()
    {
        // THE SAFETY PROPERTY. If a prompt already timed out to a clean deny and the phone answers
        // afterwards - the user picked it up a minute later and tapped Allow - applying that would
        // grant a transfer the host already refused, and the two sides would disagree about what
        // happened. Once resolved there is no pending id, so a late response is ignored by
        // construction rather than by a race-prone timestamp comparison.
        Assert.False(ConsentRoutePolicy.ShouldApplyResponse("consent-42", pendingConsentId: null));
        Assert.False(ConsentRoutePolicy.ShouldApplyResponse("consent-42", pendingConsentId: ""));
    }

    [Fact]
    public void AnAnswerForADifferentRequestIsIgnored()
    {
        // For a consent decision, applying one request's answer to another means granting access
        // the user approved for something else entirely.
        Assert.False(ConsentRoutePolicy.ShouldApplyResponse("consent-41", "consent-42"));
    }

    [Fact]
    public void ConsentIdsAreComparedOrdinally()
    {
        // Ids are opaque tokens, not words. A case-insensitive match would let two distinct
        // requests be treated as one.
        Assert.False(ConsentRoutePolicy.ShouldApplyResponse("CONSENT-42", "consent-42"));
    }

    [Fact]
    public void AResponseWithNoIdIsIgnored()
    {
        // A malformed or truncated response must not be read as an answer to whatever happens to be
        // pending - that is the shape of a request an attacker would send.
        Assert.False(ConsentRoutePolicy.ShouldApplyResponse(null, "consent-42"));
        Assert.False(ConsentRoutePolicy.ShouldApplyResponse("", "consent-42"));
        Assert.False(ConsentRoutePolicy.ShouldApplyResponse("   ", "consent-42"));
    }
}
