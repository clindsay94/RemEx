using System.Security.Cryptography;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services.FileTransfer;

namespace Remex.Agent.Tests;

/// <summary>
/// PC-started transfers with a phone (RemEx-xt0af): the PC pulling a file out of a phone's shared
/// folder into a folder on this PC, and putting one into the phone's folder, through the real
/// <c>TransferSessionManager</c> and the real <c>/ws/files</c> channel loop against a fake phone.
/// </summary>
public sealed class PhoneTransferTests
{
    private const string PhoneA = "phone-a";
    private const string PhoneB = "phone-b";
    private const int FrameBytes = 64 * 1024;

    private static string Sha(byte[] bytes) => Convert.ToBase64String(SHA256.HashData(bytes));

    /// <summary>
    /// Plays the phone's side of a download it was asked to serve: ready, the frames, wait for the
    /// PC's final ack (the drain rule), then file_transfer_complete.
    /// </summary>
    private static async Task ServeDownloadAsync(
        PhoneRelayTestKit kit, FakePhoneSocket control, FakeChannelSocket channel, FileTransferOffer offer,
        byte[] payload, string? announcedSha = null, string? errorFrame = null)
    {
        var allAcked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.OnFrame = (envelope, _) =>
        {
            if (envelope.Kind == FileFrameKinds.Ack && envelope.CommittedOffset >= payload.Length)
                allAcked.TrySetResult();
        };

        kit.Transfers.HandleReady(new FileTransferReady { TransferId = offer.TransferId, Accepted = true }, PhoneA);

        if (errorFrame is not null)
        {
            channel.Deliver(new FileFrameEnvelope { Kind = FileFrameKinds.Error, TransferId = offer.TransferId, Error = errorFrame }, []);
            return;
        }

        for (var offset = 0; offset < payload.Length; offset += FrameBytes)
        {
            var length = Math.Min(FrameBytes, payload.Length - offset);
            channel.Deliver(
                new FileFrameEnvelope
                {
                    Kind = FileFrameKinds.Data,
                    TransferId = offer.TransferId,
                    Offset = offset,
                    Length = length,
                    Final = offset + length >= payload.Length,
                },
                payload.AsSpan(offset, length));
        }

        await allAcked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await kit.Transfers.HandleCompleteAsync(
            new FileTransferComplete { TransferId = offer.TransferId, Sha256Base64 = announcedSha ?? Sha(payload) },
            control, isLoopback: false, PhoneA, CancellationToken.None);
    }

    private static (FakePhoneSocket Control, FakeChannelSocket Channel, Task Loop) StartPhone(
        PhoneRelayTestKit kit, CancellationToken ct)
    {
        var (control, _) = kit.ConnectProvenPhone(PhoneA, "Pixel");
        var channel = new FakeChannelSocket();
        var loop = kit.Transfers.RunChannelAsync(PhoneA, channel, ct);
        return (control, channel, loop);
    }

    // ─── Pull: phone → a folder on this PC ───────────────────────────────────────

    [Fact]
    public async Task Download_LandsTheVerifiedFileAtTheChosenLocalPath()
    {
        using var kit = new PhoneRelayTestKit();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (control, channel, _) = StartPhone(kit, cts.Token);
        var payload = RandomNumberGenerator.GetBytes(300_000);
        FileTransferOffer? seen = null;
        control.OnMessage = message =>
        {
            if (message.FileTransferOffer is { } offer)
            {
                seen = offer;
                _ = Task.Run(() => ServeDownloadAsync(kit, control, channel, offer, payload));
            }
        };
        var destination = Path.Combine(kit.LocalFolder, "IMG_1.jpg");
        var reported = new List<long>();

        await kit.Relay.DownloadAsync(
            PhoneA, "root", "DCIM/IMG_1.jpg", destination,
            new SynchronousProgress(p => reported.Add(p.BytesTransferred)), cts.Token);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));

        // Asked the phone for its OWN existing download: the source folder and the name, separately.
        Assert.NotNull(seen);
        Assert.Equal(FileTransferModes.Download, seen!.Mode);
        Assert.Equal("root", seen.DestRoot);
        Assert.Equal("DCIM", seen.DestRelativePath);
        Assert.Equal("IMG_1.jpg", seen.FileName);
        Assert.Contains(payload.Length, reported.Select(r => (int)r));
    }

    [Fact]
    public async Task Download_OfAFolder_PullsEachFileIntoItsOwnPlace()
    {
        using var kit = new PhoneRelayTestKit();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (control, channel, _) = StartPhone(kit, cts.Token);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["a.jpg"] = RandomNumberGenerator.GetBytes(10_000),
            ["b.jpg"] = RandomNumberGenerator.GetBytes(70_000),
        };
        control.OnMessage = message =>
        {
            if (message.FileTransferOffer is { } offer)
                _ = Task.Run(() => ServeDownloadAsync(kit, control, channel, offer, files[offer.FileName]));
        };
        var folder = Directory.CreateDirectory(Path.Combine(kit.LocalFolder, "Camera")).FullName;

        foreach (var name in files.Keys)
            await kit.Relay.DownloadAsync(PhoneA, "root", "Camera/" + name, Path.Combine(folder, name), null, cts.Token);

        foreach (var (name, bytes) in files)
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(folder, name)));
    }

    /// <summary>
    /// The phone has sent everything and said so; the PC's copy into a destination on another drive
    /// then takes far longer than the idle window with no bytes moving. That is the PC being slow, not
    /// the phone going quiet, so the pull must succeed rather than report "stopped responding".
    /// </summary>
    [Fact]
    public async Task Download_ASlowLandingAfterThePhoneFinished_IsNotReportedAsThePhoneStoppingResponding()
    {
        var promoter = new SlowPromoter(TimeSpan.FromMilliseconds(1500));
        using var kit = new PhoneRelayTestKit(
            configure: o => o.PeerIdleTimeout = TimeSpan.FromMilliseconds(300), promoter: promoter);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (control, channel, _) = StartPhone(kit, cts.Token);
        var payload = RandomNumberGenerator.GetBytes(80_000);
        control.OnMessage = message =>
        {
            if (message.FileTransferOffer is { } offer)
                _ = Task.Run(() => ServeDownloadAsync(kit, control, channel, offer, payload));
        };
        var destination = Path.Combine(kit.LocalFolder, "slow.jpg");

        await kit.Relay.DownloadAsync(PhoneA, "root", "slow.jpg", destination, null, cts.Token);

        Assert.True(promoter.Finished);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    /// <summary>A promoter that lands the file only after a delay, like a copy onto a slow drive.</summary>
    private sealed class SlowPromoter(TimeSpan delay) : Remex.Agent.Services.FileTransfer.IStagedFilePromoter
    {
        private volatile bool _finished;

        public bool Finished => _finished;

        public async Task PromoteStagedFileToPathAsync(string stagingPath, string destination, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            File.Move(stagingPath, destination, overwrite: true);
            _finished = true;
        }
    }

    [Fact]
    public async Task Download_ThePhoneDeclines_SurfacesItsReason_AndLeavesNothingBehind()
    {
        using var kit = new PhoneRelayTestKit();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (control, _, _) = StartPhone(kit, cts.Token);
        control.OnMessage = message =>
        {
            if (message.FileTransferOffer is { } offer)
            {
                _ = Task.Run(() => kit.Transfers.HandleReady(
                    new FileTransferReady
                    {
                        TransferId = offer.TransferId,
                        Accepted = false,
                        DeclineReason = "Source file not found or access denied.",
                    },
                    PhoneA));
            }
        };
        var destination = Path.Combine(kit.LocalFolder, "nope.jpg");

        var refusal = await Assert.ThrowsAsync<FileTransferHostException>(() =>
            kit.Relay.DownloadAsync(PhoneA, "root", "nope.jpg", destination, null, cts.Token));

        Assert.Equal("Source file not found or access denied.", refusal.HostMessage);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Download_ThePhoneSendsAnErrorFrame_FailsWithThePhonesReason()
    {
        using var kit = new PhoneRelayTestKit();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (control, channel, _) = StartPhone(kit, cts.Token);
        control.OnMessage = message =>
        {
            if (message.FileTransferOffer is { } offer)
                _ = Task.Run(() => ServeDownloadAsync(kit, control, channel, offer, [], errorFrame: "Cannot open source."));
        };

        var refusal = await Assert.ThrowsAsync<FileTransferHostException>(() =>
            kit.Relay.DownloadAsync(PhoneA, "root", "x.jpg", Path.Combine(kit.LocalFolder, "x.jpg"), null, cts.Token));

        Assert.Equal("Cannot open source.", refusal.HostMessage);
    }

    [Fact]
    public async Task Download_WithAHashThatDoesNotMatch_IsDeletedNotDelivered()
    {
        using var kit = new PhoneRelayTestKit();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (control, channel, _) = StartPhone(kit, cts.Token);
        var payload = RandomNumberGenerator.GetBytes(5_000);
        control.OnMessage = message =>
        {
            if (message.FileTransferOffer is { } offer)
                _ = Task.Run(() => ServeDownloadAsync(kit, control, channel, offer, payload, announcedSha: Sha([1, 2, 3])));
        };
        var destination = Path.Combine(kit.LocalFolder, "bad.jpg");

        var failure = await Assert.ThrowsAsync<FileTransferIntegrityException>(() =>
            kit.Relay.DownloadAsync(PhoneA, "root", "bad.jpg", destination, null, cts.Token));

        Assert.True(failure.FromPhone);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Download_ACompletionFromAnotherPhone_DoesNotFinishIt()
    {
        using var kit = new PhoneRelayTestKit();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (control, channel, _) = StartPhone(kit, cts.Token);
        var (otherControl, _) = kit.ConnectProvenPhone(PhoneB);
        var payload = RandomNumberGenerator.GetBytes(5_000);
        control.OnMessage = message =>
        {
            if (message.FileTransferOffer is not { } offer)
                return;

            _ = Task.Run(async () =>
            {
                // Phone B names A's transfer id with a hash of its own choosing, before A finishes.
                await kit.Transfers.HandleCompleteAsync(
                    new FileTransferComplete { TransferId = offer.TransferId, Sha256Base64 = Sha([9]) },
                    otherControl, isLoopback: false, PhoneB, CancellationToken.None);
                await ServeDownloadAsync(kit, control, channel, offer, payload);
            });
        };
        var destination = Path.Combine(kit.LocalFolder, "a.jpg");

        await kit.Relay.DownloadAsync(PhoneA, "root", "a.jpg", destination, null, cts.Token);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task Download_FromAPhoneThatIsNotConnected_ThrowsPhoneNotConnected()
    {
        using var kit = new PhoneRelayTestKit();
        kit.Paired.RegisterClient(PhoneA, new byte[32]);

        await Assert.ThrowsAsync<PhoneNotConnectedException>(() =>
            kit.Relay.DownloadAsync(PhoneA, "root", "a.jpg", Path.Combine(kit.LocalFolder, "a.jpg"), null, CancellationToken.None));
    }

    [Fact]
    public async Task Download_WhenThePhonesChannelDrops_FailsAsDisconnected()
    {
        using var kit = new PhoneRelayTestKit();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (control, channel, _) = StartPhone(kit, cts.Token);
        control.OnMessage = message =>
        {
            if (message.FileTransferOffer is { } offer)
            {
                _ = Task.Run(() =>
                {
                    kit.Transfers.HandleReady(new FileTransferReady { TransferId = offer.TransferId, Accepted = true }, PhoneA);
                    channel.Drop();
                });
            }
        };

        await Assert.ThrowsAsync<PhoneNotConnectedException>(() =>
            kit.Relay.DownloadAsync(PhoneA, "root", "a.jpg", Path.Combine(kit.LocalFolder, "a.jpg"), null, cts.Token));
    }

    // ─── Upload: a file on this PC → the phone's shared folder ───────────────────

    /// <summary>Plays the phone receiving an upload: ready, ack the final frame, verify, answer.</summary>
    private static void PlayPhoneReceiving(
        PhoneRelayTestKit kit, FakePhoneSocket control, FakeChannelSocket channel, List<byte> received,
        Action<FileTransferOffer>? onOffer = null, bool impostorVerdictFirst = false)
    {
        channel.OnFrame = (envelope, payload) =>
        {
            if (envelope.Kind != FileFrameKinds.Data) return;
            lock (received) received.AddRange(payload);
            if (envelope.Final)
            {
                channel.Deliver(
                    new FileFrameEnvelope
                    {
                        Kind = FileFrameKinds.Ack,
                        TransferId = envelope.TransferId,
                        CommittedOffset = envelope.Offset + envelope.Length,
                    },
                    []);
            }
        };

        control.OnMessage = message =>
        {
            if (message.FileTransferOffer is { } offer)
            {
                onOffer?.Invoke(offer);
                _ = Task.Run(() => kit.Transfers.HandleReady(
                    new FileTransferReady { TransferId = offer.TransferId, Accepted = true }, PhoneA));
            }
            else if (message.FileTransferComplete is { } complete)
            {
                _ = Task.Run(() =>
                {
                    byte[] bytes;
                    lock (received) bytes = [.. received];
                    if (impostorVerdictFirst)
                    {
                        kit.Transfers.HandleResult(
                            new FileTransferResult { TransferId = complete.TransferId, Verified = false, Error = "planted" },
                            PhoneB);
                    }

                    kit.Transfers.HandleResult(
                        new FileTransferResult
                        {
                            TransferId = complete.TransferId,
                            Verified = string.Equals(Sha(bytes), complete.Sha256Base64, StringComparison.Ordinal),
                            Sha256Base64 = Sha(bytes),
                        },
                        PhoneA);
                });
            }
        };
    }

    [Fact]
    public async Task Upload_PutsTheFileIntoTheChosenPhoneFolder_AndWaitsForThePhonesVerdict()
    {
        using var kit = new PhoneRelayTestKit();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (control, channel, _) = StartPhone(kit, cts.Token);
        var received = new List<byte>();
        FileTransferOffer? seen = null;
        PlayPhoneReceiving(kit, control, channel, received, offer => seen = offer);
        var payload = RandomNumberGenerator.GetBytes(400_000);
        var localPath = Path.Combine(kit.LocalFolder, "holiday.mp4");
        await File.WriteAllBytesAsync(localPath, payload);

        await kit.Relay.UploadAsync(PhoneA, localPath, "root", "DCIM/Camera", null, cts.Token);

        lock (received) Assert.Equal(payload, received.ToArray());
        Assert.NotNull(seen);
        Assert.Equal(FileTransferModes.Upload, seen!.Mode);
        Assert.Equal("root", seen.DestRoot);
        Assert.Equal("DCIM/Camera", seen.DestRelativePath);
        Assert.Equal("holiday.mp4", seen.FileName);
        Assert.Equal(payload.Length, seen.Size);
    }

    [Fact]
    public async Task Upload_AVerdictFromAnotherPhone_IsIgnored()
    {
        using var kit = new PhoneRelayTestKit();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (control, channel, _) = StartPhone(kit, cts.Token);
        kit.ConnectProvenPhone(PhoneB);
        var received = new List<byte>();
        PlayPhoneReceiving(kit, control, channel, received, impostorVerdictFirst: true);
        var localPath = Path.Combine(kit.LocalFolder, "a.txt");
        await File.WriteAllBytesAsync(localPath, RandomNumberGenerator.GetBytes(1_000));

        // Completes without throwing: phone B's "verified: false" did not decide phone A's upload.
        await kit.Relay.UploadAsync(PhoneA, localPath, "root", string.Empty, null, cts.Token);
    }

    [Fact]
    public async Task Upload_ThePhoneDeclines_SurfacesItsReason()
    {
        using var kit = new PhoneRelayTestKit();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (control, _, _) = StartPhone(kit, cts.Token);
        control.OnMessage = message =>
        {
            if (message.FileTransferOffer is { } offer)
            {
                _ = Task.Run(() => kit.Transfers.HandleReady(
                    new FileTransferReady
                    {
                        TransferId = offer.TransferId,
                        Accepted = false,
                        DeclineReason = "Destination folder not found or read-only.",
                    },
                    PhoneA));
            }
        };
        var localPath = Path.Combine(kit.LocalFolder, "a.txt");
        await File.WriteAllBytesAsync(localPath, [1, 2, 3]);

        var refusal = await Assert.ThrowsAsync<FileTransferHostException>(() =>
            kit.Relay.UploadAsync(PhoneA, localPath, "root", "ReadOnly", null, cts.Token));

        Assert.Equal("Destination folder not found or read-only.", refusal.HostMessage);
    }

    private sealed class SynchronousProgress(Action<Remex.Desktop.ViewModels.TransferProgress> report)
        : IProgress<Remex.Desktop.ViewModels.TransferProgress>
    {
        public void Report(Remex.Desktop.ViewModels.TransferProgress value) => report(value);
    }
}
