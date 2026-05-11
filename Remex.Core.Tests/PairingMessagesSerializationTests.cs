using Remex.Core.Messages;
using Remex.Core.Models;

namespace Remex.Core.Tests;

public class PairingMessagesSerializationTests
{
    [Fact]
    public void ProtocolVersion_DefaultsToTwo()
    {
        var msg = new RemexMessage { Type = MessageTypes.Ping };
        Assert.Equal(2, msg.ProtocolVersion);
    }

    [Fact]
    public void RoundTrip_PairingRequest_PreservesAllFields()
    {
        var original = new RemexMessage
        {
            Type = MessageTypes.PairingRequest,
            ClientId = "client-123",
            PairingRequest = new PairingRequest
            {
                ClientPublicKeyBase64 = "pubkey",
                ClientName = "TestClient",
                ClientVersion = "2.0.0",
                ClientId = "client-123"
            }
        };

        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized!.ProtocolVersion);
        Assert.Equal("client-123", deserialized.ClientId);
        Assert.NotNull(deserialized.PairingRequest);
        Assert.Equal("pubkey", deserialized.PairingRequest!.ClientPublicKeyBase64);
        Assert.Equal("TestClient", deserialized.PairingRequest.ClientName);
        Assert.Equal("2.0.0", deserialized.PairingRequest.ClientVersion);
        Assert.Equal("client-123", deserialized.PairingRequest.ClientId);
    }

    [Fact]
    public void RoundTrip_PairingResponse_PreservesAllFields()
    {
        var original = new RemexMessage
        {
            Type = MessageTypes.PairingResponse,
            PairingResponse = new PairingResponse
            {
                HostPublicKeyBase64 = "hostpub",
                HostId = "host-123",
                HostName = "TestHost",
                CertificateSpkiHashBase64 = "spki",
                PinHmacBase64 = "hmac"
            }
        };

        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.PairingResponse);
        Assert.Equal("hostpub", deserialized.PairingResponse!.HostPublicKeyBase64);
        Assert.Equal("host-123", deserialized.PairingResponse.HostId);
        Assert.Equal("TestHost", deserialized.PairingResponse.HostName);
        Assert.Equal("spki", deserialized.PairingResponse.CertificateSpkiHashBase64);
        Assert.Equal("hmac", deserialized.PairingResponse.PinHmacBase64);
    }

    [Fact]
    public void RoundTrip_PairingComplete_PreservesAllFields()
    {
        var original = new RemexMessage
        {
            Type = MessageTypes.PairingComplete,
            ClientId = "client-123",
            PairingComplete = new PairingComplete
            {
                ClientPinHmacBase64 = "clienthmac",
                ClientId = "client-123"
            }
        };

        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal("client-123", deserialized!.ClientId);
        Assert.NotNull(deserialized!.PairingComplete);
        Assert.Equal("clienthmac", deserialized.PairingComplete!.ClientPinHmacBase64);
        Assert.Equal("client-123", deserialized.PairingComplete.ClientId);
    }

    [Fact]
    public void RoundTrip_FileTransferStart_PreservesAllFields()
    {
        var original = new RemexMessage
        {
            Type = MessageTypes.FileTransferStart,
            FileTransferStart = new FileTransferStart
            {
                TransferId = "tx-1",
                Direction = "upload",
                RemotePath = "path/to/file",
                FileName = "file.txt",
                TotalBytes = 1024,
                Sha256Base64 = "hash"
            }
        };

        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.FileTransferStart);
        Assert.Equal("tx-1", deserialized.FileTransferStart!.TransferId);
        Assert.Equal("upload", deserialized.FileTransferStart.Direction);
        Assert.Equal("path/to/file", deserialized.FileTransferStart.RemotePath);
        Assert.Equal("file.txt", deserialized.FileTransferStart.FileName);
        Assert.Equal(1024, deserialized.FileTransferStart.TotalBytes);
        Assert.Equal("hash", deserialized.FileTransferStart.Sha256Base64);
    }

    [Fact]
    public void RoundTrip_FileTransferChunk_PreservesAllFields()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03 };
        var original = new RemexMessage
        {
            Type = MessageTypes.FileTransferChunk,
            FileTransferChunk = new FileTransferChunk
            {
                TransferId = "tx-2",
                Offset = 4096,
                DataBase64 = Convert.ToBase64String(payload)
            }
        };

        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized?.FileTransferChunk);
        Assert.Equal("tx-2", deserialized!.FileTransferChunk!.TransferId);
        Assert.Equal(4096, deserialized.FileTransferChunk.Offset);
        Assert.Equal(Convert.ToBase64String(payload), deserialized.FileTransferChunk.DataBase64);
    }

    [Fact]
    public void RoundTrip_FileTransferEnd_PreservesAllFields()
    {
        var original = new RemexMessage
        {
            Type = MessageTypes.FileTransferEnd,
            FileTransferEnd = new FileTransferEnd
            {
                TransferId = "tx-3",
                Success = true,
                Sha256Base64 = "sha256=="
            }
        };

        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized?.FileTransferEnd);
        Assert.Equal("tx-3", deserialized!.FileTransferEnd!.TransferId);
        Assert.True(deserialized.FileTransferEnd.Success);
        Assert.Equal("sha256==", deserialized.FileTransferEnd.Sha256Base64);
    }

    [Fact]
    public void RoundTrip_FileTransferCancel_PreservesTransferId()
    {
        var original = new RemexMessage
        {
            Type = MessageTypes.FileTransferCancel,
            FileTransferCancel = new FileTransferCancel { TransferId = "tx-4" }
        };

        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized?.FileTransferCancel);
        Assert.Equal("tx-4", deserialized!.FileTransferCancel!.TransferId);
    }

    [Fact]
    public void Deserialize_InvalidJson_ReturnsNull()
    {
        var garbage = System.Text.Encoding.UTF8.GetBytes("not-json{{");
        var result = MessageSerializer.Deserialize(garbage);

        Assert.Null(result);
    }
}
