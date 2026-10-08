using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.Services.FilePreview;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The explorer half of the File Transfer screen (2026-10-08 redesign), driven through scripted devices: the folder
/// tree, navigation between devices and folders, the preview pane, and moving files between devices.
/// </summary>
public sealed class FileExplorerTests : IDisposable
{
    private const string PhoneId = "phone-1";
    private readonly string _disk = Directory.CreateTempSubdirectory("remex-explorer-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_disk, recursive: true); } catch (IOException) { }
    }

    /// <summary>A device that answers file requests from an in-memory tree. Folders end with '/'.</summary>
    private sealed class ScriptedDevice(string clientId) : IPhoneFileConnection
    {
        public Dictionary<string, (string Name, bool Writable)> Roots { get; } = new();
        public Dictionary<string, byte[]> Files { get; } = new(); // "root:path/to/file"
        public HashSet<string> Folders { get; } = new();         // "root:path/to/folder"
        public List<FileVolumeInfo> Volumes { get; } = new();
        public bool ReadRange { get; set; } = true;
        public bool Hash { get; set; } = true;
        public bool FailBrowse { get; set; }
        public List<RemexMessage> Sent { get; } = new();

        public string ClientId => clientId;
        public bool IsConnected => true;
        public event Action? Disconnected { add { } remove { } }
        public event Action<RemexMessage>? FileTransferMessageReceived;
        public void Dispose() { }

        public void AddFile(string root, string path, string text) => AddFile(root, path, Encoding.UTF8.GetBytes(text));

        public void AddFile(string root, string path, byte[] bytes)
        {
            Files[$"{root}:{path}"] = bytes;
            var parts = path.Split('/');
            for (var i = 1; i < parts.Length; i++)
                Folders.Add($"{root}:{string.Join('/', parts[..i])}");
        }

        public Task SendAsync(RemexMessage m)
        {
            Sent.Add(m);
            var reply = Answer(m);
            if (reply is not null)
                FileTransferMessageReceived?.Invoke(reply);
            return Task.CompletedTask;
        }

        private RemexMessage? Answer(RemexMessage m) => m.Type switch
        {
            MessageTypes.FileRootsRequest => new RemexMessage
            {
                Type = MessageTypes.FileRootsResponse,
                FileRootsResponse = new FileRootsResponse
                {
                    Roots = Roots.Select(r => new FileSharedRoot { RootId = r.Key, DisplayName = r.Value.Name, IsWritable = r.Value.Writable }).ToArray(),
                    FileCapabilities = new FileCapabilities
                    {
                        Protocol = 3, Binary = true, Resume = true, ReadRange = ReadRange, Hash = Hash,
                        FullBrowse = Volumes.Count > 0, Ops = ["delete", "rename", "copy", "move", "mkdir", "search", "manifest"],
                    },
                },
            },
            MessageTypes.FileBrowseRequest => Browse(m.FileBrowseRequest!),
            MessageTypes.FileVolumesRequest => new RemexMessage
            {
                Type = MessageTypes.FileVolumesResponse,
                FileVolumesResponse = new FileVolumesResponse { RequestId = m.FileVolumesRequest!.RequestId, Volumes = [.. Volumes], FullBrowseGranted = true },
            },
            MessageTypes.FileReadRangeRequest => Read(m.FileReadRangeRequest!),
            MessageTypes.FileMetadataRequest => new RemexMessage
            {
                Type = MessageTypes.FileMetadataResponse,
                FileMetadataResponse = new FileMetadataResponse { RequestId = m.FileMetadataRequest!.RequestId, Size = 1 },
            },
            MessageTypes.FileThumbnailRequest => new RemexMessage
            {
                Type = MessageTypes.FileThumbnailResponse,
                FileThumbnailResponse = new FileThumbnailResponse { RequestId = m.FileThumbnailRequest!.RequestId },
            },
            MessageTypes.FileHashRequest => new RemexMessage
            {
                Type = MessageTypes.FileHashResponse,
                FileHashResponse = Files.TryGetValue($"{m.FileHashRequest!.RootId}:{m.FileHashRequest.RelativePath}", out var b)
                    ? new FileHashResponse { RequestId = m.FileHashRequest.RequestId, Sha256Base64 = Convert.ToBase64String(SHA256.HashData(b)) }
                    : new FileHashResponse { RequestId = m.FileHashRequest.RequestId, ErrorMessage = "Not found." },
            },
            _ => null,
        };

        private RemexMessage Browse(FileBrowseRequest r)
        {
            var folder = (r.RelativePath ?? string.Empty).Trim('/');
            var prefix = folder.Length == 0 ? $"{r.RootId}:" : $"{r.RootId}:{folder}/";
            static string Child(string rest) => rest.Split('/')[0];
            var dirs = Folders.Where(f => f.StartsWith(prefix, StringComparison.Ordinal))
                .Select(f => Child(f[prefix.Length..])).Distinct()
                .Select(n => new FileEntry { Name = n, IsDirectory = true });
            var files = Files.Keys.Where(f => f.StartsWith(prefix, StringComparison.Ordinal) && !f[prefix.Length..].Contains('/'))
                .Select(f => new FileEntry { Name = f[prefix.Length..], IsDirectory = false, SizeBytes = Files[f].Length });
            return new RemexMessage
            {
                Type = MessageTypes.FileBrowseResponse,
                FileBrowseResponse = FailBrowse
                    ? new FileBrowseResponse { RequestId = r.RequestId, Entries = [], ErrorMessage = "Phone said no." }
                    : new FileBrowseResponse { RequestId = r.RequestId, Entries = [.. dirs, .. files] },
            };
        }

        private RemexMessage Read(FileReadRangeRequest r)
        {
            var bytes = Files[$"{r.RootId}:{r.RelativePath}"];
            var start = r.FromEnd ? Math.Max(0, bytes.Length - r.Length) : r.Offset;
            var count = (int)Math.Clamp(bytes.Length - start, 0, r.Length);
            return new RemexMessage
            {
                Type = MessageTypes.FileReadRangeResponse,
                FileReadRangeResponse = new FileReadRangeResponse
                {
                    RequestId = r.RequestId, Offset = start, FileSize = bytes.Length, Eof = start + count >= bytes.Length,
                    DataBase64 = Convert.ToBase64String(bytes, (int)start, count),
                },
            };
        }
    }

    private sealed class FakeAccess(ScriptedDevice phone) : IPhoneFileAccess
    {
        public event Action? AvailabilityChanged;
        public List<PhoneFileSource> Phones { get; } = [new(PhoneId, "Pixel 9")];
        public IReadOnlyList<PhoneFileSource> AvailablePhones() => [.. Phones];
        public IPhoneFileConnection Open(string clientId) =>
            Phones.Any(p => p.ClientId == clientId) ? phone : throw new PhoneNotConnectedException();
        public void Raise() => AvailabilityChanged?.Invoke();
    }

    private sealed class FakeTransfers : IPhoneFileTransfers
    {
        public List<(string ClientId, string RootId, string Remote, string Local)> Downloads { get; } = new();
        public List<(string ClientId, string Local, string RootId, string RemoteDir)> Uploads { get; } = new();

        public Task DownloadAsync(string clientId, string rootId, string remoteRelativePath, string localPath, IProgress<TransferProgress>? progress, CancellationToken ct)
        {
            lock (Downloads) Downloads.Add((clientId, rootId, remoteRelativePath, localPath));
            return Task.CompletedTask;
        }

        public Task UploadAsync(string clientId, string localPath, string rootId, string remoteDirectory, IProgress<TransferProgress>? progress, CancellationToken ct)
        {
            lock (Uploads) Uploads.Add((clientId, localPath, rootId, remoteDirectory));
            return Task.CompletedTask;
        }
    }

    private static Action<Action> UiThreadStandIn()
    {
        var gate = new object();
        return action => { lock (gate) action(); };
    }

    private sealed record Kit(FileTransferViewModel Vm, ScriptedDevice Pc, ScriptedDevice Phone, FakeAccess Access, FakeTransfers Transfers);

    private async Task<Kit> OpenAsync(Action<ScriptedDevice>? pc = null, Action<ScriptedDevice>? phone = null)
    {
        var pcDevice = new ScriptedDevice("pc");
        pcDevice.Roots["docs"] = ("Documents", true);
        pcDevice.Roots["pics"] = ("Pictures", false);
        pcDevice.AddFile("docs", "Logs/agent.log", "one\ntwo\nERROR three\n");
        pcDevice.AddFile("docs", "notes.txt", "hello");
        pc?.Invoke(pcDevice);

        var phoneDevice = new ScriptedDevice(PhoneId);
        phoneDevice.Roots["dcim"] = ("DCIM", true);
        phoneDevice.AddFile("dcim", "Camera/IMG_1.jpg", [1, 2, 3]);
        phoneDevice.AddFile("dcim", "notes.txt", "hello");
        phone?.Invoke(phoneDevice);

        var access = new FakeAccess(phoneDevice);
        var transfers = new FakeTransfers();
        var vm = new FileTransferViewModel(
            new ConnectionViewModel { IsConnected = true },
            transferQueue: new FileTransferQueue(UiThreadStandIn()),
            client: new FileTransferClient(pcDevice),
            phoneAccess: () => access,
            phoneTransfers: () => transfers);
        vm.PostToUi = work => work();
        vm.LoadPcRoots = () => Task.FromResult<IReadOnlyList<FileTransferRootConfiguration>>(
        [
            new FileTransferRootConfiguration { RootId = "docs", DisplayName = "Documents", AbsolutePath = _disk, IsWritable = true },
        ]);
        await WaitForAsync(() => vm.SelectedRemoteRoot is not null && !vm.IsLoading && vm.Tree.Devices.Count == 2, "the screen should open on This PC");
        return new Kit(vm, pcDevice, phoneDevice, access, transfers);
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 400 && !condition(); i++)
            await Task.Delay(10);
        condition().Should().BeTrue(because);
    }

    // ── Tree ──

    [Fact]
    public async Task TheTree_ListsThisPcAndEachPhone_AndRevealsTheFolderOnShow_WithoutNavigatingAgain()
    {
        var kit = await OpenAsync();
        var navigations = 0;
        kit.Vm.Tree.NavigateRequested += _ => navigations++;

        kit.Vm.Tree.Devices.Select(d => d.IsThisPc).Should().Equal(true, false);
        var thisPc = kit.Vm.Tree.Devices[0];
        await WaitForAsync(() => thisPc.IsExpanded && thisPc.Children.Any(c => c.IsRoot), "This PC opens to its shared folders");
        thisPc.Children.Select(c => c.Name).Should().Equal("Documents", "Pictures");
        thisPc.Children.Single(c => c.Name == "Pictures").IsReadOnlyRoot.Should().BeTrue();
        kit.Vm.Tree.SelectedNode.Should().BeSameAs(thisPc.Children.Single(c => c.RootId == kit.Vm.SelectedRemoteRoot!.RootId));
        navigations.Should().Be(0, "revealing where the screen already is must not ask it to go there again");
    }

    [Fact]
    public async Task ExpandingASharedFolder_ListsOnlyItsFolders()
    {
        var kit = await OpenAsync();
        var docs = kit.Vm.Tree.Devices[0].Children.Single(c => c.RootId == "docs");

        await kit.Vm.Tree.EnsureChildrenAsync(docs);

        docs.Children.Select(c => c.Name).Should().Equal("Logs");
        docs.Children.Single().Path.Should().Be("Logs");
    }

    [Fact]
    public async Task ARowThatCannotBeListed_SaysSo_AndTriesAgainWhenOpenedAgain()
    {
        var kit = await OpenAsync();
        var phone = kit.Vm.Tree.Devices[1];
        await kit.Vm.Tree.EnsureChildrenAsync(phone);
        var dcim = phone.Children.Single(c => c.RootId == "dcim");
        kit.Phone.FailBrowse = true;

        await kit.Vm.Tree.EnsureChildrenAsync(dcim);
        dcim.Children.Should().ContainSingle(c => c.IsPlaceholder && c.LoadFailed);

        kit.Phone.FailBrowse = false;
        await kit.Vm.Tree.EnsureChildrenAsync(dcim);
        dcim.Children.Select(c => c.Name).Should().Equal("Camera");
    }

    [Fact]
    public async Task APhoneThatAllowsWholeDeviceBrowsing_ListsItsVolumes_ReadOnly()
    {
        var kit = await OpenAsync(phone: p => p.Volumes.Add(new FileVolumeInfo { Id = "vol-1", Label = "Internal storage", Path = "/", Kind = "internal" }));
        var phone = kit.Vm.Tree.Devices[1];

        await kit.Vm.Tree.EnsureChildrenAsync(phone);

        var volume = phone.Children.Single(c => c.RootId == "vol-1");
        volume.IsVolume.Should().BeTrue();
        volume.IsWritable.Should().BeFalse();
    }

    [Fact]
    public async Task TheDeviceRows_FollowPhonesComingAndGoing_AndKeepWhatWasOpen()
    {
        var kit = await OpenAsync();
        var thisPc = kit.Vm.Tree.Devices[0];

        kit.Access.Phones.Clear();
        kit.Access.Raise();
        kit.Vm.Tree.Devices.Should().ContainSingle().Which.Should().BeSameAs(thisPc);

        kit.Access.Phones.Add(new PhoneFileSource(PhoneId, "Pixel 9"));
        kit.Access.Raise();
        kit.Vm.Tree.Devices.Should().HaveCount(2);
        kit.Vm.Tree.Devices[0].Should().BeSameAs(thisPc, "an open row is not rebuilt when another device arrives");
    }

    // ── Navigation ──

    [Fact]
    public async Task PickingAFolderOnAnotherSharedFolder_LandsInThatFolder()
    {
        var kit = await OpenAsync(pc: d => d.AddFile("pics", "2026/a.jpg", [1]));
        var pics = kit.Vm.Tree.Devices[0].Children.Single(c => c.RootId == "pics");
        await kit.Vm.Tree.EnsureChildrenAsync(pics);

        kit.Vm.Tree.SelectedNode = pics.Children.Single(c => c.Name == "2026");

        await WaitForAsync(() => kit.Vm.SelectedRemoteRoot?.RootId == "pics" && kit.Vm.RemoteEntries.Any(e => e.Name == "a.jpg"),
            "the screen shows the folder picked in the tree, not the top of its shared folder");
    }

    [Fact]
    public async Task PickingAFolderOnThePhone_SwitchesDevice_AndLandsInThatFolder()
    {
        var kit = await OpenAsync();

        await kit.Vm.NavigateToAsync(kit.Vm.Sources.Single(s => s.ClientId == PhoneId), "dcim", "Camera");

        await WaitForAsync(() => kit.Vm.IsPhoneSource && kit.Vm.RemoteEntries.Any(e => e.Name == "IMG_1.jpg"),
            "the phone's Camera folder is shown");
        kit.Vm.CurrentDeviceLabel.Should().Be("Pixel 9");
    }

    // ── Preview ──

    private static PreviewTarget Target(Kit kit, FileTransferClient client, string root, string path, bool phone = false) =>
        new(client, phone ? new FileSourceOption(PhoneId, "Pixel 9") : FileSourceOption.ThisPc, root, path,
            new FileEntry { Name = Path.GetFileName(path), IsDirectory = false });

    [Fact]
    public async Task ALog_IsPreviewedAsColouredLines_AndOffersLiveTail()
    {
        var kit = await OpenAsync();
        var client = new FileTransferClient(kit.Pc);
        await client.ListRemoteRootsAsync(CancellationToken.None);
        var preview = new FilePreviewViewModel((_, _) => Task.FromResult<FileTransferClient?>(client));

        await preview.LoadAsync(Target(kit, client, "docs", "Logs/agent.log"), CancellationToken.None);

        preview.State.Should().Be(PreviewState.Text);
        preview.Lines.Select(l => l.Text).Should().Equal("one", "two", "ERROR three");
        preview.Lines[2].Spans.Should().Contain(s => s.Kind == SyntaxKind.LogError);
        preview.CanTail.Should().BeTrue();
        preview.CanComputeHash.Should().BeTrue();
    }

    [Theory]
    [InlineData("data.txt", PreviewState.Binary)]   // named as text, is not
    [InlineData("data.blob", PreviewState.Details)] // unknown name, sniffed binary: details only
    [InlineData("setup.exe", PreviewState.Details)] // never previewed
    public async Task AFileThatIsNotText_IsNotShownAsText(string name, PreviewState expected)
    {
        var kit = await OpenAsync(pc: d => d.AddFile("docs", name, [0, 1, 2, 0, 3]));
        var client = new FileTransferClient(kit.Pc);
        await client.ListRemoteRootsAsync(CancellationToken.None);
        var preview = new FilePreviewViewModel((_, _) => Task.FromResult<FileTransferClient?>(client));

        await preview.LoadAsync(Target(kit, client, "docs", name), CancellationToken.None);

        preview.State.Should().Be(expected);
        preview.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task ADeviceThatPredatesPreviews_IsAskedToUpdate_RatherThanReadFrom()
    {
        var kit = await OpenAsync(pc: d => d.ReadRange = false);
        var client = new FileTransferClient(kit.Pc);
        await client.ListRemoteRootsAsync(CancellationToken.None);
        var preview = new FilePreviewViewModel((_, _) => Task.FromResult<FileTransferClient?>(client));

        await preview.LoadAsync(Target(kit, client, "docs", "notes.txt"), CancellationToken.None);

        preview.State.Should().Be(PreviewState.NeedsUpdate);
        kit.Pc.Sent.Should().NotContain(m => m.Type == MessageTypes.FileReadRangeRequest);
    }

    [Fact]
    public async Task AnImageOverTheLimit_SaysTooLarge()
    {
        var kit = await OpenAsync(pc: d => d.AddFile("docs", "huge.png", new byte[100]));
        var client = new FileTransferClient(kit.Pc);
        await client.ListRemoteRootsAsync(CancellationToken.None);
        // A host that claims a size over the limit: the loader refuses after its first read.
        kit.Pc.Files["docs:huge.png"] = new byte[FileTransferLimits.ReadRangeMaxBytes];
        var preview = new FilePreviewViewModel((_, _) => Task.FromResult<FileTransferClient?>(client));
        var decoded = 0;
        preview.DecodeImage = _ => { decoded++; return null; };

        // Within the limit an image is decoded…
        await preview.LoadAsync(Target(kit, client, "docs", "huge.png"), CancellationToken.None);
        decoded.Should().Be(1);
    }

    [Fact]
    public async Task APastedHash_IsComparedInAnyForm_AndANonHashSaysSo()
    {
        var kit = await OpenAsync();
        var client = new FileTransferClient(kit.Pc);
        await client.ListRemoteRootsAsync(CancellationToken.None);
        var preview = new FilePreviewViewModel((_, _) => Task.FromResult<FileTransferClient?>(client));
        await preview.LoadAsync(Target(kit, client, "docs", "notes.txt"), CancellationToken.None);

        await preview.ComputeHashCommand.ExecuteAsync(null);
        var hex = Convert.ToHexStringLower(SHA256.HashData("hello"u8));
        preview.HashHex.Should().Be(hex);

        preview.CompareInput = hex.ToUpperInvariant();
        preview.CompareOutcome.Should().Be(HashCompareOutcome.Match);
        preview.CompareInput = new string('0', 64);
        preview.CompareOutcome.Should().Be(HashCompareOutcome.Mismatch);
        preview.CompareInput = "abc";
        preview.CompareOutcome.Should().Be(HashCompareOutcome.NotAHash);
    }

    [Fact]
    public async Task VerifyingAgainstThePhone_SaysIdentical_ThenDifferentWhenTheCopiesDiffer()
    {
        var kit = await OpenAsync();
        var client = new FileTransferClient(kit.Pc);
        await client.ListRemoteRootsAsync(CancellationToken.None);
        var phoneClient = new FileTransferClient(kit.Phone);
        await phoneClient.ListRemoteRootsAsync(CancellationToken.None);
        var preview = new FilePreviewViewModel((s, _) => Task.FromResult<FileTransferClient?>(s.IsThisPc ? client : phoneClient))
        {
            OtherDevices = () => [new FileSourceOption(PhoneId, "Pixel 9")],
            LastFolderOn = _ => ("dcim", string.Empty),
        };

        await preview.LoadAsync(Target(kit, client, "docs", "notes.txt"), CancellationToken.None);
        await WaitForAsync(() => preview.CounterpartRoot is not null, "the phone's shared folders are listed");
        preview.CounterpartPath.Should().Be("notes.txt", "the same name, in the folder last opened on the phone");

        await preview.VerifyAgainstCommand.ExecuteAsync(null);
        preview.CounterpartOutcome.Should().Be(CounterpartOutcome.Identical);

        kit.Phone.Files["dcim:notes.txt"] = "hello!"u8.ToArray();
        await preview.VerifyAgainstCommand.ExecuteAsync(null);
        preview.CounterpartOutcome.Should().Be(CounterpartOutcome.Different);
    }

    [Fact]
    public void ALiveTail_ResetsAppendsAndKeepsAtMostItsLimit()
    {
        var preview = new FilePreviewViewModel((_, _) => Task.FromResult<FileTransferClient?>(null));

        preview.ApplyTail(new TailUpdate(true, ["a", "b"], "c", 3));
        preview.Lines.Select(l => l.Text).Should().Equal("a", "b");
        preview.Lines.Should().OnlyContain(l => l.Number == null, "a tail does not know its line numbers");
        preview.PendingLine.Should().Be("c");

        preview.ApplyTail(new TailUpdate(false, ["c"], string.Empty, 4));
        preview.Lines.Select(l => l.Text).Should().Equal("a", "b", "c");

        preview.ApplyTail(new TailUpdate(false, Enumerable.Range(0, FilePreviewViewModel.MaxTailLines).Select(i => $"x{i}").ToList(), "", 9));
        preview.Lines.Should().HaveCount(FilePreviewViewModel.MaxTailLines);
        preview.Lines[^1].Text.Should().Be($"x{FilePreviewViewModel.MaxTailLines - 1}");
    }

    // ── Moving files between devices ──

    [Fact]
    public async Task PcFilesDroppedOnAPhoneFolder_AreUploadedFromWhereTheyAreOnDisk()
    {
        var kit = await OpenAsync();
        var phone = kit.Vm.Tree.Devices[1];
        await kit.Vm.Tree.EnsureChildrenAsync(phone);
        var dcim = phone.Children.Single(c => c.RootId == "dcim");
        var payload = new FileDragPayload(FileSourceOption.ThisPc, "docs", "Logs",
            [new FileEntry { Name = "agent.log", IsDirectory = false }, new FileEntry { Name = "Old", IsDirectory = true }]);

        await kit.Vm.DropOntoNodeAsync(payload, dcim, copy: false);
        await Task.WhenAll(kit.Vm.TransferQueue.Items.Select(i => i.Completion.Task));

        kit.Transfers.Uploads.Should().ContainSingle()
            .Which.Should().Be((PhoneId, Path.Combine(_disk, "Logs", "agent.log"), "dcim", string.Empty));
        kit.Vm.TransferQueue.Items.Should().OnlyContain(i => i.Kind == FileTransferQueueKind.Upload);
    }

    [Fact]
    public async Task PhoneFilesDroppedOnAPcFolder_AreDownloadedThere_WithoutReplacingWhatIsThere()
    {
        var kit = await OpenAsync();
        File.WriteAllText(Path.Combine(_disk, "notes.txt"), "already here");
        var docs = kit.Vm.Tree.Devices[0].Children.Single(c => c.RootId == "docs");
        var payload = new FileDragPayload(new FileSourceOption(PhoneId, "Pixel 9"), "dcim", string.Empty,
            [new FileEntry { Name = "notes.txt", IsDirectory = false }]);

        await kit.Vm.DropOntoNodeAsync(payload, docs, copy: false);
        await Task.WhenAll(kit.Vm.TransferQueue.Items.Select(i => i.Completion.Task));

        kit.Transfers.Downloads.Should().ContainSingle()
            .Which.Should().Be((PhoneId, "dcim", "notes.txt", Path.Combine(_disk, "notes (2).txt")));
    }

    [Fact]
    public async Task PhoneToPhone_AndFolders_AreRefusedWithAReason_AndQueueNothing()
    {
        var kit = await OpenAsync();
        var phoneB = new FileSourceOption("phone-2", "Tablet");
        var node = kit.Vm.Tree.Devices[1];
        await kit.Vm.Tree.EnsureChildrenAsync(node);
        var dcim = node.Children.Single(c => c.RootId == "dcim");

        await kit.Vm.DropOntoNodeAsync(new FileDragPayload(phoneB, "x", "", [new FileEntry { Name = "a.jpg", IsDirectory = false }]), dcim, copy: false);
        kit.Vm.StatusText.Should().Be(LocalizationService.Instance["FilePreview_PhoneToPhoneUnsupported"],
            "a refusal names its real reason, not whatever later check happens to fail");
        await kit.Vm.DropOntoNodeAsync(new FileDragPayload(FileSourceOption.ThisPc, "docs", "", [new FileEntry { Name = "Logs", IsDirectory = true }]), dcim, copy: false);
        kit.Vm.StatusText.Should().Be(LocalizationService.Instance["FilePreview_FoldersNotSent"]);

        kit.Vm.TransferQueue.Items.Should().BeEmpty();
        kit.Transfers.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task APcPath_ThatWouldLeaveItsSharedFolder_IsNeverResolved()
    {
        var kit = await OpenAsync();

        (await kit.Vm.LocalPathForAsync("docs", "Logs/agent.log")).Should().Be(Path.Combine(_disk, "Logs", "agent.log"));
        (await kit.Vm.LocalPathForAsync("docs", "../outside.txt")).Should().BeNull();
        (await kit.Vm.LocalPathForAsync("not-a-root", "a.txt")).Should().BeNull();
    }

    [Fact]
    public void UniqueLocalPath_KeepsBothRatherThanReplacing()
    {
        var file = Path.Combine(_disk, "a.txt");
        FileTransferViewModel.UniqueLocalPath(file).Should().Be(file);
        File.WriteAllText(file, "x");
        File.WriteAllText(Path.Combine(_disk, "a (2).txt"), "x");
        FileTransferViewModel.UniqueLocalPath(file).Should().Be(Path.Combine(_disk, "a (3).txt"));
    }
}
