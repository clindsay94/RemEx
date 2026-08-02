using Remex.Agent.Services.FileTransfer;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins which surface is asked for file-transfer consent (RemEx-mneb).
/// </summary>
/// <remarks>
/// The consent UX is backwards today: the PC pops a dialog on the PC monitor for a decision the
/// PHONE user is making, possibly from another room, so the prompt waits in front of nobody until it
/// times out. The person being asked should be the person who asked.
/// </remarks>
public class ConsentRoutePolicyTests
{
    [Fact]
    public void AConnectedPhoneThatCanPromptIsAskedItself()
    {
        // The whole point of the bead.
        Assert.Equal(ConsentRoute.Phone,
            ConsentRoutePolicy.Route(requestingClientConnected: true, clientSupportsPhonePrompt: true));
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
        Assert.Equal(ConsentRoute.Desktop,
            ConsentRoutePolicy.Route(requestingClientConnected: true, clientSupportsPhonePrompt: false));
    }

    [Fact]
    public void ARequestWhoseAskerIsGoneIsDeniedRatherThanShownOnThePc()
    {
        // Deny, NOT a desktop prompt. The asker is gone, so a PC dialog would ask about a transfer
        // that can no longer happen - and a user answering "allow" would be granting durable trust
        // to a device that is not there, on the strength of a request whose origin they cannot see.
        Assert.Equal(ConsentRoute.Deny,
            ConsentRoutePolicy.Route(requestingClientConnected: false, clientSupportsPhonePrompt: true));
        Assert.Equal(ConsentRoute.Deny,
            ConsentRoutePolicy.Route(requestingClientConnected: false, clientSupportsPhonePrompt: false));
    }

    [Fact]
    public void NoRouteEverAsksASurfaceTheClientCannotReach()
    {
        // The invariant behind all three rules, swept rather than sampled: Phone is chosen only
        // when the client is both connected AND capable. One wrong branch sends a prompt into the
        // void, and the failure looks like a hung transfer rather than a routing bug.
        foreach (var connected in new[] { true, false })
        {
            foreach (var capable in new[] { true, false })
            {
                var route = ConsentRoutePolicy.Route(connected, capable);

                if (route == ConsentRoute.Phone)
                {
                    Assert.True(connected && capable,
                        $"routed to Phone with connected={connected}, capable={capable}");
                }
            }
        }
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
