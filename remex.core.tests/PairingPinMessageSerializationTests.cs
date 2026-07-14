using System.Text;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Models.IPC;

namespace Remex.Core.Tests;

/// <summary>
/// Wire-format tests for the ASI-compliance PIN-over-WS messages (RemEx-1t0b): the
/// <c>pairing_pin_request</c> / <c>pairing_pin_response</c> envelopes and the
/// <c>supportsPinAutoFetch</c> flag on <c>pairing_response</c>. These are an optional protocol
/// addition (protocolVersion stays 2), so both new-field serialization AND old-peer tolerance of
/// unknown members must hold. <see cref="MessageSerializer.Serialize"/> emits UTF-8 bytes and
/// <see cref="MessageSerializer.Deserialize"/> takes UTF-8 bytes — the on-wire form.
/// </summary>
public class PairingPinMessageSerializationTests
{
    private static string Json(RemexMessage msg) => Encoding.UTF8.GetString(MessageSerializer.Serialize(msg));

    private static RemexMessage? RoundTrip(RemexMessage msg) => MessageSerializer.Deserialize(MessageSerializer.Serialize(msg));

    private static RemexMessage? FromJson(string json) => MessageSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void PairingPinRequest_RoundTrips_PreservingTypeClientAndCorrelation()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.PairingPinRequest,
            ProtocolVersion = 2,
            ClientId = "client-abc",
            CorrelationId = "corr-123",
        });

        Assert.NotNull(back);
        Assert.Equal("pairing_pin_request", back!.Type);
        Assert.Equal(MessageTypes.PairingPinRequest, back.Type);
        Assert.Equal("client-abc", back.ClientId);
        Assert.Equal("corr-123", back.CorrelationId);
        Assert.Null(back.PairingPin); // request carries no payload
    }

    [Fact]
    public void PairingPinResponse_WithPin_RoundTrips_UsingCamelCaseJson()
    {
        var msg = new RemexMessage
        {
            Type = MessageTypes.PairingPinResponse,
            CorrelationId = "corr-123",
            PairingPin = new PairingPinInfo("123456", 1770000000000L),
        };

        var json = Json(msg);
        // camelCase property names on the raw JSON (source-gen context is CamelCase).
        Assert.Contains("\"pairingPin\"", json);
        Assert.Contains("\"pin\"", json);
        Assert.Contains("\"expiresAtUnixMs\"", json);

        var back = RoundTrip(msg);
        Assert.NotNull(back);
        Assert.Equal(MessageTypes.PairingPinResponse, back!.Type);
        Assert.Equal("corr-123", back.CorrelationId);
        Assert.NotNull(back.PairingPin);
        Assert.Equal("123456", back.PairingPin!.Pin);
        Assert.Equal(1770000000000L, back.PairingPin.ExpiresAtUnixMs);
    }

    [Fact]
    public void PairingPinResponse_WithoutPin_HasNullPayload()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.PairingPinResponse,
            CorrelationId = "corr-xyz",
            PairingPin = null, // deny / no-session: deliberately indistinguishable
        });

        Assert.NotNull(back);
        Assert.Equal(MessageTypes.PairingPinResponse, back!.Type);
        Assert.Null(back.PairingPin);
    }

    [Fact]
    public void PairingResponse_SupportsPinAutoFetch_SerializesCamelCaseAndRoundTrips()
    {
        var msg = new RemexMessage
        {
            Type = MessageTypes.PairingResponse,
            PairingResponse = new PairingResponse
            {
                HostPublicKeyBase64 = "pub",
                HostId = "host-1",
                HostName = "PC",
                CertificateSpkiHashBase64 = "spki",
                PinHmacBase64 = "hmac",
                SupportsPinAutoFetch = true,
            },
        };

        Assert.Contains("\"supportsPinAutoFetch\"", Json(msg));

        var back = RoundTrip(msg);
        Assert.NotNull(back);
        Assert.NotNull(back!.PairingResponse);
        Assert.True(back.PairingResponse!.SupportsPinAutoFetch);
    }

    [Fact]
    public void PairingResponse_WithoutFlag_DefaultsToFalse()
    {
        // An OLD host's pairing_response has no supportsPinAutoFetch member at all.
        const string legacyJson =
            "{\"type\":\"pairing_response\",\"protocolVersion\":2," +
            "\"pairingResponse\":{\"hostPublicKey\":\"pub\",\"hostId\":\"h\",\"hostName\":\"PC\"," +
            "\"certificateSpkiHash\":\"spki\",\"pinHmac\":\"hmac\"}}";

        var back = FromJson(legacyJson);

        Assert.NotNull(back);
        Assert.NotNull(back!.PairingResponse);
        Assert.False(back.PairingResponse!.SupportsPinAutoFetch); // absent ⇒ old host ⇒ manual entry
    }

    [Fact]
    public void OldPeer_Skips_UnknownMembers_OnPairingResponse()
    {
        // A NEW host's pairing_response arriving at an OLD peer that doesn't know the field: the
        // deserializer must SKIP unknown members (STJ default), not throw. Includes a future field
        // to prove the skip is general, not special-cased.
        const string newerJson =
            "{\"type\":\"pairing_response\",\"protocolVersion\":2," +
            "\"pairingResponse\":{\"hostPublicKey\":\"pub\",\"hostId\":\"h\",\"hostName\":\"PC\"," +
            "\"certificateSpkiHash\":\"spki\",\"pinHmac\":\"hmac\",\"supportsPinAutoFetch\":true," +
            "\"someFutureField\":123}}";

        var back = FromJson(newerJson);

        Assert.NotNull(back);
        Assert.Equal(MessageTypes.PairingResponse, back!.Type);
        Assert.NotNull(back.PairingResponse);
        Assert.Equal("h", back.PairingResponse!.HostId);
    }
}
