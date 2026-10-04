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
        // file_manage_request is NOT here any more (RemEx-fgmne): it is relayed to a phone that allows
        // changes. Its own refusals are pinned by the "manage" tests below. An allowlisted TYPE with no
        // body is still refused, whichever type it is.
        new RemexMessage { Type = MessageTypes.FileManageRequest },
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

    // ─── Rule 5: managing files on the phone (RemEx-fgmne) ───────────────────────

    private static RemexMessage RootsRequest() => new() { Type = MessageTypes.FileRootsRequest };

    private static RemexMessage RootsReply(bool pcChanges, bool withCapabilities = true) => new()
    {
        Type = MessageTypes.FileRootsResponse,
        FileRootsResponse = new FileRootsResponse
        {
            Roots = [new FileSharedRoot { RootId = "root", DisplayName = "DCIM", IsWritable = true }],
            FileCapabilities = withCapabilities
                ? new FileCapabilities
                {
                    Protocol = 3,
                    Ops = ["delete", "rename", "copy", "move", "mkdir"],
                    PcChanges = pcChanges,
                }
                : null,
        },
    };

    private static RemexMessage ManageRequest(
        string requestId, string operation, string relativePath = "DCIM/a.jpg",
        string? newName = null, string? destinationPath = null, string rootId = "root") => new()
    {
        Type = MessageTypes.FileManageRequest,
        FileManageRequest = new FileManageRequest
        {
            RequestId = requestId,
            RootId = rootId,
            RelativePath = relativePath,
            Operation = operation,
            NewName = newName,
            DestinationPath = destinationPath,
        },
    };

    private static RemexMessage ManageReply(string requestId, bool success, string? error = null) => new()
    {
        Type = MessageTypes.FileManageResponse,
        FileManageResponse = new FileManageResponse { RequestId = requestId, Success = success, ErrorMessage = error },
    };

    /// <summary>The five operations, each well-formed.</summary>
    public static TheoryData<string> ManageOperationNames() =>
        [
            FileManageOperations.Delete, FileManageOperations.Rename, FileManageOperations.Copy,
            FileManageOperations.Move, FileManageOperations.Mkdir,
        ];

    private static RemexMessage WellFormed(string operation) => operation switch
    {
        FileManageOperations.Rename => ManageRequest("m", operation, newName: "b.jpg"),
        FileManageOperations.Mkdir => ManageRequest("m", operation, relativePath: "DCIM", newName: "Trip"),
        FileManageOperations.Copy or FileManageOperations.Move => ManageRequest("m", operation, destinationPath: "DCIM/Trip/a.jpg"),
        _ => ManageRequest("m", operation),
    };

    /// <summary>Delivers the phone's own roots reply, which is the only thing that can turn manage on.</summary>
    private static async Task PhoneSaysAsync(PhoneRelayTestKit kit, IPhoneFileConnection connection, bool allowsChanges)
    {
        await connection.SendAsync(RootsRequest());
        Assert.True(kit.Relay.TryDeliverReply(PhoneA, identityProven: true, isLoopback: false, RootsReply(allowsChanges)));
    }

    [Theory]
    [MemberData(nameof(ManageOperationNames))]
    public async Task Manage_BeforeThePhoneHasSaidItAllowsChanges_IsRefusedAndNeverReachesThePhone(string operation)
    {
        using var kit = new PhoneRelayTestKit();
        var (socket, _) = kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);

        await Assert.ThrowsAsync<PhoneChangesNotAllowedException>(() => connection.SendAsync(WellFormed(operation)));

        Assert.Empty(socket.Messages);
    }

    [Theory]
    [MemberData(nameof(ManageOperationNames))]
    public async Task Manage_WhenThePhoneSwitchIsOff_IsRefusedAndNeverReachesThePhone(string operation)
    {
        using var kit = new PhoneRelayTestKit();
        var (socket, _) = kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        await PhoneSaysAsync(kit, connection, allowsChanges: false);

        await Assert.ThrowsAsync<PhoneChangesNotAllowedException>(() => connection.SendAsync(WellFormed(operation)));

        Assert.Empty(socket.MessagesOfType(MessageTypes.FileManageRequest));
    }

    [Fact]
    public async Task Manage_ToAPhoneThatPredatesTheSwitch_IsRefused()
    {
        // An older phone answers roots with no pcChanges field at all, or with no capabilities.
        using var kit = new PhoneRelayTestKit();
        var (socket, _) = kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        await connection.SendAsync(RootsRequest());
        Assert.True(kit.Relay.TryDeliverReply(PhoneA, true, false, RootsReply(pcChanges: true, withCapabilities: false)));

        await Assert.ThrowsAsync<PhoneChangesNotAllowedException>(() => connection.SendAsync(WellFormed(FileManageOperations.Delete)));

        Assert.Empty(socket.MessagesOfType(MessageTypes.FileManageRequest));
    }

    [Theory]
    [MemberData(nameof(ManageOperationNames))]
    public async Task Manage_AfterThePhoneSaidItAllowsChanges_EachOperationReachesThePhone_WithNoClientId(string operation)
    {
        using var kit = new PhoneRelayTestKit();
        var (socket, _) = kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        await PhoneSaysAsync(kit, connection, allowsChanges: true);

        var request = WellFormed(operation) with { ClientId = PhoneB };
        await connection.SendAsync(request);

        var sent = Assert.Single(socket.MessagesOfType(MessageTypes.FileManageRequest));
        Assert.Equal(operation, sent.FileManageRequest!.Operation);
        Assert.Null(sent.ClientId);
    }

    [Fact]
    public async Task Manage_WhenThePhoneLaterTurnsTheSwitchOff_IsRefusedAgainAfterTheNextRootsReply()
    {
        using var kit = new PhoneRelayTestKit();
        var (socket, _) = kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        await PhoneSaysAsync(kit, connection, allowsChanges: true);
        await connection.SendAsync(WellFormed(FileManageOperations.Mkdir));

        await PhoneSaysAsync(kit, connection, allowsChanges: false);

        await Assert.ThrowsAsync<PhoneChangesNotAllowedException>(() => connection.SendAsync(WellFormed(FileManageOperations.Mkdir)));
        Assert.Single(socket.MessagesOfType(MessageTypes.FileManageRequest));
    }

    [Fact]
    public async Task APlantedRootsReply_FromLoopbackOrAnUnprovenSession_CannotTurnManageOn()
    {
        using var kit = new PhoneRelayTestKit();
        var (socket, _) = kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        await connection.SendAsync(RootsRequest());

        Assert.False(kit.Relay.TryDeliverReply(PhoneA, identityProven: true, isLoopback: true, RootsReply(true)));
        Assert.False(kit.Relay.TryDeliverReply(PhoneA, identityProven: false, isLoopback: false, RootsReply(true)));

        await Assert.ThrowsAsync<PhoneChangesNotAllowedException>(() => connection.SendAsync(WellFormed(FileManageOperations.Delete)));
        Assert.Empty(socket.MessagesOfType(MessageTypes.FileManageRequest));
    }

    public static TheoryData<RemexMessage> UnsafeManageRequests() =>
    [
        ManageRequest("m", FileManageOperations.Rename, newName: "../x"),
        ManageRequest("m", FileManageOperations.Rename, newName: "a/b"),
        ManageRequest("m", FileManageOperations.Rename, newName: "a\\b"),
        ManageRequest("m", FileManageOperations.Rename, newName: ".."),
        ManageRequest("m", FileManageOperations.Rename, newName: new string('x', 256)),
        ManageRequest("m", FileManageOperations.Rename, newName: null),
        ManageRequest("m", FileManageOperations.Rename, relativePath: "", newName: "b.jpg"),
        ManageRequest("m", FileManageOperations.Mkdir, relativePath: "DCIM", newName: "a/b"),
        ManageRequest("m", FileManageOperations.Mkdir, relativePath: "DCIM", newName: null),
        ManageRequest("m", FileManageOperations.Mkdir, relativePath: "../DCIM", newName: "Trip"),
        ManageRequest("m", FileManageOperations.Delete, relativePath: "../outside.jpg"),
        ManageRequest("m", FileManageOperations.Delete, relativePath: "DCIM/../../x"),
        ManageRequest("m", FileManageOperations.Delete, relativePath: ""),
        ManageRequest("m", FileManageOperations.Delete, relativePath: "/"),
        ManageRequest("m", FileManageOperations.Move, destinationPath: "../escape.jpg"),
        ManageRequest("m", FileManageOperations.Copy, destinationPath: "DCIM\\escape.jpg"),
        ManageRequest("m", FileManageOperations.Copy, destinationPath: null),
        ManageRequest("m", FileManageOperations.Move, relativePath: "", destinationPath: "DCIM/x"),
        // A name the PC invents may not be invisible, direction-changing or over the byte cap.
        ManageRequest("m", FileManageOperations.Rename, newName: "evil‮fdp.exe"),
        ManageRequest("m", FileManageOperations.Mkdir, relativePath: "DCIM", newName: "a​b"),
        ManageRequest("m", FileManageOperations.Rename, newName: new string('é', 128)),
        ManageRequest("m", FileManageOperations.Move, destinationPath: "DCIM/x‮.jpg"),
        ManageRequest("m", FileManageOperations.Copy, destinationPath: "DCIM/" + new string('é', 128)),
        ManageRequest("m", "chmod"),
        ManageRequest("m", FileManageOperations.Delete, rootId: ""),
        ManageRequest("", FileManageOperations.Delete),
    ];

    [Theory]
    [MemberData(nameof(UnsafeManageRequests))]
    public async Task Manage_WithAnUnsafeNameOrPathOrOperation_IsRefusedEvenWhenThePhoneAllowsChanges(RemexMessage request)
    {
        using var kit = new PhoneRelayTestKit();
        var (socket, _) = kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        await PhoneSaysAsync(kit, connection, allowsChanges: true);

        await Assert.ThrowsAsync<PhoneFileRequestRefusedException>(() => connection.SendAsync(request));

        Assert.Empty(socket.MessagesOfType(MessageTypes.FileManageRequest));
    }

    [Fact]
    public async Task Delete_OfAFileWhoseExistingNameHoldsAJoiner_IsStillRelayed()
    {
        // The stricter new-name rule must not strand a file that already has U+200D in its name.
        using var kit = new PhoneRelayTestKit();
        var (socket, _) = kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        await PhoneSaysAsync(kit, connection, allowsChanges: true);

        await connection.SendAsync(ManageRequest("m", FileManageOperations.Delete, relativePath: "DCIM/family‍.jpg"));

        Assert.Single(socket.MessagesOfType(MessageTypes.FileManageRequest));
    }

    [Fact]
    public async Task RootManagement_StaysRefused_EvenWhenThePhoneAllowsChanges()
    {
        // WHICH FOLDERS A PHONE SHARES IS ONLY EVER THE PHONE'S DECISION.
        using var kit = new PhoneRelayTestKit();
        var (socket, _) = kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        await PhoneSaysAsync(kit, connection, allowsChanges: true);

        await Assert.ThrowsAsync<PhoneFileRequestRefusedException>(() => connection.SendAsync(new RemexMessage
        {
            Type = MessageTypes.FileRootManageRequest,
            FileRootManageRequest = new FileRootManageRequest { RequestId = "rm", Operation = "remove", RootId = "root" },
        }));

        Assert.Empty(socket.MessagesOfType(MessageTypes.FileRootManageRequest));
    }

    [Fact]
    public void TheAllowlist_HoldsTheSevenReadOnlyTypesAndManage_AndNotRootManagement()
    {
        Assert.Equal(8, PhoneFileRelay.RelayedRequests.Count);
        IEnumerable<string> allowlist = PhoneFileRelay.RelayedRequests;
        Assert.Contains(MessageTypes.FileManageRequest, allowlist);
        Assert.DoesNotContain(MessageTypes.FileRootManageRequest, allowlist);
        Assert.DoesNotContain(MessageTypes.FileHashRequest, allowlist);
    }

    [Fact]
    public async Task ManageReply_FromThePhoneThatWasAsked_IsDeliveredAsTheyWroteIt_IncludingARefusal()
    {
        using var kit = new PhoneRelayTestKit();
        kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        var received = new List<RemexMessage>();
        connection.FileTransferMessageReceived += received.Add;
        await PhoneSaysAsync(kit, connection, allowsChanges: true);
        received.Clear();
        await connection.SendAsync(ManageRequest("m1", FileManageOperations.Delete));

        Assert.True(kit.Relay.TryDeliverReply(PhoneA, true, false, ManageReply("m1", success: false, "A file with that name already exists.")));

        var reply = Assert.Single(received).FileManageResponse!;
        Assert.False(reply.Success);
        Assert.Equal("A file with that name already exists.", reply.ErrorMessage);
    }

    [Fact]
    public async Task ManageReply_FromLoopback_AnUnprovenSession_OrADifferentPhone_IsDropped()
    {
        using var kit = new PhoneRelayTestKit();
        kit.ConnectProvenPhone(PhoneA);
        kit.ConnectProvenPhone(PhoneB);
        using var connection = kit.Relay.Open(PhoneA);
        var received = new List<RemexMessage>();
        connection.FileTransferMessageReceived += received.Add;
        await PhoneSaysAsync(kit, connection, allowsChanges: true);
        received.Clear();
        await connection.SendAsync(ManageRequest("m1", FileManageOperations.Delete));

        Assert.False(kit.Relay.TryDeliverReply(PhoneA, identityProven: true, isLoopback: true, ManageReply("m1", true)));
        Assert.False(kit.Relay.TryDeliverReply(PhoneA, identityProven: false, isLoopback: false, ManageReply("m1", true)));
        Assert.False(kit.Relay.TryDeliverReply(PhoneB, identityProven: true, isLoopback: false, ManageReply("m1", true)));

        Assert.Empty(received);

        // The genuine answer still lands: none of the impostors consumed the request.
        Assert.True(kit.Relay.TryDeliverReply(PhoneA, true, false, ManageReply("m1", true)));
        Assert.Single(received);
    }

    [Fact]
    public void ManageReply_ToARequestNobodySent_IsDropped()
    {
        using var kit = new PhoneRelayTestKit();
        kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        var received = new List<RemexMessage>();
        connection.FileTransferMessageReceived += received.Add;

        Assert.False(kit.Relay.TryDeliverReply(PhoneA, true, false, ManageReply("never-asked", true)));

        Assert.Empty(received);
    }

    [Fact]
    public async Task ACopyOutlivesTheShortRequestTimeout_ButADeleteDoesNot()
    {
        // Copy and move stream the whole file on the phone before it answers. Failing them at the
        // 90-second backstop would report a healthy large copy as lost while it was still running.
        using var kit = new PhoneRelayTestKit(requestTimeout: TimeSpan.FromMilliseconds(120), copyMoveTimeout: TimeSpan.FromSeconds(30));
        kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        var received = new List<RemexMessage>();
        connection.FileTransferMessageReceived += received.Add;
        await PhoneSaysAsync(kit, connection, allowsChanges: true);
        received.Clear();

        await connection.SendAsync(ManageRequest("del", FileManageOperations.Delete));
        await connection.SendAsync(ManageRequest("cp", FileManageOperations.Copy, destinationPath: "DCIM/copy.jpg"));
        await Task.Delay(600);

        var timedOut = Assert.Single(received).FileManageResponse!;
        Assert.Equal("del", timedOut.RequestId);
        Assert.False(timedOut.Success);
        Assert.True(kit.Relay.TryDeliverReply(PhoneA, true, false, ManageReply("cp", true)));
        Assert.Equal(2, received.Count);
        Assert.True(received[1].FileManageResponse!.Success);
    }

    [Fact]
    public async Task APhoneThatLeavesMidManage_FailsTheRequestRatherThanLeavingItHanging()
    {
        using var kit = new PhoneRelayTestKit();
        var (_, session) = kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        var received = new List<RemexMessage>();
        connection.FileTransferMessageReceived += received.Add;
        await PhoneSaysAsync(kit, connection, allowsChanges: true);
        received.Clear();
        await connection.SendAsync(ManageRequest("m1", FileManageOperations.Delete));

        session.Dispose();

        var failure = Assert.Single(received).FileManageResponse!;
        Assert.Equal("m1", failure.RequestId);
        Assert.False(failure.Success);
        Assert.False(string.IsNullOrWhiteSpace(failure.ErrorMessage));
    }

    [Fact]
    public async Task TheRealClient_ManagesAPhoneThatAllowsIt_AndIsRefusedByOneThatDoesNot()
    {
        using var kit = new PhoneRelayTestKit();
        var allows = true;
        var (socket, _) = kit.ConnectProvenPhone(PhoneA, "Pixel");
        socket.OnMessage = request => _ = Task.Run(() =>
        {
            var reply = request.Type switch
            {
                MessageTypes.FileRootsRequest => RootsReply(allows),
                MessageTypes.FileManageRequest => ManageReply(request.FileManageRequest!.RequestId, success: true),
                _ => null,
            };
            if (reply is not null)
                kit.Relay.TryDeliverReply(PhoneA, identityProven: true, isLoopback: false, reply);
        });

        using var connection = kit.Relay.Open(PhoneA);
        using var client = new FileTransferClient(connection);
        await client.ListRemoteRootsAsync(CancellationToken.None);
        Assert.True(client.SupportsPcChanges);

        await client.MakeDirectoryRemoteAsync("root", "DCIM", "Trip", CancellationToken.None);
        await client.RenameRemoteAsync("root", "DCIM/a.jpg", "b.jpg", CancellationToken.None);
        await client.DeleteRemoteAsync("root", "DCIM/b.jpg", CancellationToken.None);
        Assert.Equal(3, socket.MessagesOfType(MessageTypes.FileManageRequest).Count);

        allows = false;
        await client.ListRemoteRootsAsync(CancellationToken.None);
        Assert.False(client.SupportsPcChanges);
        await Assert.ThrowsAsync<PhoneChangesNotAllowedException>(
            () => client.DeleteRemoteAsync("root", "DCIM/b.jpg", CancellationToken.None));
        Assert.Equal(3, socket.MessagesOfType(MessageTypes.FileManageRequest).Count);
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
    public async Task Reply_StillInFlightWhenThePhoneWasUnpaired_IsDropped()
    {
        // The session proved its id before the unpair, and is still registered for a moment while it
        // is torn down. A reply it sends in that window is no longer a paired phone's answer.
        using var kit = new PhoneRelayTestKit();
        kit.ConnectProvenPhone(PhoneA);
        using var connection = kit.Relay.Open(PhoneA);
        var received = new List<RemexMessage>();
        connection.FileTransferMessageReceived += received.Add;
        await connection.SendAsync(BrowseRequest("r1"));

        kit.Paired.UnregisterClient(PhoneA);

        Assert.False(kit.Relay.TryDeliverReply(PhoneA, identityProven: true, isLoopback: false, BrowseReply("r1", "a.jpg")));
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

    // ─── Failure wording ─────────────────────────────────────────────────────────

    /// <summary>Every reason the PC itself gives, read off <see cref="PeerTransferFailure"/>.</summary>
    private static List<string> PcOwnReasonValues() =>
        [.. typeof(PeerTransferFailure)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => field.GetRawConstantValue())
            .OfType<string>()];

    public static TheoryData<string> PcOwnReasons()
    {
        var data = new TheoryData<string>();
        foreach (var value in PcOwnReasonValues())
            data.Add(value);
        return data;
    }

    [Fact]
    public void ThePcsOwnReasons_AreReadOffTheConstants()
    {
        // Anti-vacuity: an empty list would make the theory below pass by never running.
        Assert.True(PcOwnReasonValues().Count >= 20);
    }

    /// <summary>
    /// The PC's own English never reaches the screen: <see cref="FileTransferHostException"/> shows its
    /// message verbatim, so no constant may map to it. A new constant without a mapping fails here.
    /// </summary>
    [Theory]
    [MemberData(nameof(PcOwnReasons))]
    public void APcOwnReason_NeverBecomesAVerbatimHostMessage(string reason)
    {
        var failure = PhoneFileRelay.FailureFor(reason);

        Assert.IsNotType<FileTransferHostException>(failure);
    }

    [Theory]
    [InlineData(PeerTransferFailure.PhoneStopped, PhoneTransferProblem.PhoneStopped)]
    [InlineData(PeerTransferFailure.PhoneDeclined, PhoneTransferProblem.PhoneRefused)]
    [InlineData(PeerTransferFailure.PhoneDidNotAccept, PhoneTransferProblem.PhoneRefused)]
    [InlineData(PeerTransferFailure.TooLarge, PhoneTransferProblem.FileTooLarge)]
    [InlineData(PeerTransferFailure.LocalFileChanged, PhoneTransferProblem.FileChanged)]
    [InlineData(PeerTransferFailure.NameNotAllowed, PhoneTransferProblem.NameNotAllowed)]
    [InlineData(PeerTransferFailure.Failed, PhoneTransferProblem.Unknown)]
    public void APcOwnReason_MapsToItsDiagnosis(string reason, PhoneTransferProblem expected)
    {
        var failure = Assert.IsType<PhoneTransferFailedException>(PhoneFileRelay.FailureFor(reason));

        Assert.Equal(expected, failure.Problem);
    }

    [Fact]
    public void AReasonThePhoneWrote_IsShownAsItWroteIt()
    {
        var failure = Assert.IsType<FileTransferHostException>(
            PhoneFileRelay.FailureFor("Destination folder not found or read-only."));

        Assert.Equal("Destination folder not found or read-only.", failure.HostMessage);
    }
}
