using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The File Transfer screen's Source picker (RemEx-xt0af): This PC, or a paired phone that is
/// connected, browsed read-only through the host's relay.
/// </summary>
public sealed class FileTransferPhoneSourceTests : IDisposable
{
    private const string PhoneId = "phone-a";
    private readonly DirectoryInfo _local = Directory.CreateTempSubdirectory("remex-phone-source-");

    public void Dispose()
    {
        try { _local.Delete(recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private static readonly FileSharedRoot PhoneRoot = new()
    {
        RootId = "content://tree/DCIM",
        DisplayName = "DCIM",
        IsWritable = true,
        CanRename = true,
        CanDelete = true,
        CanMove = true,
    };

    /// <summary>The host's relay, reduced to one phone that answers roots, browse and volumes.</summary>
    private sealed class FakePhoneAccess : IPhoneFileAccess
    {
        public List<PhoneFileSource> Phones { get; } = [new(PhoneId, "Pixel 9")];
        public FakePhoneConnection? Last { get; private set; }
        public bool FullBrowse { get; init; } = true;

        public event Action? AvailabilityChanged;

        public IReadOnlyList<PhoneFileSource> AvailablePhones() => [.. Phones];

        public IPhoneFileConnection Open(string clientId)
        {
            if (Phones.All(p => p.ClientId != clientId))
                throw new PhoneNotConnectedException();
            return Last = new FakePhoneConnection(clientId, FullBrowse);
        }

        public void RaiseAvailabilityChanged() => AvailabilityChanged?.Invoke();
    }

    private sealed class FakePhoneConnection(string clientId, bool fullBrowse) : IPhoneFileConnection
    {
        public ConcurrentQueue<string> SentTypes { get; } = new();
        public string ClientId { get; } = clientId;
        public bool IsConnected { get; private set; } = true;
        public bool Disposed { get; private set; }

        public event Action<RemexMessage>? FileTransferMessageReceived;
        public event Action? Disconnected;

        public Task SendAsync(RemexMessage message)
        {
            SentTypes.Enqueue(message.Type);
            RemexMessage? reply = message.Type switch
            {
                MessageTypes.FileRootsRequest => new RemexMessage
                {
                    Type = MessageTypes.FileRootsResponse,
                    FileRootsResponse = new FileRootsResponse
                    {
                        Roots = [PhoneRoot],
                        FileCapabilities = new FileCapabilities
                        {
                            Protocol = 3,
                            Binary = true,
                            FullBrowse = fullBrowse,
                            // The phone advertises management ops; the PC must still not offer them.
                            Ops = ["delete", "rename", "copy", "move", "mkdir", "search", "manifest"],
                        },
                    },
                },
                MessageTypes.FileBrowseRequest => new RemexMessage
                {
                    Type = MessageTypes.FileBrowseResponse,
                    FileBrowseResponse = new FileBrowseResponse
                    {
                        RequestId = message.FileBrowseRequest!.RequestId,
                        Entries = [new FileEntry { Name = "IMG_1.jpg", IsDirectory = false, SizeBytes = 5 }],
                    },
                },
                MessageTypes.FileVolumesRequest => new RemexMessage
                {
                    Type = MessageTypes.FileVolumesResponse,
                    FileVolumesResponse = new FileVolumesResponse
                    {
                        RequestId = message.FileVolumesRequest!.RequestId,
                        FullBrowseGranted = fullBrowse,
                        Volumes = fullBrowse
                            ? [new FileVolumeInfo { Id = "content://tree/primary", Label = "Phone storage", Path = "primary", Kind = "root" }]
                            : [],
                    },
                },
                _ => null,
            };

            if (reply is not null)
                FileTransferMessageReceived?.Invoke(reply);
            return Task.CompletedTask;
        }

        public void Disconnect()
        {
            IsConnected = false;
            Disconnected?.Invoke();
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakePhoneTransfers : IPhoneFileTransfers
    {
        public ConcurrentQueue<(string ClientId, string RootId, string Remote, string Local)> Downloads { get; } = new();
        public ConcurrentQueue<(string ClientId, string Local, string RootId, string RemoteDirectory)> Uploads { get; } = new();

        public Task DownloadAsync(string clientId, string rootId, string remoteRelativePath, string localPath,
            IProgress<TransferProgress>? progress, CancellationToken ct)
        {
            Downloads.Enqueue((clientId, rootId, remoteRelativePath, localPath));
            return Task.CompletedTask;
        }

        public Task UploadAsync(string clientId, string localPath, string rootId, string remoteDirectory,
            IProgress<TransferProgress>? progress, CancellationToken ct)
        {
            Uploads.Enqueue((clientId, localPath, rootId, remoteDirectory));
            return Task.CompletedTask;
        }
    }

    private static FileTransferViewModel NewViewModel(FakePhoneAccess access, FakePhoneTransfers? transfers = null)
    {
        var vm = new FileTransferViewModel(
            new ConnectionViewModel(),
            transferQueue: new FileTransferQueue(post: null),
            phoneAccess: () => access,
            phoneTransfers: () => transfers);
        vm.PostToUi = work => work();
        return vm;
    }

    private static async Task SelectPhoneAsync(FileTransferViewModel vm)
    {
        vm.SelectedSource = vm.Sources.Single(s => s.ClientId == PhoneId);
        await WaitForAsync(() => vm.SelectedRemoteRoot is not null && !vm.IsLoading, "the phone's roots should load");
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++)
            await Task.Delay(10);

        condition().Should().BeTrue(because);
    }

    [Fact]
    public void Sources_ListThisPcFirst_ThenEachConnectedPhone()
    {
        using var vm = NewViewModel(new FakePhoneAccess());

        vm.Sources.Select(s => s.ClientId).Should().Equal(null, PhoneId);
        vm.Sources[0].IsThisPc.Should().BeTrue();
        vm.Sources[1].DeviceName.Should().Be("Pixel 9");
        vm.SelectedSource!.IsThisPc.Should().BeTrue();
        vm.HasPhoneSources.Should().BeTrue();
    }

    [Fact]
    public void Sources_FollowPhonesComingAndGoing()
    {
        var access = new FakePhoneAccess();
        using var vm = NewViewModel(access);

        access.Phones.Add(new PhoneFileSource("phone-b", null));
        access.RaiseAvailabilityChanged();

        vm.Sources.Should().HaveCount(3);
        vm.Sources.Single(s => s.ClientId == "phone-b").IsUnnamedPhone.Should().BeTrue();

        access.Phones.Clear();
        access.RaiseAvailabilityChanged();

        vm.Sources.Should().ContainSingle().Which.IsThisPc.Should().BeTrue();
        vm.HasPhoneSources.Should().BeFalse();
    }

    [Fact]
    public async Task SelectingAPhone_BrowsesItThroughTheRelay()
    {
        var access = new FakePhoneAccess();
        using var vm = NewViewModel(access);

        await SelectPhoneAsync(vm);
        await WaitForAsync(() => vm.RemoteEntries.Count > 0, "the phone's folder should list");

        vm.IsPhoneSource.Should().BeTrue();
        vm.RemoteRoots.Should().ContainSingle().Which.RootId.Should().Be(PhoneRoot.RootId);
        vm.RemoteEntries.Select(e => e.Name).Should().Contain("IMG_1.jpg");
        access.Last!.SentTypes.Should().Contain([MessageTypes.FileRootsRequest, MessageTypes.FileBrowseRequest]);
    }

    [Fact]
    public async Task APhone_IsBrowsedNotManaged_EvenThoughItAdvertisesManagementOps()
    {
        using var vm = NewViewModel(new FakePhoneAccess());
        await SelectPhoneAsync(vm);
        vm.SelectedRemoteEntry = new FileEntry { Name = "IMG_1.jpg", IsDirectory = false };
        vm.SetSelectedEntries([vm.SelectedRemoteEntry]);

        vm.CanManageFiles.Should().BeFalse();
        vm.SupportsCopyMove.Should().BeFalse();
        vm.SupportsMkdir.Should().BeFalse();
        vm.DeleteRemoteCommand.CanExecute(null).Should().BeFalse();
        vm.StartRenameCommand.CanExecute(null).Should().BeFalse();
        vm.VerifyHashCommand.CanExecute(null).Should().BeFalse();
        vm.UploadFolderCommand.CanExecute(null).Should().BeFalse();

        // What a phone source DOES offer.
        vm.SupportsSearch.Should().BeTrue();
        vm.SupportsFolderTransfer.Should().BeTrue();
        vm.DownloadCommand.CanExecute(null).Should().BeTrue();
        vm.UploadCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void ThisPc_NeverOffersTheWholeDeviceButton()
    {
        // RemEx-xt0af's original complaint: on This PC the button asked the PC for its own drives over
        // loopback and got "A paired client identity is required to browse volumes". It is gone there.
        using var vm = NewViewModel(new FakePhoneAccess());

        vm.SupportsFullBrowse = true;

        vm.ShowVolumesButton.Should().BeFalse();
        vm.LoadVolumesCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task APhoneWithWholeDeviceBrowsingOn_OffersIt_AndItsStorageBecomesBrowsable()
    {
        using var vm = NewViewModel(new FakePhoneAccess { FullBrowse = true });
        await SelectPhoneAsync(vm);

        vm.ShowVolumesButton.Should().BeTrue();
        await vm.LoadVolumesCommand.ExecuteAsync(null);

        vm.RemoteRoots.Select(r => r.RootId).Should().Contain("content://tree/primary");
        vm.RemoteRoots.Single(r => r.RootId == "content://tree/primary").IsWritable.Should().BeFalse();
    }

    [Fact]
    public async Task APhoneWithWholeDeviceBrowsingOff_DoesNotOfferIt()
    {
        using var vm = NewViewModel(new FakePhoneAccess { FullBrowse = false });
        await SelectPhoneAsync(vm);

        vm.ShowVolumesButton.Should().BeFalse();
    }

    [Fact]
    public async Task ThePhoneDisconnecting_ReturnsToThisPc_AndSaysWhy()
    {
        var access = new FakePhoneAccess();
        using var vm = NewViewModel(access);
        await SelectPhoneAsync(vm);
        var connection = access.Last!;

        access.Phones.Clear();
        connection.Disconnect();

        vm.IsPhoneSource.Should().BeFalse();
        vm.SelectedSource!.IsThisPc.Should().BeTrue();
        connection.Disposed.Should().BeTrue();
        vm.StatusText.Should().Be(LocalizationService.Instance["FileTransfer_PhoneDisconnected"]);
    }

    [Fact]
    public void SelectingAPhoneThatLeftInTheMeantime_StaysOnThisPc()
    {
        var access = new FakePhoneAccess();
        using var vm = NewViewModel(access);
        var phone = vm.Sources.Single(s => s.ClientId == PhoneId);
        access.Phones.Clear();

        vm.SelectedSource = phone;

        vm.IsPhoneSource.Should().BeFalse();
        vm.StatusText.Should().Be(LocalizationService.Instance["FileTransfer_PhoneNotConnected"]);
    }

    [Fact]
    public async Task Downloading_FromAPhone_GoesThroughTheHostTransferEngine()
    {
        var transfers = new FakePhoneTransfers();
        using var vm = NewViewModel(new FakePhoneAccess(), transfers);
        await SelectPhoneAsync(vm);

        var subtree = new RemoteSubtree
        {
            BasePath = "Camera",
            Entries =
            [
                new FileManifestEntry { RelativePath = "Camera/a.jpg" },
                new FileManifestEntry { RelativePath = "Camera/2026", IsDirectory = true },
                new FileManifestEntry { RelativePath = "Camera/2026/b.jpg" },
            ],
        };

        var queued = vm.EnqueueSubtreeDownloads(PhoneRoot, subtree, Path.Combine(_local.FullName, "Camera"));

        queued.Should().Be(2);
        await WaitForAsync(() => transfers.Downloads.Count == 2, "both files should be handed to the host");
        transfers.Downloads.Select(d => d.ClientId).Should().AllBe(PhoneId);
        transfers.Downloads.Select(d => d.Remote).Should().BeEquivalentTo(["Camera/a.jpg", "Camera/2026/b.jpg"]);
        transfers.Downloads.Select(d => d.Local).Should().BeEquivalentTo(
        [
            Path.Combine(_local.FullName, "Camera", "a.jpg"),
            Path.Combine(_local.FullName, "Camera", "2026", "b.jpg"),
        ]);
    }

    [Fact]
    public async Task AHostileManifestFromAPhone_CannotWriteOutsideTheChosenFolder()
    {
        var transfers = new FakePhoneTransfers();
        using var vm = NewViewModel(new FakePhoneAccess(), transfers);
        await SelectPhoneAsync(vm);
        var destination = Path.Combine(_local.FullName, "Camera");

        var subtree = new RemoteSubtree
        {
            BasePath = "Camera",
            Entries =
            [
                new FileManifestEntry { RelativePath = "Camera/ok.jpg" },
                new FileManifestEntry { RelativePath = "Camera/../../escape.txt" },
                new FileManifestEntry { RelativePath = "../escape.txt" },
                new FileManifestEntry { RelativePath = "/etc/passwd/../../escape.txt" },
                new FileManifestEntry { RelativePath = @"C:\Windows\escape.txt" },
                new FileManifestEntry { RelativePath = "C:/Windows/escape.txt" },
                new FileManifestEntry { RelativePath = @"Camera/..\..\escape.txt" },
                new FileManifestEntry { RelativePath = "Camera/a//empty-name.txt" },
                new FileManifestEntry { RelativePath = "Camera/" + new string('x', 300) },
                new FileManifestEntry { RelativePath = "Camera/../..", IsDirectory = true },
                new FileManifestEntry { RelativePath = "Camera/CON" },
            ],
        };

        var queued = vm.EnqueueSubtreeDownloads(PhoneRoot, subtree, destination);

        queued.Should().Be(1, "only the one ordinary file may be written");
        await WaitForAsync(() => transfers.Downloads.Count == 1, "the ordinary file should still download");
        transfers.Downloads.Single().Local.Should().Be(Path.Combine(destination, "ok.jpg"));
        Directory.EnumerateFileSystemEntries(_local.FullName, "*", SearchOption.AllDirectories)
            .Should().OnlyContain(path => path.StartsWith(destination, StringComparison.Ordinal),
                "nothing may be created outside the folder the person picked");
    }

    [Fact]
    public async Task Uploading_ToAPhone_GoesThroughTheHostTransferEngine_IntoTheOpenFolder()
    {
        var transfers = new FakePhoneTransfers();
        using var vm = NewViewModel(new FakePhoneAccess(), transfers);
        await SelectPhoneAsync(vm);
        vm.RemotePath = "/Camera/2026";
        var local = Path.Combine(_local.FullName, "note.txt");
        await File.WriteAllTextAsync(local, "hello");

        vm.EnqueueUploads([local]);

        await WaitForAsync(() => transfers.Uploads.Count == 1, "the upload should be handed to the host");
        var upload = transfers.Uploads.Single();
        upload.ClientId.Should().Be(PhoneId);
        upload.RootId.Should().Be(PhoneRoot.RootId);
        upload.RemoteDirectory.Should().Be("Camera/2026");
    }

    [Fact]
    public async Task UploadingAFolder_ToAPhone_IsDeclinedPlainly()
    {
        var transfers = new FakePhoneTransfers();
        using var vm = NewViewModel(new FakePhoneAccess(), transfers);
        await SelectPhoneAsync(vm);

        await vm.EnqueueFolderUploadAsync(_local.FullName);

        transfers.Uploads.Should().BeEmpty();
        vm.StatusText.Should().Be(LocalizationService.Instance["FileTransfer_PhoneFolderUploadUnavailable"]);
    }
}
