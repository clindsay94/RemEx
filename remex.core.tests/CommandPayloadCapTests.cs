using System.Text;
using System.Text.Json;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;
using Remex.Core.Services.Network;

namespace Remex.Core.Tests;

/// <summary>
/// The 8338 ingress will not allocate for more than a command could plausibly be (RemEx-ga503).
/// </summary>
/// <remarks>
/// <para>
/// **THE CAP GUARDS AN ALLOCATION A STRANGER SIZES.** <c>RemexNetworkListener</c> reads a four-byte
/// length prefix, checks it against the cap and allocates that many bytes — all before
/// <c>IsRequestAuthenticated</c>. The peer presents no certificate — the handshake proves the HOST to
/// it, not the reverse — so at the old 10MB anyone who could reach the port could declare the maximum,
/// send nothing, and hold a large-object allocation until the read timeout, across all 16 default
/// concurrent slots.
/// </para>
/// <para>
/// **SO THE NUMBER NEEDS A REASON, AND A REASON NEEDS A TEST.** A bare constant invites someone to
/// raise it back the first time a payload does not fit, without knowing what is supposed to fit. These
/// pin both ends: small enough that declaring a size and sending nothing costs the host almost
/// nothing, and far larger than anything this ingress can dispatch — which is the eleven shared
/// whole-machine power verbs and nothing else (RemEx-pmb4).
/// </para>
/// </remarks>
public class CommandPayloadCapTests
{
    [Fact]
    public void TheCapIsSmallEnoughToHaveRemovedTheAmplification()
    {
        // ONE BOUND, CARRYING THE EXPLANATION. An earlier version asserted `< 85_000` as well and put
        // the message on that one - which can never fail while this passes, so the assertion that
        // actually fires for anything between 64KB and the LOH threshold had no message at all.
        Assert.True(RemexNetworkListener.MaxPayloadSize <= 64 * 1024,
            $"the cap is {RemexNetworkListener.MaxPayloadSize} bytes. It bounds an allocation made " +
            "BEFORE the peer is authenticated, so its size is chosen by a stranger - the point is " +
            "that declaring a large size and sending nothing now costs the host almost nothing. " +
            "Raising it back toward the old 10MB restores an amplification of about 160MB across " +
            "the default 16 concurrent slots.");
    }

    [Fact]
    public void TheLargestPlausibleCommandFitsWithRoomToSpare()
    {
        // **THE OTHER END, AND THE ONE THAT STOPS THIS BEING TIGHTENED INTO A BUG.** Deliberately far
        // wider than anything this channel can actually dispatch: 8338 serves only the eleven shared
        // whole-machine power verbs, and the largest of those is WAKEONLAN's MAC, broadcast address,
        // port and delay - under about 200 bytes. LAUNCHAPP is withheld from this ingress on purpose
        // (RemEx-pmb4), so a launch-path-shaped payload is an over-generous upper bound rather than a
        // real case, and it is used precisely because it is over-generous.
        var request = new CommandRequest(
            "LAUNCHAPP",
            new Dictionary<string, string>
            {
                ["path"] = new string('x', 4096),
                ["args"] = new string('y', 4096),
            },
            ClientId: new string('c', 64));

        var json = JsonSerializer.Serialize(request, RemexJsonSerializerContext.Default.CommandRequest);
        var bytes = Encoding.UTF8.GetByteCount(json);

        Assert.True(bytes < RemexNetworkListener.MaxPayloadSize,
            $"a realistic worst-case command is {bytes} bytes and the cap is " +
            $"{RemexNetworkListener.MaxPayloadSize} - the cap has been tightened past what this " +
            "channel legitimately carries");

        // Margin, not just fit. A cap that only just accommodates the worst case is one that a
        // slightly longer path breaks, and the failure would present as a command that silently does
        // nothing rather than as anything pointing here.
        Assert.True(bytes * 4 < RemexNetworkListener.MaxPayloadSize,
            $"only {RemexNetworkListener.MaxPayloadSize / (double)bytes:F1}x headroom over the " +
            "worst case - too tight to absorb a field being added");
    }

    [Fact]
    public void ACommandWithNoArgumentsIsTinyComparedToTheCap()
    {
        // Anti-vacuity for the pair above: the shared verbs are whole-machine power actions with no
        // parameters at all, so if this were not tiny the serializer would be doing something other
        // than what these tests assume, and the margin assertion would be measuring the wrong thing.
        var json = JsonSerializer.Serialize(
            new CommandRequest("SHUTDOWN", null), RemexJsonSerializerContext.Default.CommandRequest);

        Assert.True(Encoding.UTF8.GetByteCount(json) < 256);
    }
}
