using System.Collections.Concurrent;
using Avalonia.Platform.Storage;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Backup;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The tray flyout's Send tile (RemEx-172ba): pick files, then offer them to the attached phone through
/// the existing PC-started upload (<see cref="IPhoneFileTransfers.UploadAsync"/>) into the phone's first
/// writable shared folder, on the session's one transfer queue. Harness shape from
/// <see cref="TrayFlyoutViewModelToolbarTests"/>, with a synchronous queue marshaller.
/// </summary>
public sealed class TrayFlyoutSendTests : IAsyncLifetime
{
    private const string PhoneId = "phone-1";

    private string _tempDir = null!;
    private DashboardLayoutService _layoutService = null!;
    private ShellViewModel _shell = null!;
    private TrayFlyoutViewModel _tray = null!;
    private readonly FakePhoneAccess _access = new();
    private readonly FakePhoneTransfers _transfers = new();
    private readonly IServiceProvider? _savedHost = App.EmbeddedHostServices;

    public async Task InitializeAsync()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-172ba-send-").FullName;
        var theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), theme);
        await _layoutService.LoadAsync();

        var launcher = new Mock<ILauncherStorageService>();
        launcher.Setup(l => l.LoadEntriesAsync()).ReturnsAsync(new List<AppEntry>());

        var connection = new ConnectionViewModel();
        _shell = new ShellViewModel(
            _layoutService, theme, connection,
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .AddSingleton(new RemexSavefileService(_layoutService, launcher.Object, new FileTransferRootSettingsService()))
                .BuildServiceProvider(),
            transferQueuePost: run => run());
        _shell.ProfileReplacedDispatch = run => run();
        _shell.DiagnosticsLogDispatch = run => run();

        _tray = new TrayFlyoutViewModel(_shell, new HomeViewModel(connection, _shell), launcher.Object)
        {
            ResolvePhoneAccess = () => _access,
            ResolvePhoneTransfers = () => _transfers,
        };
    }

    public Task DisposeAsync()
    {
        App.EmbeddedHostServices = _savedHost;
        PhonePresenceMonitor.Instance.Refresh();
        _tray.Dispose();
        _shell.Dispose();
        _layoutService.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    private string NewLocalFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "hello");
        return path;
    }

    private static IStorageFile StorageFile(string path)
    {
        var file = new Mock<IStorageFile>();
        file.SetupGet(f => f.Path).Returns(new Uri(path));
        file.SetupGet(f => f.Name).Returns(Path.GetFileName(path));
        return file.Object;
    }

    private void SetAttachedPhones(params ClientSession[] sessions)
    {
        App.EmbeddedHostServices = new SessionProvider(sessions);
        _tray.Refresh();
    }

    [Fact]
    public void WithAPhoneAttached_TheSendTileSends_AndIsEnabledLikeRemoteDesktop()
    {
        SetAttachedPhones(new ClientSession("192.168.1.42", "Pixel 9"));

        var send = _tray.Tiles.Single(t => t.Id == "send");
        var remote = _tray.Tiles.Single(t => t.Id == "remote");

        PhonePresenceMonitor.Instance.IsPhoneAttached.Should().BeTrue("precondition");
        send.Command.Should().BeSameAs(_tray.SendFilesToPhoneCommand);
        send.OpensMainWindow.Should().BeFalse("sending happens from the tray; the queue and notifications report it");
        send.IsEnabled.Should().BeTrue();
        remote.IsEnabled.Should().BeTrue("both need a phone attached, by the same presence signal");
    }

    [Fact]
    public void WithNoPhoneAttached_TheSendTileIsDisabled_AndSaysItNeedsAPhone()
    {
        SetAttachedPhones();

        var send = _tray.Tiles.Single(t => t.Id == "send");
        var remote = _tray.Tiles.Single(t => t.Id == "remote");

        PhonePresenceMonitor.Instance.IsPhoneAttached.Should().BeFalse("precondition");
        send.IsEnabled.Should().BeFalse();
        remote.IsEnabled.Should().BeFalse();
        send.DisabledTooltip.Should().Be(LocalizationService.Instance["Tray_Disabled_NeedsPhone"]);
    }

    [Fact]
    public async Task WithZeroPhonesReachable_TheCommandNeverOpensThePicker()
    {
        _access.Phones.Clear();
        var picked = false;
        _tray.PickFilesToSendAsync = _ => { picked = true; return Task.FromResult<IReadOnlyList<IStorageFile>>([]); };

        await _tray.SendFilesToPhoneCommand.ExecuteAsync(null);

        picked.Should().BeFalse("there is no phone to send to, so there is nothing to pick for");
        _transfers.Uploads.Should().BeEmpty();
        (_shell.CurrentView is FileTransferViewModel).Should().BeFalse("only several phones open the Files page");
    }

    [Fact]
    public async Task TheCommandReturnsOnceFilesAreQueued_NotWhenTheyFinish()
    {
        // R2: awaiting every upload held the Send tile disabled for as long as the transfers ran.
        _access.Roots = [WritableRoot("downloads")];
        var a = NewLocalFile("a.txt");
        _tray.PickFilesToSendAsync = _ => Task.FromResult<IReadOnlyList<IStorageFile>>([StorageFile(a)]);
        _transfers.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var run = _tray.SendFilesToPhoneCommand.ExecuteAsync(null);
            (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(run,
                "the command finishes while the upload is still running");
            await run;

            _tray.LastSendOutcome.IsCompleted.Should().BeFalse("the outcome is still waiting on the upload");
            _shell.TransferQueueForTests.Items.Should().ContainSingle().Which.State.Should().NotBe(TransferState.Done);
        }
        finally
        {
            _transfers.Gate.TrySetResult();
        }

        await _tray.LastSendOutcome.WaitAsync(TimeSpan.FromSeconds(10));
        _shell.TransferQueueForTests.Items.Single().State.Should().Be(TransferState.Done);
    }

    [Fact]
    public async Task APickerThatThrows_IsCaught_AndNothingIsSent()
    {
        // R3: the exception used to escape the AsyncRelayCommand onto the UI thread.
        _access.Roots = [WritableRoot("downloads")];
        _tray.PickFilesToSendAsync = _ => throw new InvalidOperationException("picker broke");

        var send = () => _tray.SendFilesToPhoneCommand.ExecuteAsync(null);

        await send.Should().NotThrowAsync();
        _transfers.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task PickedFiles_AreOfferedToThePhonesFirstWritableFolder_OnTheSharedQueue()
    {
        _access.Roots = [ReadOnlyRoot("dcim"), WritableRoot("downloads"), WritableRoot("documents")];
        var a = NewLocalFile("a.txt");
        var b = NewLocalFile("b.txt");
        FilePickerOpenOptions? asked = null;
        _tray.PickFilesToSendAsync = options =>
        {
            asked = options;
            return Task.FromResult<IReadOnlyList<IStorageFile>>([StorageFile(a), StorageFile(b)]);
        };

        await _tray.SendFilesToPhoneCommand.ExecuteAsync(null);
        await _tray.LastSendOutcome.WaitAsync(TimeSpan.FromSeconds(10));

        asked!.AllowMultiple.Should().BeTrue();
        _transfers.Uploads.Should().BeEquivalentTo(new[]
        {
            (PhoneId, a, "downloads", string.Empty),
            (PhoneId, b, "downloads", string.Empty),
        });
        _shell.TransferQueueForTests.Items.Select(i => i.FileName).Should().BeEquivalentTo(["a.txt", "b.txt"],
            "they ride the Files page's own queue, so its list and badge show them");
        _access.LastConnection!.Disposed.Should().BeTrue("the roots lookup does not leave a relay registration behind");
    }

    [Fact]
    public async Task APhoneSharingNoWritableFolder_GetsNothing()
    {
        _access.Roots = [ReadOnlyRoot("dcim")];

        await _tray.SendToPhoneAsync(new PhoneFileSource(PhoneId, "Pixel"), [NewLocalFile("a.txt")]);

        _transfers.Uploads.Should().BeEmpty();
        _shell.TransferQueueForTests.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task APhoneThatDroppedOff_GetsNothing_AndNothingThrows()
    {
        _access.Phones.Clear(); // Open now throws PhoneNotConnectedException

        var send = () => _tray.SendToPhoneAsync(new PhoneFileSource(PhoneId, "Pixel"), [NewLocalFile("a.txt")]);

        await send.Should().NotThrowAsync();
        _transfers.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task WithTwoPhones_ItOpensFilesToChoose_WithoutPicking()
    {
        _access.Phones.Add(new PhoneFileSource("phone-2", "Tablet"));
        var picked = false;
        var raised = false;
        _tray.PickFilesToSendAsync = _ => { picked = true; return Task.FromResult<IReadOnlyList<IStorageFile>>([]); };
        _tray.RaiseMainWindow = () => raised = true;

        await _tray.SendFilesToPhoneCommand.ExecuteAsync(null);

        picked.Should().BeFalse("there is no single phone to send to");
        raised.Should().BeTrue();
        _shell.CurrentView.Should().BeOfType<FileTransferViewModel>();
    }

    [Fact]
    public async Task ACancelledPicker_SendsNothing()
    {
        _access.Roots = [WritableRoot("downloads")];
        _tray.PickFilesToSendAsync = _ => Task.FromResult<IReadOnlyList<IStorageFile>>([]);

        await _tray.SendFilesToPhoneCommand.ExecuteAsync(null);

        _transfers.Uploads.Should().BeEmpty();
        _access.LastConnection.Should().BeNull("nothing is asked of the phone until there is something to send");
    }

    private static FileSharedRoot WritableRoot(string id) => new() { RootId = id, DisplayName = id, IsWritable = true };
    private static FileSharedRoot ReadOnlyRoot(string id) => new() { RootId = id, DisplayName = id, IsWritable = false };

    private sealed class FakePhoneAccess : IPhoneFileAccess
    {
        public List<PhoneFileSource> Phones { get; } = [new(PhoneId, "Pixel 9")];
        public List<FileSharedRoot> Roots { get; set; } = [];
        public FakePhoneConnection? LastConnection { get; private set; }

        public event Action? AvailabilityChanged { add { } remove { } }

        public IReadOnlyList<PhoneFileSource> AvailablePhones() => [.. Phones];

        public IPhoneFileConnection Open(string clientId)
        {
            if (Phones.All(p => p.ClientId != clientId))
                throw new PhoneNotConnectedException();
            return LastConnection = new FakePhoneConnection(clientId, this);
        }
    }

    private sealed class FakePhoneConnection(string clientId, FakePhoneAccess owner) : IPhoneFileConnection
    {
        public string ClientId { get; } = clientId;
        public bool IsConnected => !Disposed;
        public bool Disposed { get; private set; }

        public event Action<RemexMessage>? FileTransferMessageReceived;
        public event Action? Disconnected { add { } remove { } }

        public Task SendAsync(RemexMessage message)
        {
            if (message.Type == MessageTypes.FileRootsRequest)
            {
                FileTransferMessageReceived?.Invoke(new RemexMessage
                {
                    Type = MessageTypes.FileRootsResponse,
                    FileRootsResponse = new FileRootsResponse { Roots = [.. owner.Roots] },
                });
            }
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakePhoneTransfers : IPhoneFileTransfers
    {
        public ConcurrentQueue<(string ClientId, string Local, string RootId, string RemoteDirectory)> Uploads { get; } = new();

        /// <summary>When set, every upload waits for it.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public Task DownloadAsync(string clientId, string rootId, string remoteRelativePath, string localPath,
            IProgress<TransferProgress>? progress, CancellationToken ct) => Task.CompletedTask;

        public Task UploadAsync(string clientId, string localPath, string rootId, string remoteDirectory,
            IProgress<TransferProgress>? progress, CancellationToken ct)
        {
            Uploads.Enqueue((clientId, localPath, rootId, remoteDirectory));
            return Gate?.Task ?? Task.CompletedTask;
        }
    }

    private sealed class SessionProvider(IReadOnlyList<ClientSession> sessions) : IServiceProvider, IClientSessionSource
    {
        public IReadOnlyList<ClientSession> Snapshot() => sessions;

        public object? GetService(Type serviceType)
            => serviceType == typeof(IClientSessionSource) ? this : null;
    }
}
