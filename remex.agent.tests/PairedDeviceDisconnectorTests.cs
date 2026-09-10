using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Services;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.RemoteDesktop;
using Remex.Agent.Services.Security;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Core.Services.FileTransfer;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Unpairing a device cuts it off NOW, not at its next connection attempt (RemEx-6nkht).
/// </summary>
/// <remarks>
/// <para>
/// <c>IsClientPaired</c> is consulted when a connection is ESTABLISHED and nowhere afterwards, so
/// before this every revocation was a promise about the future: a phone already mirroring the desktop
/// kept mirroring it, and kept controlling it, for as long as it liked. The confirmation the user
/// reads says the device "will no longer be able to connect to this PC", and these tests are what
/// makes that sentence about the present tense.
/// </para>
/// <para>
/// THREE CHANNELS, TESTED SEPARATELY, because they end in three different ways: the control socket is
/// aborted, the desktop loop is CANCELLED so its drain contract still runs, and the file socket is
/// aborted so that the channel's own finally suspends partial transfers rather than losing them.
/// </para>
/// </remarks>
public sealed class PairedDeviceDisconnectorTests : IDisposable
{
    private const string Phone = "phone-a";
    private const string Other = "phone-b";

    private readonly DirectoryInfo _staging = Directory.CreateTempSubdirectory();

    public void Dispose()
    {
        _staging.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    // ── The control channel ────────────────────────────────────────────────────

    [Fact]
    public void TheControlSocketOfTheRevokedDeviceIsCutAndNobodyElsesIs()
    {
        var registry = new ClientSessionRegistry();
        var revoked = Attach(registry, Phone);
        var bystander = Attach(registry, Other);

        Assert.Equal(1, registry.DisconnectClient(Phone));

        Assert.True(revoked.Aborted, "the revoked device must not still be holding a control channel");
        Assert.False(bystander.Aborted, "unpairing one phone must not knock another one offline");
    }

    [Fact]
    public void BOTHSocketsOfADeviceThatRedialledAreCut()
    {
        // NOT Find()'s NEWEST ONE. A phone that dropped and redialled leaves two entries, and the
        // stale one is usually still WebSocketState.Open — that is what "the disconnect has not been
        // noticed yet" means, since a socket only leaves Open on a failed I/O or a close handshake.
        // Cutting only the newest would leave a live socket belonging to an unpaired device, which is
        // the whole failure this fixes.
        var registry = new ClientSessionRegistry();
        var stale = Attach(registry, Phone);
        var fresh = Attach(registry, Phone);

        Assert.Equal(2, registry.DisconnectClient(Phone));

        Assert.True(stale.Aborted);
        Assert.True(fresh.Aborted);
    }

    [Fact]
    public void ASessionThatMerelyCLAIMSTheIdIsLeftAlone()
    {
        // THE LOOPBACK RULE, mirrored from Find(). Loopback clears the pairing gate by construction —
        // it is the PC talking to itself — and never proves an identity, so it can claim any client
        // id it likes. Without this filter any local process could open /ws, name a paired phone, and
        // have the PC's own in-process connection cut every time that phone was unpaired.
        var registry = new ClientSessionRegistry();
        var claimant = new RecordingSocket();
        var handle = registry.Register("127.0.0.1", claimant);
        registry.Identify(handle, Phone, deviceName: null);
        registry.MarkAuthenticated(handle, identityProven: false);

        Assert.Equal(0, registry.DisconnectClient(Phone));
        Assert.False(claimant.Aborted);
    }

    [Fact]
    public void CuttingAnUnknownOrBlankClientIsHarmless()
    {
        var registry = new ClientSessionRegistry();
        var live = Attach(registry, Phone);

        Assert.Equal(0, registry.DisconnectClient("never-paired"));
        Assert.Equal(0, registry.DisconnectClient("   "));
        Assert.Equal(0, registry.DisconnectClient(null));
        Assert.False(live.Aborted);
    }

    // ── The screen stream ──────────────────────────────────────────────────────

    [Fact]
    public async Task TheDesktopStreamIsCANCELLEDRatherThanKilled()
    {
        // Cancelled, not aborted, and the difference is the drain contract: TakeOverAsync promises
        // that a loop ends by its token and then calls MarkDrained in a finally. Killing the socket
        // underneath it would end the loop through an exception path instead, leaving the capture
        // session torn down by whatever unwinding happened to run.
        var registry = new DesktopSessionRegistry(NullLogger<DesktopSessionRegistry>.Instance);
        using var revoked = await registry.TakeOverAsync(Phone, TimeSpan.FromSeconds(1), CancellationToken.None);
        using var bystander = await registry.TakeOverAsync(Other, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.True(await registry.CancelSessionsForAsync(Phone));

        Assert.True(revoked.IsCancellationRequested, "the stream loop for a revoked device must stop");
        Assert.False(bystander.IsCancellationRequested, "another phone's stream must be untouched");
    }

    [Fact]
    public async Task ADesktopSessionThatAlreadyDrainedIsNotCancelledTwice()
    {
        var registry = new DesktopSessionRegistry(NullLogger<DesktopSessionRegistry>.Instance);
        var cts = await registry.TakeOverAsync(Phone, TimeSpan.FromSeconds(1), CancellationToken.None);
        registry.MarkDrained(Phone, cts);
        cts.Dispose();

        Assert.False(await registry.CancelSessionsForAsync(Phone));
        Assert.False(await registry.CancelSessionsForAsync(Other), "a client with no stream at all");
        Assert.False(await registry.CancelSessionsForAsync("   "), "blank ids are loopback and have no pairing");
    }

    // ── The file channel ───────────────────────────────────────────────────────

    [Fact]
    public async Task TheFileChannelIsCutAndItsOwnFinallyDoesTheTeardown()
    {
        // ONLY THE SOCKET, deliberately. RunChannelAsync's finally already suspends this client's
        // receive sessions (keeping partials so a transfer can resume) and cancels its sends; doing
        // that a second time from this thread is how a partial gets deleted out from under a suspend.
        // So the assertion is that the socket died and the LOOP came home on its own.
        var manager = NewTransferManager();
        var socket = new BlockingSocket();
        var loop = manager.RunChannelAsync(Phone, socket, CancellationToken.None);
        await socket.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(manager.DisconnectClient(Phone));

        Assert.True(socket.Aborted);
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void CuttingAFileChannelThatIsNotThereIsHarmless()
    {
        var manager = NewTransferManager();

        Assert.False(manager.DisconnectClient(Phone));
        Assert.False(manager.DisconnectClient("   "));
    }

    [Fact]
    public async Task ASendOnACutChannelPutsNothingOnTheWire()
    {
        // THE OTHER HALF OF FileChannel.Abort, and it had no guard at all: deleting `_superseded =
        // true` and leaving only the socket abort broke nothing in the whole agent suite (review).
        // The stake is confidentiality, not tidiness — a send already past its own superseded check
        // when the user unpairs would otherwise put another frame of somebody else's file onto a
        // socket the user has just cut.
        var socket = new BlockingSocket();
        var channel = new TransferSessionManager.FileChannel(socket);

        channel.Abort();
        await channel.SendFrameAsync(
            new FileFrameEnvelope { Kind = "data", TransferId = "t1", Offset = 0, Length = 1 },
            new byte[] { 7 },
            CancellationToken.None);

        Assert.Equal(0, socket.Sends);
    }

    [Fact]
    public async Task ADesktopSessionWhoseCtsWasDisposedUnderUsIsNotAFault()
    {
        // THE REAL RACE, which the already-drained test above cannot reach: MarkDrained removes the
        // entry BEFORE the handler's `using var sessionCts` disposes it, so that path short-circuits
        // at the lookup and never calls Cancel. Here the entry is still registered and the CTS is
        // already gone — the window between a handler finishing and its finally completing (review).
        var registry = new DesktopSessionRegistry(NullLogger<DesktopSessionRegistry>.Instance);
        var cts = await registry.TakeOverAsync(Phone, TimeSpan.FromSeconds(1), CancellationToken.None);
        cts.Dispose();

        Assert.False(await registry.CancelSessionsForAsync(Phone), "a disposed session is not a cancelled one");
    }

    // ── All three together ─────────────────────────────────────────────────────

    [Fact]
    public async Task TheDisconnectorCutsALLTHREEChannelsInOneCall()
    {
        // THE JOIN. Each channel ends correctly on its own above; this is the one that fails if a
        // future edit drops one of the three calls, which is the shape of the bug being fixed — the
        // revocation looked complete because two of the three were handled.
        var sessions = new ClientSessionRegistry();
        var desktops = new DesktopSessionRegistry(NullLogger<DesktopSessionRegistry>.Instance);
        var transfers = NewTransferManager();

        var control = Attach(sessions, Phone);
        using var stream = await desktops.TakeOverAsync(Phone, TimeSpan.FromSeconds(1), CancellationToken.None);
        var files = new BlockingSocket();
        var loop = transfers.RunChannelAsync(Phone, files, CancellationToken.None);
        await files.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await new PairedDeviceDisconnector(
            sessions, desktops, transfers, NullLogger<PairedDeviceDisconnector>.Instance)
            .DisconnectAsync(Phone);

        Assert.True(control.Aborted, "the control channel survived the revocation");
        Assert.True(stream.IsCancellationRequested, "the screen stream survived the revocation");
        Assert.True(files.Aborted, "the file channel survived the revocation");
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ItAllHappensOffTheCallersThread()
    {
        // THE GUARD ON THE FIX, which review found deletes clean: removing the Task.Run inside
        // DisconnectAsync broke nothing in the whole agent suite, so the answer to a HIGH was one
        // keystroke from being undone with the board still green. It reads as an anti-pattern to
        // anyone who has not read the comment above it, which is exactly how it would go.
        //
        // WHY THE CONTEXT AND NOT THE THREAD ID: the caller here releases its thread at the await, so
        // the pool is free to hand the same one straight back and an id comparison would flake. A
        // synchronization context is not captured by Task.Run, so its absence inside is a fact rather
        // than a race — and the context is the thing that actually matters, because it is what would
        // drag the unwind back onto the Avalonia dispatcher.
        var sessions = new ClientSessionRegistry();
        var control = Attach(sessions, Phone);
        var disconnector = new PairedDeviceDisconnector(
            sessions,
            new DesktopSessionRegistry(NullLogger<DesktopSessionRegistry>.Instance),
            NewTransferManager(),
            NullLogger<PairedDeviceDisconnector>.Instance);

        // RESTORED BEFORE THE AWAIT, NOT IN A FINALLY AROUND IT (review). The await captures `caller`,
        // so a finally after it resumes on a pool thread via caller.Post — restoring the previous
        // context onto a thread that never had `caller` installed, and leaving it installed on the one
        // that did. Without the Task.Run the abort happens in the synchronous portion, so the meaning
        // of the test is preserved by capturing the task first.
        Task cut;
        var caller = new SynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(caller);
        try
        {
            cut = disconnector.DisconnectAsync(Phone);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await cut;

        Assert.True(control.Aborted);
        Assert.True(control.AbortedUnder is null, "the abort must not run under the caller's context");
    }

    [Fact]
    public async Task ONEChannelFailingDoesNotSpareTheOtherTwo()
    {
        // "Best effort, and each channel independently so" was a claim in the class docs that nothing
        // held (review) — both catch blocks were entered by no test at all. The control channel is the
        // one that can now throw something other than ObjectDisposedException, since narrowing that
        // catch, so it is the honest place to inject.
        var sessions = new ClientSessionRegistry();
        var socket = new RecordingSocket { AbortThrows = new InvalidOperationException("the socket is wedged") };
        var handle = sessions.Register("192.168.1.50", socket);
        sessions.Identify(handle, Phone, deviceName: null);
        sessions.MarkAuthenticated(handle, identityProven: true);

        var desktops = new DesktopSessionRegistry(NullLogger<DesktopSessionRegistry>.Instance);
        using var stream = await desktops.TakeOverAsync(Phone, TimeSpan.FromSeconds(1), CancellationToken.None);
        var transfers = NewTransferManager();
        var files = new BlockingSocket();
        var loop = transfers.RunChannelAsync(Phone, files, CancellationToken.None);
        await files.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await new PairedDeviceDisconnector(
            sessions, desktops, transfers, NullLogger<PairedDeviceDisconnector>.Instance)
            .DisconnectAsync(Phone);

        Assert.True(stream.IsCancellationRequested, "a wedged control socket must not spare the screen stream");
        Assert.True(files.Aborted, "a wedged control socket must not spare the file channel");
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private static RecordingSocket Attach(ClientSessionRegistry registry, string clientId)
    {
        var socket = new RecordingSocket();
        var handle = registry.Register("192.168.1.50", socket);
        registry.Identify(handle, clientId, deviceName: null);
        registry.MarkAuthenticated(handle, identityProven: true);
        return socket;
    }

    private TransferSessionManager NewTransferManager()
    {
        var files = Mock.Of<IFileTransferService>();
        var resolver = new SharedRootReadResolver(
            files, Mock.Of<IFileTrustService>(), new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance));
        return new TransferSessionManager(
            NullLogger<TransferSessionManager>.Instance, files, resolver, _staging.FullName);
    }

    /// <summary>Open until somebody aborts it, and remembers that they did.</summary>
    private class RecordingSocket : WebSocket
    {
        private volatile WebSocketState _state = WebSocketState.Open;

        public bool Aborted => _state == WebSocketState.Aborted;

        /// <summary>The synchronization context in force when somebody aborted this socket.</summary>
        /// <remarks>
        /// Null means it happened on a plain thread-pool thread, which is what the Task.Run hop
        /// exists to guarantee. See ItAllHappensOffTheCallersThread.
        /// </remarks>
        public SynchronizationContext? AbortedUnder { get; private set; }

        /// <summary>Set to make Abort fail the way a disposed socket would.</summary>
        public Exception? AbortThrows { get; init; }

        /// <summary>How many frames actually reached the wire.</summary>
        public int Sends;

        public override void Abort()
        {
            AbortedUnder = SynchronizationContext.Current;
            if (AbortThrows is not null) throw AbortThrows;
            _state = WebSocketState.Aborted;
        }
        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType t, bool e, CancellationToken c)
        {
            Interlocked.Increment(ref Sends);
            return Task.CompletedTask;
        }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
            => throw new NotSupportedException("nothing in these tests reads");
    }

    /// <summary>
    /// Parks in the receive the way a real idle file channel does, and comes home when aborted.
    /// </summary>
    /// <remarks>
    /// Entering the receive is the signal that <c>RunChannelAsync</c> has registered the channel —
    /// waiting on that rather than sleeping is what keeps this test from being timing-dependent.
    /// </remarks>
    private sealed class BlockingSocket : RecordingSocket
    {
        private readonly TaskCompletionSource _aborted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Abort()
        {
            base.Abort();
            _aborted.TrySetResult();
        }

        /// <remarks>
        /// AN ABORT IS NOT A CLEAN CLOSE, and the first version of this modelled it as one (review).
        /// A parked receive on a socket somebody aborts does not return a Close result — .NET's
        /// ManagedWebSocket raises OperationCanceledException(nameof(WebSocketState.Aborted)), which
        /// this repo documents at ClientSessionRegistry.TrySendAsync. Returning Close drove the one
        /// exit from RunChannelAsync that needs no exception handling at all, leaving both of the
        /// catch clauses production actually takes untouched by the test.
        /// </remarks>
        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer, CancellationToken ct)
        {
            Entered.TrySetResult();
            await _aborted.Task.WaitAsync(ct);
            throw new OperationCanceledException(nameof(WebSocketState.Aborted));
        }
    }
}
