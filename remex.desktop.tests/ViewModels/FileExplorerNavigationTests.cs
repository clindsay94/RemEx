using System;
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
/// Navigation and device-handling decisions of the explorer half of the File Transfer screen that
/// <see cref="FileExplorerTests"/> does not reach: refused or no-op navigation, path normalising, remembering the
/// last folder per device, reusing and re-opening the tree's phone connections, and the copy-path text.
/// </summary>
public sealed class FileExplorerNavigationTests : IDisposable
{
    private const string PhoneId = "phone-1";
    private readonly string _disk = Directory.CreateTempSubdirectory("remex-explorer-nav-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_disk, recursive: true); } catch (IOException) { }
    }

    /// <summary>A device with shared folders whose listings are fixed: "root:folder" -> entry names ('/' suffix = folder).</summary>
    private sealed class Device(string clientId) : IPhoneFileConnection
    {
        public Dictionary<string, bool> Roots { get; } = new(); // root id -> writable
        public Dictionary<string, string[]> Listings { get; } = new();
        public List<string> BrowsedPaths { get; } = new();
        public bool Disposed { get; private set; }
        public bool Connected { get; set; } = true;

        public string ClientId => clientId;
        public bool IsConnected => Connected;
        public event Action? Disconnected { add { } remove { } }
        public event Action<RemexMessage>? FileTransferMessageReceived;
        public void Dispose() => Disposed = true;

        public Task SendAsync(RemexMessage m)
        {
            RemexMessage? reply = m.Type switch
            {
                MessageTypes.FileRootsRequest => new RemexMessage
                {
                    Type = MessageTypes.FileRootsResponse,
                    FileRootsResponse = new FileRootsResponse
                    {
                        Roots = Roots.Select(r => new FileSharedRoot { RootId = r.Key, DisplayName = r.Key.ToUpperInvariant(), IsWritable = r.Value }).ToArray(),
                        FileCapabilities = new FileCapabilities { Protocol = 3, Binary = true, Ops = ["delete", "rename", "copy", "move", "mkdir"] },
                    },
                },
                MessageTypes.FileBrowseRequest => Browse(m.FileBrowseRequest!),
                _ => null,
            };
            if (reply is not null)
                FileTransferMessageReceived?.Invoke(reply);
            return Task.CompletedTask;
        }

        private RemexMessage Browse(FileBrowseRequest r)
        {
            var folder = (r.RelativePath ?? string.Empty).Trim('/');
            BrowsedPaths.Add($"{r.RootId}:{folder}");
            var names = Listings.TryGetValue($"{r.RootId}:{folder}", out var found) ? found : [];
            return new RemexMessage
            {
                Type = MessageTypes.FileBrowseResponse,
                FileBrowseResponse = new FileBrowseResponse
                {
                    RequestId = r.RequestId,
                    Entries = [.. names.Select(n => new FileEntry { Name = n.TrimEnd('/'), IsDirectory = n.EndsWith('/'), SizeBytes = 1 })],
                },
            };
        }
    }

    private sealed class Access(Func<string, IPhoneFileConnection> open) : IPhoneFileAccess
    {
        public event Action? AvailabilityChanged;
        public List<PhoneFileSource> Phones { get; } = [new(PhoneId, "Pixel 9")];
        public int Opened { get; private set; }
        public IReadOnlyList<PhoneFileSource> AvailablePhones() => [.. Phones];
        public IPhoneFileConnection Open(string clientId)
        {
            if (Phones.All(p => p.ClientId != clientId))
                throw new PhoneNotConnectedException();
            Opened++;
            return open(clientId);
        }
        public void Raise() => AvailabilityChanged?.Invoke();
    }

    private sealed record Kit(FileTransferViewModel Vm, Device Pc, Access Access);

    private async Task<Kit> OpenAsync(Func<string, IPhoneFileConnection>? phone = null)
    {
        var pc = new Device("pc");
        pc.Roots["docs"] = true;
        pc.Roots["pics"] = false;
        pc.Listings["docs:"] = ["Logs/", "notes.txt"];
        pc.Listings["docs:Logs"] = ["2026/", "agent.log"];
        pc.Listings["docs:Logs/2026"] = ["jan.log"];
        pc.Listings["pics:"] = ["a.jpg"];

        var access = new Access(phone ?? (_ => new Device(PhoneId)));
        var vm = new FileTransferViewModel(
            new ConnectionViewModel { IsConnected = true },
            transferQueue: new FileTransferQueue(action => action()),
            client: new FileTransferClient(pc),
            phoneAccess: () => access,
            phoneTransfers: () => null);
        vm.PostToUi = work => work();
        vm.LoadPcRoots = () => Task.FromResult<IReadOnlyList<FileTransferRootConfiguration>>(
            [new FileTransferRootConfiguration { RootId = "docs", DisplayName = "Documents", AbsolutePath = _disk, IsWritable = true }]);
        await WaitForAsync(() => vm.SelectedRemoteRoot is not null && !vm.IsLoading, "the screen opens on This PC");
        return new Kit(vm, pc, access);
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 400 && !condition(); i++)
            await Task.Delay(10);
        condition().Should().BeTrue(because);
    }

    // ── Navigation ──

    [Fact]
    public async Task ADeviceThatIsNotListed_IsNotNavigatedTo()
    {
        var kit = await OpenAsync();
        var before = kit.Vm.SelectedSource;
        var browses = kit.Pc.BrowsedPaths.Count;

        await kit.Vm.NavigateToAsync(new FileSourceOption("ghost", "Gone phone"), "dcim", "Camera");

        kit.Vm.SelectedSource.Should().BeSameAs(before);
        kit.Pc.BrowsedPaths.Should().HaveCount(browses);
    }

    [Fact]
    public async Task AskingForJustTheDevice_AlreadyOnShow_ChangesNothing()
    {
        var kit = await OpenAsync();
        await kit.Vm.NavigateToAsync(kit.Vm.SelectedSource!, "docs", "Logs");
        var path = kit.Vm.RemotePath;
        var browses = kit.Pc.BrowsedPaths.Count;

        await kit.Vm.NavigateToAsync(kit.Vm.SelectedSource!, null, "Logs/2026");

        kit.Vm.RemotePath.Should().Be(path);
        kit.Pc.BrowsedPaths.Should().HaveCount(browses);
    }

    [Theory]
    [InlineData("Logs/2026", "/Logs/2026")]
    [InlineData("\\Logs\\2026\\", "/Logs/2026")]
    [InlineData("/Logs/", "/Logs")]
    [InlineData("", "/")]
    public async Task APathInTheShownFolder_IsNormalisedToSlashesAndBrowsed(string asked, string shown)
    {
        var kit = await OpenAsync();
        await WaitForAsync(() => kit.Vm.SelectedRemoteRoot?.RootId == "docs", "docs is the first shared folder");

        await kit.Vm.NavigateToAsync(kit.Vm.SelectedSource!, "docs", asked);

        kit.Vm.RemotePath.Should().Be(shown);
        await WaitForAsync(() => !kit.Vm.IsLoading && kit.Pc.BrowsedPaths.Contains("docs:" + shown.Trim('/')),
            "the normalised path is what the device is asked for");
    }

    [Fact]
    public async Task ASharedFolderThatIsNotListed_LeavesTheScreenWhereItWas()
    {
        var kit = await OpenAsync();
        var root = kit.Vm.SelectedRemoteRoot;
        var path = kit.Vm.RemotePath;

        await kit.Vm.NavigateToAsync(kit.Vm.SelectedSource!, "no-such-root", "x");

        kit.Vm.SelectedRemoteRoot.Should().BeSameAs(root);
        kit.Vm.RemotePath.Should().Be(path);
    }

    [Fact]
    public async Task EveryListing_IsRememberedAsTheLastFolderOnThatDevice()
    {
        var kit = await OpenAsync();

        await kit.Vm.NavigateToAsync(kit.Vm.SelectedSource!, "docs", "Logs/2026");

        kit.Vm.Preview.LastFolderOn!(FileSourceOption.ThisPc).Should().Be(("docs", "Logs/2026"));
        kit.Vm.Preview.LastFolderOn(new FileSourceOption(PhoneId, "Pixel 9")).Should().BeNull("the phone has not been browsed");
    }

    // ── Phones kept open for the tree ──

    [Fact]
    public async Task APhoneOpenedForTheTree_IsReusedWhileConnected_AndReopenedOnceItDrops()
    {
        var created = new List<Device>();
        var kit = await OpenAsync(_ => { var d = new Device(PhoneId); d.Roots["dcim"] = true; created.Add(d); return d; });
        var phone = kit.Vm.Sources.Single(s => s.ClientId == PhoneId);

        var first = await kit.Vm.ClientForAsync(phone, CancellationToken.None);
        var again = await kit.Vm.ClientForAsync(phone, CancellationToken.None);
        again.Should().BeSameAs(first);
        kit.Access.Opened.Should().Be(1);

        created[0].Connected = false;
        var reopened = await kit.Vm.ClientForAsync(phone, CancellationToken.None);

        reopened.Should().NotBeSameAs(first);
        kit.Access.Opened.Should().Be(2);
        created[0].Disposed.Should().BeTrue("the dead connection is released, not leaked");
    }

    [Fact]
    public async Task APhoneThatCannotBeReached_YieldsNoClient()
    {
        var kit = await OpenAsync();
        var phone = kit.Vm.Sources.Single(s => s.ClientId == PhoneId);
        kit.Access.Phones.Clear(); // listed in Sources, but the relay no longer has it

        var client = await kit.Vm.ClientForAsync(phone, CancellationToken.None);

        client.Should().BeNull();
    }

    [Fact]
    public async Task APhoneThatLeaves_HasItsTreeConnectionClosed()
    {
        Device? opened = null;
        var kit = await OpenAsync(_ => opened = new Device(PhoneId) { Roots = { ["dcim"] = true } });
        await kit.Vm.ClientForAsync(kit.Vm.Sources.Single(s => s.ClientId == PhoneId), CancellationToken.None);
        opened.Should().NotBeNull();

        kit.Access.Phones.Clear();
        kit.Access.Raise();

        opened!.Disposed.Should().BeTrue();
    }

    // ── Context-menu helpers ──

    [Fact]
    public async Task CopyPath_ReadsDeviceThenSharedFolderThenTheSelectedPath()
    {
        var kit = await OpenAsync();
        await kit.Vm.NavigateToAsync(kit.Vm.SelectedSource!, "docs", "Logs");
        var copied = new List<string>();
        kit.Vm.CopyText = text => { copied.Add(text); return Task.CompletedTask; };

        kit.Vm.SelectedRemoteEntry = new FileEntry { Name = "agent.log", IsDirectory = false };
        await kit.Vm.CopyPathCommand.ExecuteAsync(null);
        kit.Vm.SelectedRemoteEntry = new FileEntry { Name = "..", IsDirectory = true };
        await kit.Vm.CopyPathCommand.ExecuteAsync(null);

        var pc = FileTreeViewModel.DeviceLabel(FileSourceOption.ThisPc);
        copied.Should().Equal($"{pc} › DOCS › Logs › agent.log", $"{pc} › DOCS › Logs");
    }

    [Fact]
    public async Task SendingTo_ADeviceWithNoWritableFolder_SaysSo_AndQueuesNothing()
    {
        var kit = await OpenAsync(_ => new Device(PhoneId) { Roots = { ["dcim"] = false } });
        await WaitForAsync(() => kit.Vm.RemoteEntries.Any(e => e.Name == "notes.txt"), "the listing is shown");
        kit.Vm.SetSelectedEntries([kit.Vm.RemoteEntries.Single(e => e.Name == "notes.txt")]);
        var phone = kit.Vm.Sources.Single(s => s.ClientId == PhoneId);

        await kit.Vm.SendSelectionToCommand.ExecuteAsync(phone);

        kit.Vm.StatusText.Should().Be(LocalizationService.Instance["FileTransfer_DropTargetReadOnly"]);
        kit.Vm.TransferQueue.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task SendingNothing_DoesNothing()
    {
        var kit = await OpenAsync();
        kit.Vm.SetSelectedEntries([]);
        kit.Vm.StatusText = string.Empty;

        await kit.Vm.SendSelectionToCommand.ExecuteAsync(kit.Vm.Sources.Single(s => s.ClientId == PhoneId));

        kit.Vm.StatusText.Should().BeEmpty();
        kit.Vm.TransferQueue.Items.Should().BeEmpty();
        kit.Access.Opened.Should().Be(0, "no point reaching for the phone when there is nothing to send");
    }

    [Fact]
    public async Task NoThumbnail_IsFetchedForAFolderOrAFileThatIsNotAnImage()
    {
        var kit = await OpenAsync();
        var browses = kit.Pc.BrowsedPaths.Count;

        var folder = await kit.Vm.GetThumbnailAsync(new FileEntry { Name = "Logs", IsDirectory = true }, CancellationToken.None);
        var text = await kit.Vm.GetThumbnailAsync(new FileEntry { Name = "notes.txt", IsDirectory = false }, CancellationToken.None);

        folder.Should().BeNull();
        text.Should().BeNull();
        kit.Pc.BrowsedPaths.Should().HaveCount(browses);
    }
}
