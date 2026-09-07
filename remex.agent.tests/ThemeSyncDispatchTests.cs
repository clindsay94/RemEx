using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.Media;
using Remex.Agent.Services.Security;
using Remex.Agent.Services.Theme;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Core.Services.Theme;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The host end of <c>theme_sync</c> (RemEx-y06a0.1, RemEx-sudp8): a valid snapshot lands in
/// <see cref="IPhoneThemeSnapshotStore"/> exactly once, stamped with the connection's clientId, and
/// a malformed one is dropped rather than stored.
/// </summary>
/// <remarks>
/// Same shape as <c>MediaSeekDispatchTests</c> next door: driven through the real
/// <c>switch</c> in <c>HandleAsync</c> rather than by calling a method directly, because this
/// message also has no reply on the wire — a routing mistake here would present as "Match my phone"
/// silently never appearing, with nothing in any log.
/// </remarks>
public class ThemeSyncDispatchTests
{
    /// <summary>Delivers a scripted list of messages, one per receive, then closes.</summary>
    private sealed class ScriptedWebSocket(params RemexMessage[] script) : WebSocket
    {
        private int _next;

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool e, CancellationToken c)
            => Task.CompletedTask;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
        {
            if (_next >= script.Length)
            {
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }

            var payload = MessageSerializer.Serialize(script[_next++]);
            payload.CopyTo(b.Array.AsSpan(b.Offset));
            return Task.FromResult(
                new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true));
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override void Dispose() { }
    }

    /// <summary>
    /// Same as <see cref="ScriptedWebSocket"/> but delivers raw JSON text frames verbatim — needed
    /// to reproduce a key ENTIRELY ABSENT from the wire (e.g. no "seed" at all), which a
    /// <see cref="RemexMessage"/> built in C# and re-serialized cannot: a property left unset there
    /// still carries its own field initializer and serializes present-with-a-value, not absent.
    /// </summary>
    private sealed class RawScriptedWebSocket(params string[] script) : WebSocket
    {
        private int _next;

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool e, CancellationToken c)
            => Task.CompletedTask;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
        {
            if (_next >= script.Length)
            {
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }

            var payload = System.Text.Encoding.UTF8.GetBytes(script[_next++]);
            payload.CopyTo(b.Array.AsSpan(b.Offset));
            return Task.FromResult(
                new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true));
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override void Dispose() { }
    }

    private static PhoneThemeSnapshot Valid(string clientId = "phone-1") => new()
    {
        SeedHex = "#6750A4",
        Style = "tonal_spot",
        Mode = "dark",
        Contrast = 0.5,
        DynamicColor = false,
        SentAtUnixMs = 1_700_000_000_000,
        ClientId = clientId, // the phone never actually sends this; harmless if it tried to
    };

    private static RemexMessage Sync(PhoneThemeSnapshot snapshot, string? clientId = "phone-1") => new()
    {
        Type = MessageTypes.ThemeSync,
        ClientId = clientId,
        ThemeSync = snapshot,
    };

    [Fact]
    public async Task AValidSyncIsStoredOnceAndRaisesChangedOnce()
    {
        var store = new PhoneThemeSnapshotStore();
        var changedCount = 0;
        PhoneThemeSnapshot? lastRaised = null;
        store.Changed += s => { changedCount++; lastRaised = s; };

        await RunAsync(store, Sync(Valid()));

        Assert.NotNull(store.Latest);
        Assert.Equal("#6750A4", store.Latest!.SeedHex);
        Assert.Equal(1, changedCount);
        Assert.Same(store.Latest, lastRaised);
    }

    [Fact]
    public async Task TheStoredSnapshotIsStampedWithTheSessionsIdentityNotThePayloads()
    {
        // Loopback never proves an identity (RemEx-4215): an unauthenticated local connection is
        // frozen at NO clientId by design, so the correct stamp here is null - and that is exactly
        // what this pins. The payload's own ClientId (which the real phone never sends) must not
        // leak into the store as a substitute for a session identity that was never proved.
        var store = new PhoneThemeSnapshotStore();

        await RunAsync(store, Sync(Valid(clientId: "impersonated")));

        Assert.Null(store.Latest!.ClientId);
    }

    [Theory]
    [InlineData("not-a-hex-color")]
    [InlineData("#GGGGGG")]
    [InlineData("6750A4")]
    public async Task AMalformedSeedIsNotStored(string badSeed)
    {
        var store = new PhoneThemeSnapshotStore();
        await RunAsync(store, Sync(Valid() with { SeedHex = badSeed }));
        Assert.Null(store.Latest);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-1.5)]
    public async Task AContrastOutsideTheSignedRangeIsNotStored(double badContrast)
    {
        var store = new PhoneThemeSnapshotStore();
        await RunAsync(store, Sync(Valid() with { Contrast = badContrast }));
        Assert.Null(store.Latest);
    }

    [Fact]
    public async Task NegativeContrastWithinAndroidsRangeIsAcceptedHere()
    {
        // -1.0..1.0 is Android's own M3 contrast range (negative = reduced contrast). Clamping that
        // into the PC's 0.0..1.0 is CustomizationViewModel.TryMapPhoneTheme's job, not a reason for
        // the host to refuse the whole message.
        var store = new PhoneThemeSnapshotStore();
        await RunAsync(store, Sync(Valid() with { Contrast = -0.5 }));
        Assert.NotNull(store.Latest);
        Assert.Equal(-0.5, store.Latest!.Contrast);
    }

    [Theory]
    [InlineData("SYSTEM")]
    [InlineData("")]
    [InlineData("sideways")]
    public async Task AnUnrecognisedModeIsNotStored(string badMode)
    {
        var store = new PhoneThemeSnapshotStore();
        await RunAsync(store, Sync(Valid() with { Mode = badMode }));
        Assert.Null(store.Latest);
    }

    [Fact]
    public async Task ABlankStyleIsNotStored()
    {
        var store = new PhoneThemeSnapshotStore();
        await RunAsync(store, Sync(Valid() with { Style = "   " }));
        Assert.Null(store.Latest);
    }

    [Fact]
    public async Task AMessageWithNoPayloadTouchesNothing()
    {
        var store = new PhoneThemeSnapshotStore();
        await RunAsync(store, new RemexMessage { Type = MessageTypes.ThemeSync });
        Assert.Null(store.Latest);
    }

    [Fact]
    public async Task ASeedKeyEntirelyAbsentIsRejectedWithoutEndingTheSession()
    {
        // RemEx-sudp8 fix round (Opus review, HIGH). Before: SeedHex/Style/Mode/Contrast/DynamicColor
        // /SentAtUnixMs were all `required`, so a theme_sync missing ANY of them made
        // System.Text.Json throw, MessageSerializer.Deserialize return null, and PingPongHandler's
        // receive loop (HandleAsync:207, `if (message is null) break;`) treat that exactly like the
        // peer closing the socket — dropping the phone's WHOLE control session over one absent
        // field, silently, before IsValidThemeSync ever ran. Now: no field is `required`, so this
        // deserializes fine (SeedHex is null), IsValidThemeSync rejects it explicitly, and — the
        // point of this test — the SAME connection goes on to accept the next, well-formed sync.
        var store = new PhoneThemeSnapshotStore();

        await RunRawAsync(
            store,
            """{"type":"theme_sync","protocolVersion":2,"clientId":"phone-1","themeSync":{"style":"tonal_spot","mode":"dark","contrast":0.5,"dynamic":false,"sentAtUnixMs":1700000000000}}""",
            """{"type":"theme_sync","protocolVersion":2,"clientId":"phone-1","themeSync":{"seed":"#6750A4","style":"tonal_spot","mode":"dark","contrast":0.5,"dynamic":false,"sentAtUnixMs":1700000000000}}""");

        Assert.NotNull(store.Latest);
        Assert.Equal("#6750A4", store.Latest!.SeedHex);
    }

    [Fact]
    public async Task ARawValidMessageWithNoRequiredFieldsRoundTripsEndToEnd()
    {
        // Sanity base case for RawScriptedWebSocket itself: a genuinely complete raw JSON frame
        // (protocolVersion included, unlike the earlier live-check omission this fix round traced)
        // must reach the store exactly like the RemexMessage-object-based tests above.
        var store = new PhoneThemeSnapshotStore();
        var log = new CapturingLogger();
        await RunRawAsync(
            store,
            log,
            """{"type":"theme_sync","protocolVersion":2,"clientId":"phone-1","themeSync":{"seed":"#6750A4","style":"tonal_spot","mode":"dark","contrast":0.5,"dynamic":false,"sentAtUnixMs":1700000000000}}""");
        Assert.True(store.Latest is not null, "LOG:\n" + string.Join("\n", log.Lines));
    }

    /// <summary>
    /// Surfaces every log line HandleAsync emits for a connection, so a test that fails
    /// unexpectedly shows WHY rather than just that it did — this is what caught the
    /// protocolVersion gate the first raw-JSON attempt at this fix round tripped over.
    /// </summary>
    private sealed class CapturingLogger : ILogger<PingPongHandler>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Lines.Add($"[{level}] {formatter(state, ex)}{(ex is null ? "" : " EX:" + ex)}");
    }

    /// <summary>Drives one whole loopback connection carrying raw JSON text frames.</summary>
    private static Task RunRawAsync(IPhoneThemeSnapshotStore store, params string[] script) =>
        RunRawAsync(store, NullLogger<PingPongHandler>.Instance, script);

    private static async Task RunRawAsync(IPhoneThemeSnapshotStore store, ILogger<PingPongHandler> logger, params string[] script)
    {
        var socket = new RawScriptedWebSocket(script);
        await DriveAsync(socket, store, logger);
    }

    /// <summary>Drives one whole loopback connection carrying <paramref name="script"/>.</summary>
    private static async Task RunAsync(IPhoneThemeSnapshotStore store, params RemexMessage[] script)
    {
        var socket = new ScriptedWebSocket(script);
        await DriveAsync(socket, store, NullLogger<PingPongHandler>.Instance);
    }

    private static async Task DriveAsync(WebSocket socket, IPhoneThemeSnapshotStore store, ILogger<PingPongHandler> logger)
    {
        var handler = new PingPongHandler(
            logger,
            null!,
            Mock.Of<Remex.Core.Services.Command.ISystemCommandService>(),
            Mock.Of<Remex.Core.Services.Network.IWakeOnLanService>(),
            Mock.Of<ILauncherStorageService>(),
            Mock.Of<IAppLauncherService>(),
            Mock.Of<IDashboardProfileStorageService>(),
            Mock.Of<IProcessMonitorService>(),
            Mock.Of<IHostCapabilitiesProvider>(),
            Mock.Of<IInputSimulationService>(),
            null!,
            null!,
            NewFileTransferHandler(),
            null!,
            null!,
            null!,
            new ClientSessionRegistry(),
            NewNameStore(),
            NewActivityStore(),
            new FakeHostClipboard(),
            Mock.Of<IMediaSessionMonitor>(),
            store);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await handler.HandleAsync(
                socket,
                isLoopback: true,
                isTrustedForPinAutoFetch: false,
                remoteAddress: "127.0.0.1",
                cts.Token);
        }
        finally
        {
            handler.Dispose();
        }
    }

    private static FileTransferHandler NewFileTransferHandler()
    {
        var svc = Mock.Of<Remex.Core.Services.FileTransfer.IFileTransferService>();
        var trust = Mock.Of<Remex.Core.Services.FileTransfer.IFileTrustService>();
        var volumes = new Remex.Agent.Services.FileTransfer.VolumeEnumerator(
            NullLogger<Remex.Agent.Services.FileTransfer.VolumeEnumerator>.Instance);
        return new FileTransferHandler(
            NullLogger<FileTransferHandler>.Instance, svc, trust, volumes,
            new Remex.Agent.Services.FileTransfer.SharedRootReadResolver(svc, trust, volumes));
    }

    private static PairedClientNameStore NewNameStore() =>
        new(NullLogger<PairedClientNameStore>.Instance,
            Path.Combine(Path.GetTempPath(), $"remex-names-{Guid.NewGuid():N}.json"));

    private static PairedDeviceActivityStore NewActivityStore() =>
        new(NullLogger<PairedDeviceActivityStore>.Instance,
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
}
