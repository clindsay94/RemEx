using Remex.Agent.Services.FileTransfer;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services.FileTransfer;

namespace Remex.Agent.Tests;

/// <summary>
/// The PC browsing a paired phone (RemEx-xt0af): the relay's four rules, each of which is the thing
/// standing between a local process and the phone's files.
/// </summary>
public sealed class PhoneFileRelayTests
{
    private const string PhoneA = "phone-a";
    private const string PhoneB = "phone-b";

    private static RemexMessage BrowseRequest(string requestId, string? claimedClientId = null) => new()
    {
        Type = MessageTypes.FileBrowseRequest,
        ClientId = claimedClientId,
        FileBrowseRequest = new FileBrowseRequest { RequestId = requestId, RootId = "root", RelativePath = "DCIM" },
    };

    private static RemexMessage BrowseReply(string requestId, params string[] names) => new()
    {
        Type = MessageTypes.FileBrowseResponse,
        FileBrowseResponse = new FileBrowseResponse
        {
            RequestId = requestId,
            Entries = [.. names.Select(n => new FileEntry { Name = n, IsDirectory = false })],
        },
    };

    // ─── Rule 1: paired AND connected ────────────────────────────────────────────

    [Fact]
    public void Open_APairedConnectedPhone_Succeeds()
    {
        using var kit = new PhoneRelayTestKit();
        kit.ConnectProvenPhone(PhoneA, "Pixel");

        using var connection = kit.Relay.Open(PhoneA);

        Assert.Equal(PhoneA, connection.ClientId);
        Assert.True(connection.IsConnected);
    }

    [Fact]
    public void Open_AConnectedPhoneThatIsNotPaired_Throws()
    {
        using var kit = new PhoneRelayTestKit();
        kit.ConnectProvenPhone(PhoneA, paired: false);

        Assert.Throws<PhoneNotConnectedException>(() => kit.Relay.Open(PhoneA));
    }

    [Fact]
    public void Open_APairedPhoneThatIsNotConnected_Throws()
    {
        using var kit = new PhoneRelayTestKit();
        kit.Paired.RegisterClient(PhoneA, new byte[32]);

        Assert.Throws<PhoneNotConnectedException>(() => kit.Relay.Open(PhoneA));
    }

    [Fact]
    public void Open_ALoopbackSessionClaimingAPairedPhone_DoesNotMakeItReachable()
    {
        // RemEx-4215: any local process can open /ws on 127.0.0.1 and name a paired phone. It must not
        // become something the PC's File Transfer screen can talk to as that phone.
        using var kit = new PhoneRelayTestKit();
        kit.Paired.RegisterClient(PhoneA, new byte[32]);
        kit.ConnectLoopbackClaiming(PhoneA);

        Assert.Throws<PhoneNotConnectedException>(() => kit.Relay.Open(PhoneA));
        Assert.Empty(kit.Relay.AvailablePhones());
    }

    [Fact]
    public void AvailablePhones_ListsOnlyPairedConnectedPhones_WithTheirNames()
    {
        using var kit = new PhoneRelayTestKit();
        kit.ConnectProvenPhone(PhoneA, "Pixel 9");
        kit.ConnectProvenPhone(PhoneB, name: null);
        kit.ConnectProvenPhone("stranger", "Unpaired", paired: false);
        kit.Paired.RegisterClient("offline", new byte[32]);

        var phones = kit.Relay.AvailablePhones().OrderBy(p => p.ClientId, StringComparer.Ordinal).ToList();

        Assert.Equal([PhoneA, PhoneB], phones.Select(p => p.ClientId));
        Assert.Equal("Pixel 9", phones[0].DisplayName);
        Assert.Null(phones[1].DisplayName);
    }

    [Fact]
    public async Task Send_ToAPhoneThatLeftAfterOpening_ThrowsPhoneNotConnected()
    {
        using var kit = new PhoneRelayTestKit();
        var (_, session) = kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);

        session.Dispose();

        await Assert.ThrowsAsync<PhoneNotConnectedException>(() => connection.SendAsync(BrowseRequest("r1")));
    }

    // ─── Rule 2: the read-only allowlist ─────────────────────────────────────────

    [Fact]
    public async Task Send_AnAllowlistedRequest_ReachesThePhone()
    {
        using var kit = new PhoneRelayTestKit();
        var (socket, _) = kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);

        await connection.SendAsync(BrowseRequest("r1"));

        var sent = Assert.Single(socket.MessagesOfType(MessageTypes.FileBrowseRequest));
        Assert.Equal("r1", sent.FileBrowseRequest!.RequestId);
    }

    public static TheoryData<RemexMessage> RefusedRequests() =>
    [
        new RemexMessage
        {
            Type = MessageTypes.FileManageRequest,
            FileManageRequest = new FileManageRequest { RequestId = "m", RootId = "root", RelativePath = "a.jpg", Operation = FileManageOperations.Delete },
        },
        new RemexMessage
        {
            Type = MessageTypes.FileRootManageRequest,
            FileRootManageRequest = new FileRootManageRequest { RequestId = "rm", Operation = "remove", RootId = "root" },
        },
        new RemexMessage
        {
            Type = MessageTypes.FileHashRequest,
            FileHashRequest = new FileHashRequest { RequestId = "h", RootId = "root", RelativePath = "a.jpg" },
        },
        new RemexMessage
        {
            Type = MessageTypes.FileTransferOffer,
            FileTransferOffer = new FileTransferOffer { TransferId = "t", Mode = FileTransferModes.Download, FileName = "a.jpg", Size = 0 },
        },
        new RemexMessage { Type = MessageTypes.FilePushOffer },
        new RemexMessage { Type = MessageTypes.Ping },
        // An allowlisted TYPE with no body is refused too: the type alone is not the request.
        new RemexMessage { Type = MessageTypes.FileBrowseRequest },
    ];

    [Theory]
    [MemberData(nameof(RefusedRequests))]
    public async Task Send_ARequestOffTheAllowlist_IsRefusedAndNeverReachesThePhone(RemexMessage request)
    {
        using var kit = new PhoneRelayTestKit();
        var (socket, _) = kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);

        await Assert.ThrowsAsync<PhoneFileRequestRefusedException>(() => connection.SendAsync(request));

        Assert.Empty(socket.Messages);
    }

    // ─── Rule 3: no client id on the way out ─────────────────────────────────────

    [Fact]
    public async Task Send_StripsAnyClientIdTheCallerPutOnTheRequest()
    {
        using var kit = new PhoneRelayTestKit();
        var (socket, _) = kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);

        await connection.SendAsync(BrowseRequest("r1", claimedClientId: PhoneB));

        Assert.Null(Assert.Single(socket.Messages).ClientId);
    }

    // ─── Rule 4: replies only from the proven phone that was asked ──────────────

    [Fact]
    public async Task Reply_FromThePhoneThatWasAsked_IsDeliveredToTheAskingConnection()
    {
        using var kit = new PhoneRelayTestKit();
        kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        var received = new List<RemexMessage>();
        connection.FileTransferMessageReceived += received.Add;
        await connection.SendAsync(BrowseRequest("r1"));

        Assert.True(kit.Relay.TryDeliverReply(PhoneA, identityProven: true, isLoopback: false, BrowseReply("r1", "a.jpg")));

        Assert.Equal("a.jpg", Assert.Single(Assert.Single(received).FileBrowseResponse!.Entries).Name);
    }

    [Fact]
    public async Task Reply_FromADifferentPairedPhone_IsDropped()
    {
        // THE ROUTING CHECK. Phone B is paired and proven, and has learned (or guessed) the request id
        // the PC sent to phone A. Its answer must not be shown as phone A's files.
        using var kit = new PhoneRelayTestKit();
        kit.ConnectProvenPhone(PhoneA);
        kit.ConnectProvenPhone(PhoneB);
        using var connection = kit.Relay.Open(PhoneA);
        var received = new List<RemexMessage>();
        connection.FileTransferMessageReceived += received.Add;
        await connection.SendAsync(BrowseRequest("r1"));

        Assert.False(kit.Relay.TryDeliverReply(PhoneB, identityProven: true, isLoopback: false, BrowseReply("r1", "planted.exe")));

        Assert.Empty(received);

        // And the genuine answer still lands afterwards: the impostor did not consume the request.
        Assert.True(kit.Relay.TryDeliverReply(PhoneA, identityProven: true, isLoopback: false, BrowseReply("r1", "a.jpg")));
        Assert.Single(received);
    }

    [Fact]
    public async Task Reply_FromAnUnprovenSession_IsDropped()
    {
        using var kit = new PhoneRelayTestKit();
        kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        var received = new List<RemexMessage>();
        connection.FileTransferMessageReceived += received.Add;
        await connection.SendAsync(BrowseRequest("r1"));

        Assert.False(kit.Relay.TryDeliverReply(PhoneA, identityProven: false, isLoopback: false, BrowseReply("r1", "planted.exe")));

        Assert.Empty(received);
    }

    [Fact]
    public async Task Reply_FromLoopback_IsDropped_EvenWhenItNamesTheRightPhone()
    {
        using var kit = new PhoneRelayTestKit();
        kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        var received = new List<RemexMessage>();
        connection.FileTransferMessageReceived += received.Add;
        await connection.SendAsync(BrowseRequest("r1"));

        Assert.False(kit.Relay.TryDeliverReply(PhoneA, identityProven: true, isLoopback: true, BrowseReply("r1", "planted.exe")));

        Assert.Empty(received);
    }

    [Fact]
    public async Task Reply_ToARequestNobodySent_IsDropped()
    {
        using var kit = new PhoneRelayTestKit();
        kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        var received = new List<RemexMessage>();
        connection.FileTransferMessageReceived += received.Add;
        await connection.SendAsync(BrowseRequest("r1"));

        Assert.False(kit.Relay.TryDeliverReply(PhoneA, identityProven: true, isLoopback: false, BrowseReply("not-asked")));
        Assert.Empty(received);
    }

    [Fact]
    public async Task Reply_IsDeliveredOnlyToTheConnectionThatAsked_NeverBroadcast()
    {
        using var kit = new PhoneRelayTestKit();
        kit.ConnectProvenPhone(PhoneA);
        using var asking = kit.Relay.Open(PhoneA);
        using var bystander = kit.Relay.Open(PhoneA);
        var askingReceived = new List<RemexMessage>();
        var bystanderReceived = new List<RemexMessage>();
        asking.FileTransferMessageReceived += askingReceived.Add;
        bystander.FileTransferMessageReceived += bystanderReceived.Add;
        await asking.SendAsync(BrowseRequest("r1"));

        Assert.True(kit.Relay.TryDeliverReply(PhoneA, identityProven: true, isLoopback: false, BrowseReply("r1")));

        Assert.Single(askingReceived);
        Assert.Empty(bystanderReceived);
    }

    // ─── Timeouts and disconnects complete what is pending ───────────────────────

    [Fact]
    public async Task PhoneDisconnecting_FailsWhatIsPending_AndRaisesDisconnectedOnce()
    {
        using var kit = new PhoneRelayTestKit();
        var (_, session) = kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        using var client = new FileTransferClient(connection);
        var disconnects = 0;
        connection.Disconnected += () => disconnects++;

        var browse = client.BrowseRemoteAsync("root", "DCIM", CancellationToken.None);
        session.Dispose();

        var failure = await Assert.ThrowsAsync<IOException>(() => browse.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("Browse error", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, disconnects);
        Assert.False(connection.IsConnected);
    }

    [Fact]
    public async Task APhoneThatRedialled_KeepsItsConnectionsWhenTheOldSessionUnwinds()
    {
        using var kit = new PhoneRelayTestKit();
        var (_, oldSession) = kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        var disconnects = 0;
        connection.Disconnected += () => disconnects++;

        var (newSocket, _) = kit.ConnectProvenPhone(PhoneA);
        oldSession.Dispose();

        Assert.Equal(0, disconnects);
        await connection.SendAsync(BrowseRequest("r1"));
        Assert.Single(newSocket.MessagesOfType(MessageTypes.FileBrowseRequest));
    }

    [Fact]
    public async Task APhoneThatNeverAnswers_FailsTheRequestAtTheRelayTimeout()
    {
        using var kit = new PhoneRelayTestKit(requestTimeout: TimeSpan.FromMilliseconds(150));
        kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        using var client = new FileTransferClient(connection);

        var metadata = client.GetMetadataRemoteAsync("root", "a.jpg", CancellationToken.None);

        await Assert.ThrowsAnyAsync<IOException>(() => metadata.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ─── End to end: the real client over the relay, against a fake phone ────────

    [Fact]
    public async Task TheRealClient_BrowsesSearchesAndReadsDetailsThroughTheRelay()
    {
        using var kit = new PhoneRelayTestKit();
        var (socket, _) = kit.ConnectProvenPhone(PhoneA, "Pixel");
        socket.OnMessage = request => _ = Task.Run(() =>
            kit.Relay.TryDeliverReply(PhoneA, identityProven: true, isLoopback: false, FakePhoneAnswer(request)));

        using var connection = kit.Relay.Open(PhoneA);
        using var client = new FileTransferClient(connection);

        var roots = await client.ListRemoteRootsAsync(CancellationToken.None);
        Assert.Equal("DCIM", Assert.Single(roots).DisplayName);
        Assert.True(client.SupportsV3);
        Assert.True(client.SupportsFullBrowse);

        var entries = await client.BrowseRemoteAsync("root", "/", CancellationToken.None);
        Assert.Equal(["IMG_1.jpg", "Camera"], entries.Select(e => e.Name));

        var (hits, truncated) = await client.SearchRemoteAsync("root", null, "IMG", 50, CancellationToken.None);
        Assert.Equal("Camera/IMG_2.jpg", Assert.Single(hits).RelativePath);
        Assert.False(truncated);

        var metadata = await client.GetMetadataRemoteAsync("root", "IMG_1.jpg", CancellationToken.None);
        Assert.Equal(1234, metadata.Size);

        var thumbnail = await client.GetThumbnailRemoteAsync("root", "IMG_1.jpg", 128, CancellationToken.None);
        Assert.Equal("AAAA", thumbnail);

        var subtree = await client.EnumerateRemoteSubtreeAsync("root", "Camera", null, CancellationToken.None);
        Assert.Equal("Camera/IMG_2.jpg", Assert.Single(subtree.Files).RelativePath);

        var (volumes, granted, _) = await client.ListVolumesAsync(CancellationToken.None);
        Assert.True(granted);
        Assert.Equal("tree:all", Assert.Single(volumes).Id);

        // Every request reached the phone with no client id stamped on it.
        Assert.All(socket.Messages, m => Assert.Null(m.ClientId));
    }

    /// <summary>The phone's file host, reduced to fixed answers for each relayed request.</summary>
    private static RemexMessage FakePhoneAnswer(RemexMessage request) => request.Type switch
    {
        MessageTypes.FileRootsRequest => new RemexMessage
        {
            Type = MessageTypes.FileRootsResponse,
            FileRootsResponse = new FileRootsResponse
            {
                Roots = [new FileSharedRoot { RootId = "root", DisplayName = "DCIM", IsWritable = true }],
                FileCapabilities = new FileCapabilities
                {
                    Protocol = 3, Binary = true, Resume = true, FullBrowse = true, Push = true,
                    Ops = ["search", "manifest"],
                },
            },
        },
        MessageTypes.FileBrowseRequest => BrowseReplyWithDirectory(request.FileBrowseRequest!.RequestId),
        MessageTypes.FileSearchRequest => new RemexMessage
        {
            Type = MessageTypes.FileSearchResponse,
            FileSearchResponse = new FileSearchResponse
            {
                RequestId = request.FileSearchRequest!.RequestId,
                Entries = [new FileSearchEntry { Name = "IMG_2.jpg", RelativePath = "Camera/IMG_2.jpg" }],
            },
        },
        MessageTypes.FileMetadataRequest => new RemexMessage
        {
            Type = MessageTypes.FileMetadataResponse,
            FileMetadataResponse = new FileMetadataResponse { RequestId = request.FileMetadataRequest!.RequestId, Size = 1234 },
        },
        MessageTypes.FileThumbnailRequest => new RemexMessage
        {
            Type = MessageTypes.FileThumbnailResponse,
            FileThumbnailResponse = new FileThumbnailResponse { RequestId = request.FileThumbnailRequest!.RequestId, JpegBase64 = "AAAA" },
        },
        MessageTypes.FileManifestRequest => new RemexMessage
        {
            Type = MessageTypes.FileManifestResponse,
            FileManifestResponse = new FileManifestResponse
            {
                RequestId = request.FileManifestRequest!.RequestId,
                RelativePath = "Camera",
                Entries = [new FileManifestEntry { RelativePath = "Camera/IMG_2.jpg", SizeBytes = 5 }],
            },
        },
        MessageTypes.FileVolumesRequest => new RemexMessage
        {
            Type = MessageTypes.FileVolumesResponse,
            FileVolumesResponse = new FileVolumesResponse
            {
                RequestId = request.FileVolumesRequest!.RequestId,
                FullBrowseGranted = true,
                Volumes = [new FileVolumeInfo { Id = "tree:all", Label = "Device", Path = "tree:all", Kind = "root" }],
            },
        },
        _ => throw new InvalidOperationException($"the fake phone does not answer {request.Type}"),
    };

    private static RemexMessage BrowseReplyWithDirectory(string requestId) => new()
    {
        Type = MessageTypes.FileBrowseResponse,
        FileBrowseResponse = new FileBrowseResponse
        {
            RequestId = requestId,
            Entries =
            [
                new FileEntry { Name = "IMG_1.jpg", IsDirectory = false, SizeBytes = 1234 },
                new FileEntry { Name = "Camera", IsDirectory = true },
            ],
        },
    };
}
