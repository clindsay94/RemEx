using System.Reflection;
using Remex.Core.Messages;
using Remex.Core.Models;

namespace Remex.Core.Tests;

/// <summary>
/// Protocol-drift guard for the 2.1 file-sharing overhaul (plan §4). Round-trips every new v3 payload
/// through the source-generated <see cref="Remex.Core.Serialization.RemexJsonSerializerContext"/> so a
/// missing <c>[JsonSerializable]</c> — the #1 NativeAOT link failure — or a field-name/shape drift is
/// caught here rather than at Android link time.
/// </summary>
public class FileTransferProtocolSerializationTests
{
    private static RemexMessage RoundTrip(RemexMessage message)
    {
        var bytes = MessageSerializer.Serialize(message);
        var back = MessageSerializer.Deserialize(bytes);
        Assert.NotNull(back);
        return back!;
    }

    [Fact]
    public void RoundTrip_FileTransferOffer_PreservesAllFields()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileTransferOffer,
            ProtocolVersion = 3,
            FileTransferOffer = new FileTransferOffer
            {
                TransferId = "tx-offer",
                Mode = FileTransferModes.Push,
                SourcePath = "/src/a.bin",
                DestRoot = "transfers",
                DestRelativePath = "sub/a.bin",
                FileName = "a.bin",
                Size = 123456,
                ResumeRequested = true,
            },
        });

        var o = back.FileTransferOffer;
        Assert.NotNull(o);
        Assert.Equal("tx-offer", o!.TransferId);
        Assert.Equal("push", o.Mode);
        Assert.Equal("/src/a.bin", o.SourcePath);
        Assert.Equal("transfers", o.DestRoot);
        Assert.Equal("sub/a.bin", o.DestRelativePath);
        Assert.Equal("a.bin", o.FileName);
        Assert.Equal(123456, o.Size);
        Assert.True(o.ResumeRequested);
        Assert.Equal(3, back.ProtocolVersion);
    }

    [Fact]
    public void RoundTrip_FileTransferReady_PreservesAllFields()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileTransferReady,
            FileTransferReady = new FileTransferReady
            {
                TransferId = "tx-ready",
                Accepted = true,
                StartOffset = 4096,
                DeclineReason = null,
            },
        });

        var r = back.FileTransferReady;
        Assert.NotNull(r);
        Assert.Equal("tx-ready", r!.TransferId);
        Assert.True(r.Accepted);
        Assert.Equal(4096, r.StartOffset);
        Assert.Null(r.DeclineReason);
    }

    [Fact]
    public void RoundTrip_FileTransferComplete_And_Result()
    {
        var complete = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileTransferComplete,
            FileTransferComplete = new FileTransferComplete { TransferId = "tx-c", Sha256Base64 = "aGFzaA==" },
        });
        Assert.Equal("tx-c", complete.FileTransferComplete!.TransferId);
        Assert.Equal("aGFzaA==", complete.FileTransferComplete.Sha256Base64);

        var result = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileTransferResult,
            FileTransferResult = new FileTransferResult { TransferId = "tx-r", Verified = false, Sha256Base64 = "aGFzaA==", Error = "mismatch" },
        });
        Assert.Equal("tx-r", result.FileTransferResult!.TransferId);
        Assert.False(result.FileTransferResult.Verified);
        Assert.Equal("aGFzaA==", result.FileTransferResult.Sha256Base64);
        Assert.Equal("mismatch", result.FileTransferResult.Error);
    }

    [Fact]
    public void RoundTrip_FileTransferControl_PreservesAction()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileTransferControl,
            FileTransferControl = new FileTransferControl { TransferId = "tx", Action = FileTransferControlActions.Pause },
        });
        Assert.Equal("tx", back.FileTransferControl!.TransferId);
        Assert.Equal("pause", back.FileTransferControl.Action);
    }

    [Fact]
    public void RoundTrip_Volumes_PreservesNestedVolumeInfo()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileVolumesResponse,
            FileVolumesResponse = new FileVolumesResponse
            {
                RequestId = "req-vol",
                FullBrowseGranted = true,
                Volumes =
                [
                    new FileVolumeInfo { Id = "C", Label = "System", Path = @"C:\", TotalBytes = 100, FreeBytes = 40, Kind = "fixed" },
                    new FileVolumeInfo { Id = "root", Label = "Root", Path = "/", TotalBytes = 200, FreeBytes = 80, Kind = "root" },
                ],
            },
        });

        var v = back.FileVolumesResponse;
        Assert.NotNull(v);
        Assert.Equal("req-vol", v!.RequestId);
        Assert.True(v.FullBrowseGranted);
        Assert.Equal(2, v.Volumes.Length);
        Assert.Equal("System", v.Volumes[0].Label);
        Assert.Equal("fixed", v.Volumes[0].Kind);
        Assert.Equal(40, v.Volumes[0].FreeBytes);
        Assert.Equal("root", v.Volumes[1].Kind);
    }

    [Fact]
    public void RoundTrip_Search_PreservesEntriesAndTruncation()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileSearchResponse,
            FileSearchResponse = new FileSearchResponse
            {
                RequestId = "req-search",
                Truncated = true,
                Entries =
                [
                    new FileSearchEntry { Name = "hit.txt", RelativePath = "a/b/hit.txt", IsDirectory = false, SizeBytes = 12, ModifiedUnixMs = 999 },
                ],
            },
        });

        var s = back.FileSearchResponse;
        Assert.NotNull(s);
        Assert.Equal("req-search", s!.RequestId);
        Assert.True(s.Truncated);
        Assert.Single(s.Entries);
        Assert.Equal("a/b/hit.txt", s.Entries[0].RelativePath);
        Assert.Equal(12, s.Entries[0].SizeBytes);
    }

    [Fact]
    public void RoundTrip_SearchRequest_PreservesFields()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileSearchRequest,
            FileSearchRequest = new FileSearchRequest
            {
                RequestId = "req",
                RootId = "transfers",
                RelativePath = "sub",
                Query = "*.pdf",
                MaxResults = 500,
            },
        });
        var s = back.FileSearchRequest!;
        Assert.Equal("transfers", s.RootId);
        Assert.Equal("sub", s.RelativePath);
        Assert.Equal("*.pdf", s.Query);
        Assert.Equal(500, s.MaxResults);
    }

    [Fact]
    public void RoundTrip_Metadata_PreservesAllFields()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileMetadataResponse,
            FileMetadataResponse = new FileMetadataResponse
            {
                RequestId = "req-meta",
                Size = 4096,
                CreatedUtc = 111,
                ModifiedUtc = 222,
                IsDirectory = true,
                ItemCount = 7,
                MimeType = "inode/directory",
                ReadOnly = true,
            },
        });

        var m = back.FileMetadataResponse;
        Assert.NotNull(m);
        Assert.Equal("req-meta", m!.RequestId);
        Assert.Equal(4096, m.Size);
        Assert.Equal(111, m.CreatedUtc);
        Assert.Equal(222, m.ModifiedUtc);
        Assert.True(m.IsDirectory);
        Assert.Equal(7, m.ItemCount);
        Assert.Equal("inode/directory", m.MimeType);
        Assert.True(m.ReadOnly);
    }

    [Fact]
    public void RoundTrip_Thumbnail_PreservesFields()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileThumbnailResponse,
            FileThumbnailResponse = new FileThumbnailResponse { RequestId = "req-thumb", JpegBase64 = "/9j/4AA=" },
        });
        Assert.Equal("req-thumb", back.FileThumbnailResponse!.RequestId);
        Assert.Equal("/9j/4AA=", back.FileThumbnailResponse.JpegBase64);
    }

    [Fact]
    public void RoundTrip_ConsentAndPush_PreservesFields()
    {
        var consentReq = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileConsentRequest,
            FileConsentRequest = new FileConsentRequest { ConsentId = "c1", Kind = FileConsentKinds.FullBrowse, Detail = "Browse all drives" },
        });
        Assert.Equal("c1", consentReq.FileConsentRequest!.ConsentId);
        Assert.Equal("full_browse", consentReq.FileConsentRequest.Kind);
        Assert.Equal("Browse all drives", consentReq.FileConsentRequest.Detail);

        var consentResp = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileConsentResponse,
            FileConsentResponse = new FileConsentResponse { ConsentId = "c1", Granted = true, Remember = true },
        });
        Assert.True(consentResp.FileConsentResponse!.Granted);
        Assert.True(consentResp.FileConsentResponse.Remember);

        var pushOffer = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FilePushOffer,
            FilePushOffer = new FilePushOffer
            {
                PushId = "p1",
                Files = [new FilePushFile { Name = "a.jpg", Size = 10 }, new FilePushFile { Name = "b.jpg", Size = 20 }],
            },
        });
        Assert.Equal("p1", pushOffer.FilePushOffer!.PushId);
        Assert.Equal(2, pushOffer.FilePushOffer.Files.Length);
        Assert.Equal("b.jpg", pushOffer.FilePushOffer.Files[1].Name);

        var pushResp = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FilePushResponse,
            FilePushResponse = new FilePushResponse { PushId = "p1", Accepted = true, TransferIds = ["t1", "t2"] },
        });
        Assert.True(pushResp.FilePushResponse!.Accepted);
        Assert.Equal(new[] { "t1", "t2" }, pushResp.FilePushResponse.TransferIds);
    }

    [Fact]
    public void RoundTrip_ConsentRequest_PreservesTheAutoDenyDeadline()
    {
        // RemEx-6mxu. The deadline reaching the renderer intact is the entire bead: the phone cannot
        // show a countdown for a number it never received, and a prompt with no deadline is one the
        // user can still answer after this side has already denied.
        var message = new RemexMessage
        {
            Type = MessageTypes.FileConsentRequest,
            FileConsentRequest = new FileConsentRequest
            {
                ConsentId = "c1",
                Kind = FileConsentKinds.IncomingPush,
                ExpiresAtUnixMs = 1_754_500_000_123L,
            },
        };

        Assert.Equal(1_754_500_000_123L, RoundTrip(message).FileConsentRequest!.ExpiresAtUnixMs);

        // AND UNDER THAT EXACT NAME, which a round-trip alone cannot prove — it reads back whatever
        // name it wrote, so renaming the property passes it while breaking every non-.NET reader. The
        // phone sheet (RemEx-vyhm) parses this key by hand, so the string IS the contract. Also pins
        // the number as a bare integer: quote it and Kotlin's Long parse fails on arrival.
        var json = System.Text.Encoding.UTF8.GetString(MessageSerializer.Serialize(message));
        Assert.Contains("\"expiresAtUnixMs\":1754500000123", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AConsentRequestWithoutAnExpiryIsStillValidOnTheWireInBothDirections()
    {
        // ADDITIVE, WHICH IS WHY THIS NEEDS NO protocolVersion BUMP — asserted rather than assumed,
        // in both directions. Inbound: a host that predates the field sends JSON without it, and that
        // must parse rather than throw. Outbound: an unstamped request must not put `expiresAtUnixMs`
        // on the wire at all, so a peer cannot read a null as "expires at the epoch" and dismiss the
        // prompt the instant it arrives.
        var fromAnOlderHost = System.Text.Encoding.UTF8.GetBytes(
            """{"type":"file_consent_request","fileConsentRequest":{"consentId":"c1","kind":"full_browse"}}""");

        var parsed = MessageSerializer.Deserialize(fromAnOlderHost);

        Assert.NotNull(parsed);
        Assert.Equal("c1", parsed!.FileConsentRequest!.ConsentId);
        Assert.Null(parsed.FileConsentRequest.ExpiresAtUnixMs);

        var json = System.Text.Encoding.UTF8.GetString(MessageSerializer.Serialize(new RemexMessage
        {
            Type = MessageTypes.FileConsentRequest,
            FileConsentRequest = new FileConsentRequest { ConsentId = "c1", Kind = FileConsentKinds.FullBrowse },
        }));

        Assert.DoesNotContain("expiresAtUnixMs", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTrip_FileRootsResponse_PreservesFileCapabilities()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileRootsResponse,
            FileRootsResponse = new FileRootsResponse
            {
                Roots = [],
                FileCapabilities = new FileCapabilities
                {
                    Protocol = 3,
                    Binary = true,
                    Resume = true,
                    Ops = ["delete", "rename", "copy", "move", "mkdir", "search"],
                    FullBrowse = true,
                    Push = true,
                },
            },
        });

        var caps = back.FileRootsResponse!.FileCapabilities;
        Assert.NotNull(caps);
        Assert.Equal(3, caps!.Protocol);
        Assert.True(caps.Binary);
        Assert.True(caps.Resume);
        Assert.Contains("mkdir", caps.Ops);
        Assert.True(caps.FullBrowse);
        Assert.True(caps.Push);
    }

    [Fact]
    public void RoundTrip_ExtendedFileManageRequest_PreservesNewFields()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileManageRequest,
            FileManageRequest = new FileManageRequest
            {
                RequestId = "req",
                RootId = "transfers",
                RelativePath = "a.txt",
                Operation = FileManageOperations.Move,
                DestinationPath = "sub/a.txt",
                Overwrite = true,
            },
        });

        var m = back.FileManageRequest!;
        Assert.Equal("move", m.Operation);
        Assert.Equal("sub/a.txt", m.DestinationPath);
        Assert.True(m.Overwrite);
    }

    [Fact]
    public void LegacyFileManageRequest_WithoutNewFields_DefaultsAreSafe()
    {
        // A v2 peer omits destinationPath/overwrite entirely; they must deserialize to safe defaults.
        var back = MessageSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(
            "{\"type\":\"file_manage_request\",\"fileManageRequest\":{\"requestId\":\"r\",\"rootId\":\"transfers\",\"relativePath\":\"a.txt\",\"operation\":\"delete\"}}"));

        Assert.NotNull(back);
        var m = back!.FileManageRequest!;
        Assert.Equal("delete", m.Operation);
        Assert.Null(m.DestinationPath);
        Assert.False(m.Overwrite);
    }

    [Fact]
    public void AllNewPayloads_SetTogether_SurviveRoundTrip()
    {
        // The strongest missing-[JsonSerializable] / drift catcher: every new payload on one envelope.
        var message = new RemexMessage
        {
            Type = MessageTypes.HostInfo,
            ProtocolVersion = 3,
            FileTransferOffer = new FileTransferOffer { TransferId = "1", Mode = "download", FileName = "f", Size = 1 },
            FileTransferReady = new FileTransferReady { TransferId = "1", Accepted = true },
            FileTransferComplete = new FileTransferComplete { TransferId = "1", Sha256Base64 = "h" },
            FileTransferResult = new FileTransferResult { TransferId = "1", Verified = true },
            FileTransferControl = new FileTransferControl { TransferId = "1", Action = "cancel" },
            FileVolumesRequest = new FileVolumesRequest { RequestId = "r" },
            FileVolumesResponse = new FileVolumesResponse { RequestId = "r", Volumes = [] },
            FileSearchRequest = new FileSearchRequest { RequestId = "r", RootId = "x", Query = "q", MaxResults = 1 },
            FileSearchResponse = new FileSearchResponse { RequestId = "r", Entries = [] },
            FileMetadataRequest = new FileMetadataRequest { RequestId = "r", RootId = "x", RelativePath = "p" },
            FileMetadataResponse = new FileMetadataResponse { RequestId = "r" },
            FileThumbnailRequest = new FileThumbnailRequest { RequestId = "r", RootId = "x", RelativePath = "p", MaxDim = 128 },
            FileThumbnailResponse = new FileThumbnailResponse { RequestId = "r" },
            FileConsentRequest = new FileConsentRequest { ConsentId = "c", Kind = "full_browse" },
            FileConsentResponse = new FileConsentResponse { ConsentId = "c", Granted = false },
            FilePushOffer = new FilePushOffer { PushId = "p", Files = [] },
            FilePushResponse = new FilePushResponse { PushId = "p", Accepted = false },
        };

        var back = RoundTrip(message);

        Assert.NotNull(back.FileTransferOffer);
        Assert.NotNull(back.FileTransferReady);
        Assert.NotNull(back.FileTransferComplete);
        Assert.NotNull(back.FileTransferResult);
        Assert.NotNull(back.FileTransferControl);
        Assert.NotNull(back.FileVolumesRequest);
        Assert.NotNull(back.FileVolumesResponse);
        Assert.NotNull(back.FileSearchRequest);
        Assert.NotNull(back.FileSearchResponse);
        Assert.NotNull(back.FileMetadataRequest);
        Assert.NotNull(back.FileMetadataResponse);
        Assert.NotNull(back.FileThumbnailRequest);
        Assert.NotNull(back.FileThumbnailResponse);
        Assert.NotNull(back.FileConsentRequest);
        Assert.NotNull(back.FileConsentResponse);
        Assert.NotNull(back.FilePushOffer);
        Assert.NotNull(back.FilePushResponse);
    }

    [Fact]
    public void EveryMessageTypeConst_RoundTripsPreservingType()
    {
        // §4 protocol-drift guard: every MessageTypes const survives a round-trip on the type discriminator.
        foreach (var type in AllMessageTypeConstants())
        {
            var back = RoundTrip(new RemexMessage { Type = type });
            Assert.Equal(type, back.Type);
        }
    }

    [Fact]
    public void MessageTypeConstants_AreUnique()
    {
        var all = AllMessageTypeConstants().ToList();
        var distinct = all.Distinct().ToList();
        Assert.Equal(distinct.Count, all.Count);
    }

    [Theory]
    [InlineData("file_transfer_offer")]
    [InlineData("file_transfer_ready")]
    [InlineData("file_transfer_complete")]
    [InlineData("file_transfer_result")]
    [InlineData("file_transfer_control")]
    [InlineData("file_volumes_request")]
    [InlineData("file_volumes_response")]
    [InlineData("file_search_request")]
    [InlineData("file_search_response")]
    [InlineData("file_metadata_request")]
    [InlineData("file_metadata_response")]
    [InlineData("file_thumbnail_request")]
    [InlineData("file_thumbnail_response")]
    [InlineData("file_consent_request")]
    [InlineData("file_consent_response")]
    [InlineData("file_push_offer")]
    [InlineData("file_push_response")]
    public void NewMessageTypeConstants_HaveExpectedWireStrings(string expected)
    {
        // Locks the exact wire strings the Android mirror (Phase B) must match verbatim.
        Assert.Contains(expected, AllMessageTypeConstants());
    }

    private static System.Collections.Generic.IEnumerable<string> AllMessageTypeConstants()
        => typeof(MessageTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);
}
