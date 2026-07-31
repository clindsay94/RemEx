using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Desktop;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Security;
using Remex.Desktop.ViewModels;
using Remex.Core;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Models.IPC;
using Remex.Core.Services.Network;
using Remex.Core.Services.Security;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

public class ConnectionViewModelTests : IDisposable
{
    private static readonly FieldInfo AppServicesBackingField =
        typeof(App).GetField("<Services>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly MethodInfo PrepareTlsValidationForConnectMethod =
        typeof(ConnectionViewModel).GetMethod(
            "PrepareTlsValidationForConnectAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo PinSnapshotField =
        typeof(ConnectionViewModel).GetField(
            "_pinSnapshot",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo AllowFirstTimeTrustField =
        typeof(ConnectionViewModel).GetField(
            "_allowFirstTimeTrustForCurrentConnect",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly Mock<IMdnsDiscoveryService> _mockDiscoveryService;
    private readonly Mock<ILogger<ConnectionViewModel>> _mockLogger;
    private ConnectionViewModel _viewModel;
    private readonly IServiceProvider? _originalAppServices;

    public ConnectionViewModelTests()
    {
        _mockDiscoveryService = new Mock<IMdnsDiscoveryService>();
        _mockLogger = new Mock<ILogger<ConnectionViewModel>>();
        _viewModel = new ConnectionViewModel(_mockDiscoveryService.Object, null, _mockLogger.Object);
        _originalAppServices = App.Services;
    }

    public void Dispose()
    {
        AppServicesBackingField.SetValue(null, _originalAppServices);
        _viewModel?.Dispose();
    }

    [Fact]
    public void Constructor_WithAllNullDependencies_ShouldNotThrow()
    {
        var vm = new ConnectionViewModel(null, null, null);
        vm.Should().NotBeNull();
        vm.IsConnected.Should().BeFalse();
        vm.Dispose();
    }

    [Theory]
    [InlineData("ws://localhost:5005/ws")]
    [InlineData("ws://192.168.1.100:5005/ws")]
    public void HostAddress_WhenSetToValidWebSocketUri_ShouldPersist(string validAddress)
    {
        _viewModel.HostAddress = validAddress;
        _viewModel.HostAddress.Should().Be(validAddress);
    }

    [Fact]
    public void InitialState_ShouldBeDisconnected()
    {
        _viewModel.IsConnected.Should().BeFalse();
        _viewModel.IsConnecting.Should().BeFalse();
        _viewModel.IsAutoReconnecting.Should().BeFalse();
    }

    [Fact]
    public void CanConnect_WhenDisconnected_ShouldReturnTrue()
    {
        _viewModel.IsConnected = false;
        _viewModel.IsConnecting = false;
        _viewModel.ConnectCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void CanConnect_WhenAlreadyConnected_ShouldReturnFalse()
    {
        _viewModel.IsConnected = true;
        _viewModel.ConnectCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void CanDisconnect_WhenConnected_ShouldReturnTrue()
    {
        _viewModel.IsConnected = true;
        _viewModel.DisconnectCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void CanDisconnect_WhenDisconnected_ShouldReturnFalse()
    {
        _viewModel.IsConnected = false;
        _viewModel.IsConnecting = false;
        _viewModel.DisconnectCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void IsConnected_WhenChanged_ShouldNotifyCommandsCanExecuteChanged()
    {
        var connectChangedCount = 0;
        _viewModel.ConnectCommand.CanExecuteChanged += (s, e) => connectChangedCount++;
        _viewModel.IsConnected = true;
        connectChangedCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void LatencyHistory_ShouldBeInitiallyEmpty()
    {
        _viewModel.LatencyHistory.Should().NotBeNull();
        _viewModel.LatencyHistory.Should().BeEmpty();
    }

    [Fact]
    public void LatencyText_ShouldDefaultToDash()
    {
        _viewModel.LatencyText.Should().Be("—");
    }

    [Fact]
    public void AverageLatency_ShouldDefaultToZero()
    {
        _viewModel.AverageLatency.Should().Be(0);
    }

    [Fact]
    public void MaxLatency_ShouldDefaultToZero()
    {
        _viewModel.MaxLatency.Should().Be(0);
    }

    [Fact]
    public void HostCapabilities_WhenNull_SupportsRemoteDesktopShouldBeTrue()
    {
        _viewModel.HostCapabilities = null;
        _viewModel.SupportsRemoteDesktop.Should().BeTrue();
    }

    [Fact]
    public void HostCapabilities_WhenSetWithRemoteDesktopDisabled_ShouldReturnFalse()
    {
        _viewModel.HostCapabilities = new HostCapabilities { SupportsRemoteDesktop = false };
        _viewModel.SupportsRemoteDesktop.Should().BeFalse();
    }

    [Fact]
    public void HostCapabilities_WhenChanged_ShouldNotifyDependentProperties()
    {
        var changedProperties = new List<string>();
        _viewModel.PropertyChanged += (s, e) => { if (e.PropertyName != null) changedProperties.Add(e.PropertyName); };
        _viewModel.HostCapabilities = new HostCapabilities { Platform = "Windows", RuntimeMode = "service" };
        changedProperties.Should().Contain(nameof(ConnectionViewModel.SupportsRemoteDesktop));
        changedProperties.Should().Contain(nameof(ConnectionViewModel.HostRuntimeSummary));
    }

    [Fact]
    public void CanSendPing_WhenNotConnected_ShouldReturnFalse()
    {
        _viewModel.IsConnected = false;
        _viewModel.SendPingCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void CanSendPing_WhenConnected_ShouldReturnTrue()
    {
        _viewModel.IsConnected = true;
        _viewModel.SendPingCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Dispose_ShouldNotThrowOnDoubleDispose()
    {
        var vm = new ConnectionViewModel(_mockDiscoveryService.Object, null, _mockLogger.Object);
        var act = () => { vm.Dispose(); vm.Dispose(); };
        act.Should().NotThrow();
    }

    [Fact]
    public void Processes_ShouldBeInitializedEmpty()
    {
        _viewModel.Processes.Should().NotBeNull();
        _viewModel.Processes.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestProcessListAsync_WhenNotConnected_ShouldNotThrow()
    {
        _viewModel.IsConnected = false;
        Func<Task> act = async () => await _viewModel.RequestProcessListAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void LauncherEntriesReceived_EventSubscription_ShouldNotThrow()
    {
        var act = () => { _viewModel.LauncherEntriesReceived += _ => { }; };
        act.Should().NotThrow();
    }

    [Fact]
    public void DiscoverHostsCommand_ShouldExist()
    {
        _viewModel.DiscoverHostsCommand.Should().NotBeNull();
    }

    [Fact]
    public async Task DiscoverHostsCommand_WhenNoDiscoveryService_ShouldNotCrash()
    {
        var vmWithoutDiscovery = new ConnectionViewModel(null, null, null);
        await vmWithoutDiscovery.DiscoverHostsCommand.ExecuteAsync(null);
        vmWithoutDiscovery.StatusText.Should().NotBeNullOrEmpty();
        vmWithoutDiscovery.Dispose();
    }

    [Fact]
    public async Task DiscoverHostsCommand_WhenNoHostsFound_ShouldUpdateStatusText()
    {
        _mockDiscoveryService
            .Setup(x => x.DiscoverHostsAsync(It.IsAny<TimeSpan>(), default))
            .ReturnsAsync(new List<string>());
        await _viewModel.DiscoverHostsCommand.ExecuteAsync(null);
        _viewModel.StatusText.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void AttachEmbeddedPairingService_WhenPinAlreadyActive_ShouldSyncCurrentState()
    {
        var service = new FakePairingService("123456", DateTimeOffset.UtcNow.AddMinutes(2));

        _viewModel.AttachEmbeddedPairingService(service);

        _viewModel.HasActivePairingPin.Should().BeTrue();
        _viewModel.ActivePairingPin.Should().Be("123456");
        _viewModel.ShowPairingPin.Should().BeTrue();
        _viewModel.PairingPinExpiresInText.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RefreshStandalonePairingPinAsync_WhenPinAppearsLater_ShouldSyncCurrentState()
    {
        var queryService = new FakeStandalonePairingPinQueryService(
            null,
            new PairingPinInfo("654321", DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds()));

        _viewModel.AttachStandalonePairingPinQueryService(queryService);

        await _viewModel.RefreshStandalonePairingPinAsync();
        _viewModel.HasActivePairingPin.Should().BeFalse();

        await _viewModel.RefreshStandalonePairingPinAsync();
        _viewModel.HasActivePairingPin.Should().BeTrue();
        _viewModel.ActivePairingPin.Should().Be("654321");
        _viewModel.ShowPairingPin.Should().BeTrue();
    }

    [Fact]
    public async Task RefreshStandalonePairingPinAsync_WhenEmbeddedHostInactive_ShouldSurfaceStandalonePin()
    {
        _viewModel.AttachEmbeddedPairingService(new FakePairingService(active: false));
        _viewModel.AttachStandalonePairingPinQueryService(
            new FakeStandalonePairingPinQueryService(
                new PairingPinInfo("777777", DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds())));

        await _viewModel.RefreshStandalonePairingPinAsync();

        _viewModel.HasActivePairingPin.Should().BeTrue();
        _viewModel.ActivePairingPin.Should().Be("777777");
        _viewModel.ShowPairingPin.Should().BeTrue();
    }

    [Fact]
    public async Task RevealPairingPinAsync_WhenEmbeddedPairingAlreadyActive_ReusesExistingSession()
    {
        var service = new FakePairingService("123456", DateTimeOffset.UtcNow.AddMinutes(2), active: true);
        _viewModel.AttachEmbeddedPairingService(service);

        await _viewModel.RevealPairingPinAsync();

        service.GetOrStartCalls.Should().Be(1);
        service.StartCalls.Should().Be(0);
        _viewModel.ActivePairingPin.Should().Be("123456");
        _viewModel.ShowPairingPin.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateQrCodeCommand_WhenEmbeddedPairingAlreadyActive_ReusesExistingSession()
    {
        var service = new FakePairingService("654321", DateTimeOffset.UtcNow.AddMinutes(2), active: true);
        _viewModel.AttachEmbeddedPairingService(service);
        _viewModel.HostAddress = "wss://192.168.1.25:5005/ws";

        var services = new ServiceCollection()
            .AddSingleton<ICertificateService>(new FakeCertificateService())
            .BuildServiceProvider();
        AppServicesBackingField.SetValue(null, services);

        await _viewModel.GenerateQrCodeCommand.ExecuteAsync(null);

        service.GetOrStartCalls.Should().Be(1);
        service.StartCalls.Should().Be(0);
        _viewModel.ActivePairingPin.Should().Be("654321");
    }

    [Fact]
    public async Task PrepareTlsValidationForConnectAsync_ReloadsPins_AndDisablesFirstTrustOnReconnect()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "remex-connection-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var storePath = Path.Combine(tempDir, "pinned_hosts.json");
            var store = new PinnedCertStore(NullLogger<PinnedCertStore>.Instance, storePath);
            var services = new ServiceCollection()
                .AddSingleton(store)
                .BuildServiceProvider();
            AppServicesBackingField.SetValue(null, services);
            _viewModel.HostAddress = "wss://192.168.1.25:5005/ws";

            await InvokePrepareTlsValidationForConnectAsync(allowTrustOnFirstUseForEmptyStore: true);

            ((IReadOnlyDictionary<string, string>)PinSnapshotField.GetValue(_viewModel)!).Should().BeEmpty();
            ((bool)AllowFirstTimeTrustField.GetValue(_viewModel)!).Should().BeTrue();

            await store.SetPinAsync("host-1", "hash-1==");

            await InvokePrepareTlsValidationForConnectAsync(allowTrustOnFirstUseForEmptyStore: false);

            var snapshot = (IReadOnlyDictionary<string, string>)PinSnapshotField.GetValue(_viewModel)!;
            snapshot.Should().ContainKey("host-1");
            ((bool)AllowFirstTimeTrustField.GetValue(_viewModel)!).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private async Task InvokePrepareTlsValidationForConnectAsync(bool allowTrustOnFirstUseForEmptyStore)
    {
        var task = (Task<Uri>)PrepareTlsValidationForConnectMethod.Invoke(
            _viewModel,
            new object[] { allowTrustOnFirstUseForEmptyStore })!;
        await task;
    }

}

internal sealed class FakePairingService : IPairingService
{
    private readonly string _pin;
    private readonly long _expiresAtUnixMs;
    private readonly bool _active;
    public int StartCalls { get; private set; }
    public int GetOrStartCalls { get; private set; }

    public FakePairingService(string pin = "123456", DateTimeOffset? expiresAt = null, bool active = true)
    {
        _pin = pin;
        _expiresAtUnixMs = (expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(2)).ToUnixTimeMilliseconds();
        _active = active;
    }

    public Task<PairingState> StartPairingAsync(CancellationToken ct)
    {
        StartCalls++;
        return Task.FromResult(new PairingState(string.Empty, _pin, _expiresAtUnixMs));
    }

    public Task<PairingState> GetOrStartPairingAsync(CancellationToken ct)
    {
        GetOrStartCalls++;
        return Task.FromResult(new PairingState(string.Empty, _pin, _expiresAtUnixMs));
    }

    public Task<string> DeriveSessionKeyAsync(string clientPublicKeyBase64, CancellationToken ct) =>
        Task.FromResult(string.Empty);

    public string GetActivePin() => _pin;

    public bool TryGetActivePinInfo(out string pin, out long expiresAtUnixMs)
    {
        pin = _pin;
        expiresAtUnixMs = _expiresAtUnixMs;
        if (!_active)
        {
            pin = string.Empty;
            expiresAtUnixMs = 0;
            return false;
        }

        pin = _pin;
        expiresAtUnixMs = _expiresAtUnixMs;
        return true;
    }

    public bool IsPairingActive => _active;

    public Task<bool> VerifyClientHmacAsync(string clientHmacBase64, CancellationToken ct) =>
        Task.FromResult(true);

    public void CancelPairing()
    {
    }

    public event Action<string, long>? PinDisplayed;
    public event Action? PinCleared;
}

internal sealed class FakeCertificateService : ICertificateService
{
    public Task<X509Certificate2> GetOrCreateCertificateAsync(CancellationToken ct) =>
        Task.FromException<X509Certificate2>(new NotSupportedException());

    public string GetSpkiSha256Base64() => "fake-spki-hash==";

    public Task RegenerateAsync(CancellationToken ct) => Task.CompletedTask;
}

internal sealed class FakeStandalonePairingPinQueryService : IPairingPinQueryService
{
    private readonly Queue<PairingPinInfo?> _responses;

    public FakeStandalonePairingPinQueryService(params PairingPinInfo?[] responses)
    {
        _responses = new Queue<PairingPinInfo?>(responses);
    }

    public Task<PairingPinInfo?> GetActivePairingPinAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : null);
    }

    public Task<PairingPinInfo?> GeneratePairingPinAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : null);
    }
}

// ---------------------------------------------------------------------------
// Correlated command infrastructure tests
// ---------------------------------------------------------------------------

/// <summary>
/// Tests for the <c>_pendingCommands</c> correlation dictionary and Cleanup behaviour.
/// <para>
/// NOTE — tests that require an open WebSocket (timeout cancellation, concurrent
/// correlated round-trips) are skipped here because <see cref="ConnectionViewModel"/>
/// uses <see cref="System.Net.WebSockets.ClientWebSocket"/> directly with no
/// injectable abstraction.  To enable those tests in the future:
/// <list type="bullet">
///   <item>Extract an <c>IWebSocketSender</c> interface from <c>MessageSerializer.SendAsync</c>.</item>
///   <item>Inject it into <see cref="ConnectionViewModel"/> so tests can provide a mock.</item>
///   <item>Expose <c>CommandTimeoutSeconds</c> as a constructor parameter so tests do not
///         have to wait 10 s for a timeout to fire.</item>
/// </list>
/// </para>
/// </summary>
public class ConnectionViewModelCorrelationTests : IDisposable
{
    private readonly ConnectionViewModel _viewModel;

    // Reflection helpers
    private static readonly FieldInfo PendingCommandsField =
        typeof(ConnectionViewModel).GetField(
            "_pendingCommands",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo ReceiveCtsField =
        typeof(ConnectionViewModel).GetField(
            "_receiveCts",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo ReconnectCtsField =
        typeof(ConnectionViewModel).GetField(
            "_reconnectCts",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo CleanupMethod =
        typeof(ConnectionViewModel).GetMethod(
            "Cleanup",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo StopReconnectingMethod =
        typeof(ConnectionViewModel).GetMethod(
            "StopReconnecting",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> GetPendingCommands()
        => (ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>>)
            PendingCommandsField.GetValue(_viewModel)!;

    private void InvokeCleanup() => CleanupMethod.Invoke(_viewModel, null);

    private void SetReceiveCts(CancellationTokenSource cancellationTokenSource)
        => ReceiveCtsField.SetValue(_viewModel, cancellationTokenSource);

    private void SetReconnectCts(CancellationTokenSource cancellationTokenSource)
        => ReconnectCtsField.SetValue(_viewModel, cancellationTokenSource);

    private void InvokeStopReconnecting() => StopReconnectingMethod.Invoke(_viewModel, null);

    public ConnectionViewModelCorrelationTests()
    {
        _viewModel = new ConnectionViewModel(null, null, null);
    }

    public void Dispose() => _viewModel.Dispose();

    /// <summary>
    /// Cleanup() must cancel every pending command TCS so callers do not hang after disconnect.
    /// </summary>
    [Fact]
    public async Task Cleanup_WithPendingCommands_ShouldCancelAllAwaiters()
    {
        var tcs1 = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs2 = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var dict = GetPendingCommands();
        dict["id-1"] = tcs1;
        dict["id-2"] = tcs2;

        InvokeCleanup();

        // Both tasks must be faulted/cancelled — awaiting them should throw
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tcs1.Task);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tcs2.Task);
    }

    /// <summary>
    /// Cleanup() must leave the dictionary empty so no stale entries remain.
    /// </summary>
    [Fact]
    public void Cleanup_WithPendingCommands_ShouldDrainDictionary()
    {
        var dict = GetPendingCommands();
        dict["id-1"] = new TaskCompletionSource<RemexMessage>();
        dict["id-2"] = new TaskCompletionSource<RemexMessage>();

        InvokeCleanup();

        dict.Should().BeEmpty();
    }

    [Fact]
    public void Cleanup_WithAlreadyDisposedReceiveTokenSource_ShouldNotThrow()
    {
        using var receiveCts = new CancellationTokenSource();
        receiveCts.Dispose();
        SetReceiveCts(receiveCts);

        var act = () => InvokeCleanup();

        act.Should().NotThrow();
    }

    [Fact]
    public void StopReconnecting_WithAlreadyDisposedTokenSource_ShouldNotThrow()
    {
        using var reconnectCts = new CancellationTokenSource();
        reconnectCts.Dispose();
        SetReconnectCts(reconnectCts);

        var act = () => InvokeStopReconnecting();

        act.Should().NotThrow();
    }

    // -- Correlated command send/receive (RemEx-h01r) -----------------------------
    //
    // Both behaviours were described in skipped tests from 2.0 and could not be written without a
    // send seam. They drive the REAL DeliverCommandResponse the receive loop uses, rather than a
    // re-implementation of correlation matching that could agree with itself while production broke.

    /// <summary>Records what was sent and never replies.</summary>
    private sealed class SilentSender : IWebSocketSender
    {
        public List<RemexMessage> Sent { get; } = new();

        public Task SendAsync(RemexMessage message, CancellationToken ct)
        {
            lock (Sent) Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SendCommandAndWaitAsync_WhenHostNeverResponds_ThrowsOperationCanceled()
    {
        var vm = new ConnectionViewModel(null, null, null);
        var sender = new SilentSender();
        vm.CommandSender = sender;
        vm.CommandTimeout = TimeSpan.FromMilliseconds(150);

        var act = async () => await vm.SendCommandAndWaitAsync(
            new RemexMessage { Type = MessageTypes.Command });

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a command whose response never arrives must fail rather than hang the caller");

        sender.Sent.Should().ContainSingle("the message goes out before the wait begins")
            .Which.CorrelationId.Should().NotBeNullOrEmpty(
                "the correlation ID is stamped on the way out, and is what a response is matched against");
    }

    [Fact]
    public async Task SendCommandAndWaitAsync_ConcurrentCalls_EachReceivesItsOwnResponse()
    {
        var vm = new ConnectionViewModel(null, null, null);
        var sender = new SilentSender();
        vm.CommandSender = sender;
        vm.CommandTimeout = TimeSpan.FromSeconds(5);

        var first = vm.SendCommandAndWaitAsync(new RemexMessage { Type = MessageTypes.Command });
        var second = vm.SendCommandAndWaitAsync(new RemexMessage { Type = MessageTypes.Command });

        // Both must be in flight before either is answered. That is exactly the condition under
        // which the former single pending-response field lost one of the two callers.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            lock (sender.Sent)
            {
                if (sender.Sent.Count == 2) break;
            }
            DateTime.UtcNow.Should().BeBefore(deadline, "both commands should have been sent");
            await Task.Delay(5);
        }

        string[] ids;
        lock (sender.Sent) ids = sender.Sent.Select(m => m.CorrelationId!).ToArray();
        ids.Should().OnlyHaveUniqueItems("each in-flight command needs its own correlation ID");

        // Answered in REVERSE order, so a pass cannot mean "the replies happened to arrive in order".
        vm.DeliverCommandResponse(new RemexMessage
        {
            Type = MessageTypes.CommandResponse,
            CorrelationId = ids[1],
            ErrorText = "second",
        });
        vm.DeliverCommandResponse(new RemexMessage
        {
            Type = MessageTypes.CommandResponse,
            CorrelationId = ids[0],
            ErrorText = "first",
        });

        (await first).ErrorText.Should().Be("first");
        (await second).ErrorText.Should().Be("second");
    }
}
