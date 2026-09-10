using System.Text;
using Remex.Core.Messages;
using Remex.Core.Models;

namespace Remex.Core.Tests;

/// <summary>
/// Round-trip coverage for the 2.0 (legacy, pre-binary-channel) file-transfer wire types. These sit in
/// the same file as the v3 types already covered by <see cref="FileTransferProtocolSerializationTests"/>
/// but had zero test mentions of their own — notable because a v2 peer (an app that has not updated)
/// still relies on this exact path being intact.
/// </summary>
public class LegacyFileTransferV2SerializationTests
{
    private static RemexMessage RoundTrip(RemexMessage message)
    {
        var bytes = MessageSerializer.Serialize(message);
        var back = MessageSerializer.Deserialize(bytes);
        Assert.NotNull(back);
        return back!;
    }

    [Fact]
    public void RoundTrip_FileRootsRequest_SurvivesAsAnEmptyPayload()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileRootsRequest,
            FileRootsRequest = new FileRootsRequest(),
        });

        Assert.NotNull(back.FileRootsRequest);
    }

    [Fact]
    public void RoundTrip_FileRootsResponse_PreservesRootsAndSharedRootPermissions()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileRootsResponse,
            FileRootsResponse = new FileRootsResponse
            {
                Roots =
                [
                    new FileSharedRoot
                    {
                        RootId = "transfers",
                        DisplayName = "Transfers",
                        IsWritable = true,
                        CanRename = true,
                        CanMove = false,
                        CanDelete = true,
                        CanRemoveRoot = false,
                    },
                ],
            },
        });

        var r = back.FileRootsResponse;
        Assert.NotNull(r);
        Assert.Single(r!.Roots);
        var root = r.Roots[0];
        Assert.Equal("transfers", root.RootId);
        Assert.Equal("Transfers", root.DisplayName);
        Assert.True(root.IsWritable);
        Assert.True(root.CanRename);
        Assert.False(root.CanMove);
        Assert.True(root.CanDelete);
        Assert.False(root.CanRemoveRoot);
    }

    [Fact]
    public void RoundTrip_FileTransferProgress_PreservesByteCounters()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileTransferProgress,
            FileTransferProgress = new FileTransferProgress { TransferId = "tx-1", BytesTransferred = 4096, TotalBytes = 8192 },
        });

        var p = back.FileTransferProgress;
        Assert.NotNull(p);
        Assert.Equal("tx-1", p!.TransferId);
        Assert.Equal(4096, p.BytesTransferred);
        Assert.Equal(8192, p.TotalBytes);
    }

    [Fact]
    public void RoundTrip_FileBrowseRequest_PreservesLegacyPathAndRootAddressing()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileBrowseRequest,
            FileBrowseRequest = new FileBrowseRequest
            {
                RequestId = "req-1",
                Path = "/legacy/path",
                RootId = "transfers",
                RelativePath = "sub/dir",
            },
        });

        var b = back.FileBrowseRequest;
        Assert.NotNull(b);
        Assert.Equal("req-1", b!.RequestId);
        Assert.Equal("/legacy/path", b.Path);
        Assert.Equal("transfers", b.RootId);
        Assert.Equal("sub/dir", b.RelativePath);
    }

    [Fact]
    public void RoundTrip_FileBrowseResponse_PreservesEntriesAndEchoedLocation()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileBrowseResponse,
            FileBrowseResponse = new FileBrowseResponse
            {
                RequestId = "req-1",
                RootId = "transfers",
                RelativePath = "sub",
                Entries =
                [
                    new FileEntry { Name = "a.txt", IsDirectory = false, SizeBytes = 10, ModifiedUnixMs = 111 },
                    new FileEntry { Name = "b", IsDirectory = true, SizeBytes = 0, ModifiedUnixMs = 222 },
                ],
            },
        });

        var r = back.FileBrowseResponse;
        Assert.NotNull(r);
        Assert.Equal("transfers", r!.RootId);
        Assert.Equal("sub", r.RelativePath);
        Assert.Equal(2, r.Entries.Length);
        Assert.Equal("a.txt", r.Entries[0].Name);
        Assert.False(r.Entries[0].IsDirectory);
        Assert.Equal(10, r.Entries[0].SizeBytes);
        Assert.True(r.Entries[1].IsDirectory);
        Assert.Null(r.ErrorMessage);
    }

    [Fact]
    public void RoundTrip_FileBrowseResponse_CarriesErrorMessageOnFailure()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileBrowseResponse,
            FileBrowseResponse = new FileBrowseResponse { RequestId = "req-2", Entries = [], ErrorMessage = "access denied" },
        });

        Assert.Equal("access denied", back.FileBrowseResponse!.ErrorMessage);
        Assert.Empty(back.FileBrowseResponse.Entries);
    }

    [Fact]
    public void RoundTrip_FileManageResponse_PreservesSuccessCodeAndConflictNames()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileManageResponse,
            FileManageResponse = new FileManageResponse
            {
                RequestId = "req-3",
                Success = false,
                ErrorMessage = "destination exists",
                ErrorCode = "destination_exists",
                ConflictingName = "report.pdf",
                ResolvedName = "report (2).pdf",
            },
        });

        var m = back.FileManageResponse;
        Assert.NotNull(m);
        Assert.False(m!.Success);
        Assert.Equal("destination exists", m.ErrorMessage);
        Assert.Equal("destination_exists", m.ErrorCode);
        Assert.Equal("report.pdf", m.ConflictingName);
        Assert.Equal("report (2).pdf", m.ResolvedName);
    }

    [Fact]
    public void RoundTrip_FileManageResponse_SuccessLeavesErrorFieldsNull()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileManageResponse,
            FileManageResponse = new FileManageResponse { RequestId = "req-4", Success = true },
        });

        var m = back.FileManageResponse!;
        Assert.True(m.Success);
        Assert.Null(m.ErrorMessage);
        Assert.Null(m.ErrorCode);
        Assert.Null(m.ConflictingName);
        Assert.Null(m.ResolvedName);
    }

    [Fact]
    public void RoundTrip_FileHashRequest_PreservesRootAndPath()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileHashRequest,
            FileHashRequest = new FileHashRequest { RequestId = "req-5", RootId = "transfers", RelativePath = "a.bin" },
        });

        var h = back.FileHashRequest;
        Assert.NotNull(h);
        Assert.Equal("req-5", h!.RequestId);
        Assert.Equal("transfers", h.RootId);
        Assert.Equal("a.bin", h.RelativePath);
    }

    [Fact]
    public void RoundTrip_FileHashResponse_PreservesHashOrError()
    {
        var hashed = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileHashResponse,
            FileHashResponse = new FileHashResponse { RequestId = "req-6", Sha256Base64 = "aGFzaA==" },
        });
        Assert.Equal("aGFzaA==", hashed.FileHashResponse!.Sha256Base64);
        Assert.Null(hashed.FileHashResponse.ErrorMessage);

        var failed = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileHashResponse,
            FileHashResponse = new FileHashResponse { RequestId = "req-7", ErrorMessage = "not found" },
        });
        Assert.Null(failed.FileHashResponse!.Sha256Base64);
        Assert.Equal("not found", failed.FileHashResponse.ErrorMessage);
    }

    [Fact]
    public void RoundTrip_FileRootManageRequest_PreservesAddOperation()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileRootManageRequest,
            FileRootManageRequest = new FileRootManageRequest
            {
                RequestId = "req-8",
                Operation = "add",
                SourceRootId = "transfers",
                SourceRelativePath = "photos",
                RootId = null,
            },
        });

        var r = back.FileRootManageRequest;
        Assert.NotNull(r);
        Assert.Equal("add", r!.Operation);
        Assert.Equal("transfers", r.SourceRootId);
        Assert.Equal("photos", r.SourceRelativePath);
        Assert.Null(r.RootId);
    }

    [Fact]
    public void RoundTrip_FileRootManageRequest_PreservesRemoveOperation()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileRootManageRequest,
            FileRootManageRequest = new FileRootManageRequest { RequestId = "req-9", Operation = "remove", RootId = "photos" },
        });

        var r = back.FileRootManageRequest!;
        Assert.Equal("remove", r.Operation);
        Assert.Equal("photos", r.RootId);
        Assert.Null(r.SourceRootId);
        Assert.Null(r.SourceRelativePath);
    }

    [Fact]
    public void RoundTrip_FileRootManageResponse_PreservesFullRootListAfterChange()
    {
        var back = RoundTrip(new RemexMessage
        {
            Type = MessageTypes.FileRootManageResponse,
            FileRootManageResponse = new FileRootManageResponse
            {
                RequestId = "req-10",
                Roots =
                [
                    new FileSharedRoot { RootId = "transfers", DisplayName = "Transfers", IsWritable = true },
                    new FileSharedRoot { RootId = "photos", DisplayName = "Photos", IsWritable = false },
                ],
            },
        });

        var r = back.FileRootManageResponse;
        Assert.NotNull(r);
        Assert.Equal(2, r!.Roots.Length);
        Assert.Equal("photos", r.Roots[1].RootId);
        Assert.False(r.Roots[1].IsWritable);
        Assert.Null(r.ErrorMessage);
    }

    [Fact]
    public void AllLegacyV2Payloads_SetTogether_SurviveRoundTrip()
    {
        // Strongest missing-[JsonSerializable] / shape-drift catcher for the whole legacy cluster at once.
        var message = new RemexMessage
        {
            Type = MessageTypes.HostInfo,
            FileRootsRequest = new FileRootsRequest(),
            FileRootsResponse = new FileRootsResponse { Roots = [] },
            FileTransferProgress = new FileTransferProgress { TransferId = "t", BytesTransferred = 1, TotalBytes = 2 },
            FileBrowseRequest = new FileBrowseRequest { RequestId = "r" },
            FileBrowseResponse = new FileBrowseResponse { RequestId = "r", Entries = [] },
            FileManageResponse = new FileManageResponse { RequestId = "r", Success = true },
            FileHashRequest = new FileHashRequest { RequestId = "r", RootId = "x", RelativePath = "p" },
            FileHashResponse = new FileHashResponse { RequestId = "r" },
            FileRootManageRequest = new FileRootManageRequest { RequestId = "r", Operation = "add" },
            FileRootManageResponse = new FileRootManageResponse { RequestId = "r", Roots = [] },
        };

        var back = RoundTrip(message);

        Assert.NotNull(back.FileRootsRequest);
        Assert.NotNull(back.FileRootsResponse);
        Assert.NotNull(back.FileTransferProgress);
        Assert.NotNull(back.FileBrowseRequest);
        Assert.NotNull(back.FileBrowseResponse);
        Assert.NotNull(back.FileManageResponse);
        Assert.NotNull(back.FileHashRequest);
        Assert.NotNull(back.FileHashResponse);
        Assert.NotNull(back.FileRootManageRequest);
        Assert.NotNull(back.FileRootManageResponse);
    }

    [Fact]
    public void FileConflictingNameAndResolvedName_TravelUnderTheirExactWireNames()
    {
        // A round trip alone reads back whatever name it wrote, so it cannot catch a JsonPropertyName
        // typo. FileManagerLogic.kt on Android parses these keys by hand, so the string IS the contract.
        var message = new RemexMessage
        {
            Type = MessageTypes.FileManageResponse,
            FileManageResponse = new FileManageResponse
            {
                RequestId = "r",
                Success = false,
                ConflictingName = "report.pdf",
                ResolvedName = "report (2).pdf",
            },
        };

        var json = Encoding.UTF8.GetString(MessageSerializer.Serialize(message));
        Assert.Contains("\"conflictingName\":\"report.pdf\"", json, StringComparison.Ordinal);
        Assert.Contains("\"resolvedName\":\"report (2).pdf\"", json, StringComparison.Ordinal);
    }
}
