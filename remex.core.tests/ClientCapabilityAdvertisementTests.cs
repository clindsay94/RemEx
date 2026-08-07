using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Native;
using Remex.Core.Serialization;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins that this client tells the host it can render a consent prompt (RemEx-vyhm).
/// </summary>
/// <remarks>
/// <para>
/// The host routes a file-consent question to the phone ONLY for a client whose
/// <c>clientCapabilities.supportsConsentPrompt</c> it has seen, and falls back to the PC dialog
/// otherwise (<c>ConsentRoutePolicy.Route</c>). That fallback is deliberately silent — it is the
/// compatibility path for every phone built before the sheet — so a client that stops advertising the
/// flag does not fail, it just quietly goes back to prompting on a monitor in another room. Nothing
/// would catch that but this test.
/// </para>
/// <para>
/// The send paths need a live socket, which is why the stamping is a separate pure function.
/// </para>
/// </remarks>
public class ClientCapabilityAdvertisementTests
{
    [Fact]
    public void EveryOutboundMessageAdvertisesTheConsentPrompt()
    {
        var stamped = RemexNativeClient.StampOutbound(
            new RemexMessage { Type = MessageTypes.Ping }, "phone-1");

        Assert.True(stamped.ClientCapabilities?.SupportsConsentPrompt);
    }

    [Fact]
    public void StampingIsAppliedToEveryTypeRatherThanOneHandshakeMessage()
    {
        // The host keeps the flag in a per-SESSION registry entry, so a single announcement at connect
        // is lost by any reconnect. Whatever the client happens to send next has to re-establish it.
        foreach (var type in new[]
                 {
                     MessageTypes.Ping,
                     MessageTypes.Command,
                     MessageTypes.FileConsentResponse,
                     MessageTypes.FilePushOffer,
                 })
        {
            var stamped = RemexNativeClient.StampOutbound(new RemexMessage { Type = type }, "phone-1");

            Assert.True(stamped.ClientCapabilities?.SupportsConsentPrompt, type);
        }
    }

    [Fact]
    public void StampingSetsIdentityAndProtocolWithoutDisturbingThePayload()
    {
        var original = new RemexMessage
        {
            Type = MessageTypes.FileConsentResponse,
            FileConsentResponse = new FileConsentResponse
            {
                ConsentId = "abc123",
                Granted = true,
                Remember = true,
            },
        };

        var stamped = RemexNativeClient.StampOutbound(original, "phone-1");

        Assert.Equal("phone-1", stamped.ClientId);
        Assert.Equal(2, stamped.ProtocolVersion);
        Assert.Equal(MessageTypes.FileConsentResponse, stamped.Type);
        Assert.Equal("abc123", stamped.FileConsentResponse?.ConsentId);
        Assert.True(stamped.FileConsentResponse?.Granted);
        Assert.True(stamped.FileConsentResponse?.Remember);
        // `with` on a record leaves the caller's instance alone; the send paths pass messages they did
        // not build and must not mutate.
        Assert.Null(original.ClientCapabilities);
    }

    [Fact]
    public void TheAdvertisementSurvivesTheWireAsTheHostReadsIt()
    {
        var json = RemexJson.Serialize(
            RemexNativeClient.StampOutbound(new RemexMessage { Type = MessageTypes.Ping }, "phone-1"),
            RemexJsonSerializerContext.Default.RemexMessage);

        // The exact property name the host binds. A rename on either side would route every consent
        // question back to the PC dialog with nothing logged.
        Assert.Contains("\"supportsConsentPrompt\":true", json);

        var parsed = RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.RemexMessage);

        Assert.True(parsed?.ClientCapabilities?.SupportsConsentPrompt);
    }

    [Fact]
    public void AbsentCapabilitiesStillMeanNo()
    {
        // The compatibility path, restated from the other end: an older phone sends no such field, and
        // the host must read that as "cannot render a prompt" rather than defaulting it on.
        var parsed = RemexJson.Deserialize(
            """{"type":"ping","clientId":"old-phone"}""",
            RemexJsonSerializerContext.Default.RemexMessage);

        Assert.Null(parsed?.ClientCapabilities);
    }
}
