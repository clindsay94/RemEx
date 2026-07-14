using System.Text;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Serialization;

namespace Remex.Core.Tests;

/// <summary>
/// RemEx-y6x6 diagnostic: reproduce the EXACT control-plane round-trip the Android client drives, starting
/// from the verbatim JSON string the Kotlin <c>FileTransferEngine.send()</c> produces (via org.json), NOT a
/// C# object. The native send path (<c>AndroidNativeExports.HandleDispatchMessage</c>) does
/// <c>RemexJson.Deserialize(json, RemexMessage)</c> then re-serializes to the host; if the body does not
/// survive that, the host sees <c>FileTransferOffer == null</c>, its <c>is not null</c> guard fails, no
/// <c>file_transfer_ready</c> is sent, and the phone times out with "Peer did not respond".
/// </summary>
public class FileTransferOfferRawJsonDiagnosticTests
{
    // Verbatim shape of FileTransferEngine.sendOffer() for a DOWNLOAD (PC -> phone).
    private const string DownloadOfferJson =
        "{\"type\":\"file_transfer_offer\",\"protocolVersion\":3,\"fileTransferOffer\":{" +
        "\"transferId\":\"tx-123\",\"mode\":\"download\",\"destRoot\":\"Documents\"," +
        "\"destRelativePath\":\"reports/q3.pdf\",\"fileName\":\"q3.pdf\",\"size\":1048576," +
        "\"resumeRequested\":false}}";

    // Verbatim shape for an UPLOAD/push (phone -> PC): sourcePath present, size as a large long.
    private const string UploadOfferJson =
        "{\"type\":\"file_transfer_offer\",\"protocolVersion\":3,\"fileTransferOffer\":{" +
        "\"transferId\":\"tx-456\",\"mode\":\"push\",\"sourcePath\":\"/storage/emulated/0/DCIM/x.jpg\"," +
        "\"destRoot\":\"transfers\",\"destRelativePath\":\"inbox\",\"fileName\":\"x.jpg\"," +
        "\"size\":5242880,\"resumeRequested\":false}}";

    [Theory]
    [InlineData(nameof(DownloadOfferJson))]
    [InlineData(nameof(UploadOfferJson))]
    public void PhoneOfferJson_DeserializesWithNonNullBody(string which)
    {
        var json = which == nameof(DownloadOfferJson) ? DownloadOfferJson : UploadOfferJson;

        // Step 1: exactly what HandleDispatchMessage does with the phone's string.
        var msg = RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.RemexMessage);
        Assert.NotNull(msg);
        Assert.Equal(MessageTypes.FileTransferOffer, msg!.Type);
        Assert.NotNull(msg.FileTransferOffer); // <-- if null here, the host's guard drops the offer.

        // Step 2: re-serialize (SendMessageAsync -> MessageSerializer.Serialize) and re-read (host side).
        var onWire = MessageSerializer.Serialize(msg);
        var hostView = MessageSerializer.Deserialize(onWire);
        Assert.NotNull(hostView);
        Assert.NotNull(hostView!.FileTransferOffer);
        Assert.False(string.IsNullOrEmpty(hostView.FileTransferOffer!.TransferId));
        Assert.False(string.IsNullOrEmpty(hostView.FileTransferOffer!.FileName));
    }

    [Fact]
    public void HostReadyJson_IsRecognizedAsFilePrefixedAndDeserializes()
    {
        // What SendReadyAsync puts on the wire back to the phone.
        var ready = MessageSerializer.Serialize(new RemexMessage
        {
            Type = MessageTypes.FileTransferReady,
            ProtocolVersion = 3,
            FileTransferReady = new FileTransferReady { TransferId = "tx-123", Accepted = true, StartOffset = 0 },
        });
        var back = MessageSerializer.Deserialize(ready);
        Assert.NotNull(back);
        // The native router forwards to the phone only if Type starts with "file_" (the fix in
        // AndroidNativeExports.OnNativeMessageReceived).
        Assert.StartsWith("file_", back!.Type);
        Assert.NotNull(back.FileTransferReady);
    }
}
