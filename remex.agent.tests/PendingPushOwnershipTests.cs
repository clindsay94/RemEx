using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Services.FileTransfer;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Core.Services.FileTransfer;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// A host-initiated push is answered by the peer it was offered to, not by whoever replies first
/// (RemEx-5dq3).
/// </summary>
/// <remarks>
/// <para>
/// The fifth control-plane entry point that keys on a transfer id, left out of RemEx-juas — which
/// bound the other four — on reasoning that was half right. <c>IsForeignTransfer</c> genuinely cannot
/// answer here: a pending push has no receive session, no send session and no staging manifest yet,
/// because the ready IS the handshake that precedes all three. What was wrong was the conclusion that
/// no owner was available. <see cref="TransferSessionManager.PushFileAsync"/> holds the client id at
/// the moment it registers the wait and simply discarded it.
/// </para>
/// <para>
/// THE REACHABLE ATTACKER IS LOOPBACK, which is authenticated by construction and frozen to the empty
/// client id since RemEx-4215 — so it owns no paired device's transfer, which is exactly what makes
/// the empty key safe to compare against.
/// </para>
/// </remarks>
public sealed class PendingPushOwnershipTests : IDisposable
{
    private const string Phone = "phone-a";
    private const string Transfer = "transfer-1";

    private readonly DirectoryInfo _staging = Directory.CreateTempSubdirectory();
    private readonly CapturingLogger _log = new();

    public void Dispose()
    {
        _staging.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AStrangerCannotDECLINEAPushOfferedToSomebodyElse()
    {
        // THE BUG, end to end. An unbound connection sends file_transfer_ready { accepted: false } for
        // a transfer id it merely knows and wins the race against the phone that already consented.
        // PushFileAsync returns false and the user watches a transfer they agreed to not arrive —
        // "they accepted, and nothing came", which is the failure the comments on that path warn about.
        var manager = NewManager();
        var (push, socket) = StartPush(manager);
        await socket.OfferSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        manager.HandleReady(new FileTransferReady { TransferId = Transfer, Accepted = false }, channelKey: string.Empty);

        // ASSERTED ON THE REFUSAL, NOT ON push.IsCompleted — which was inert and injection caught it.
        // The outer task does not complete the instant its wait is satisfied: PushFileAsync goes on to
        // look up a channel and stream, so "not finished yet" is true either way for a while. Polling
        // it would have been a race dressed as an assertion. The log line IS the decision.
        Assert.Contains(_log.Warnings, w => w.Contains("Refusing a file_transfer_ready"));

        // And the real peer's answer still lands, so the guard refuses rather than deafens.
        manager.HandleReady(new FileTransferReady { TransferId = Transfer, Accepted = false }, channelKey: Phone);

        // BOUNDED, because `await push` alone cannot fail (review): false is also what
        // PushFileAsync returns when NOTHING is ever delivered and its 30s deadline expires, so a
        // guard regressed to refuse everyone would pass this — just thirty seconds slower.
        Assert.False(await push.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AStrangerCannotACCEPTAPushOfferedToSomebodyElse()
    {
        // accepted:true early is the same defect by a different route: it satisfies the wait, then the
        // channel lookup that follows misses because the stranger has no file channel, and the
        // transfer fails just as silently.
        var manager = NewManager();
        var (push, socket) = StartPush(manager);
        await socket.OfferSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        manager.HandleReady(new FileTransferReady { TransferId = Transfer, Accepted = true }, channelKey: "someone-else");

        Assert.Contains(_log.Warnings, w => w.Contains("Refusing a file_transfer_ready"));

        manager.HandleReady(new FileTransferReady { TransferId = Transfer, Accepted = false }, channelKey: Phone);

        // BOUNDED, because `await push` alone cannot fail (review): false is also what
        // PushFileAsync returns when NOTHING is ever delivered and its 30s deadline expires, so a
        // guard regressed to refuse everyone would pass this — just thirty seconds slower.
        Assert.False(await push.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TheOWNERSAnswerIsAccepted()
    {
        // THE FLOOR. Without this the guard could refuse everyone — including the peer the push was
        // offered to — and both tests above would still pass, because both prove a negative.
        var manager = NewManager();
        var (push, socket) = StartPush(manager);
        await socket.OfferSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        manager.HandleReady(new FileTransferReady { TransferId = Transfer, Accepted = false }, channelKey: Phone);

        var completed = await Task.WhenAny(push, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(push, completed);
        Assert.False(await push, "the peer declined, which is a completed handshake and not a hang");
        Assert.DoesNotContain(_log.Warnings, w => w.Contains("Refusing a file_transfer_ready"));
    }

    [Fact]
    public async Task AFinishedPushDoesNotEvictTheONETHATTOOKItsSlot()
    {
        // COMPARE AND REMOVE (RemEx-6e3mn). The finally used to remove by transfer id alone, so it
        // evicted whatever was registered under that id — including a SECOND push that had taken the
        // slot, which would then be stranded until its own 30s deadline by the first one's cleanup.
        //
        // Driven by cancelling the first push rather than answering it: its answer would be refused
        // as a stranger's now that the slot belongs to somebody else, and it would sit there for the
        // full deadline. Cancelling runs the same finally in milliseconds.
        var manager = NewManager();
        using var firstToken = new CancellationTokenSource();

        var (first, firstSocket) = StartPush(manager, Phone, firstToken.Token);
        await firstSocket.OfferSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var (second, secondSocket) = StartPush(manager, "phone-b", CancellationToken.None);
        await secondSocket.OfferSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await firstToken.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        // The second push's wait must still be registered: its owner's answer resolves it promptly.
        manager.HandleReady(new FileTransferReady { TransferId = Transfer, Accepted = false }, channelKey: "phone-b");

        Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(5)),
            "the first push's cleanup evicted the second's wait, which then hangs to its own deadline");
    }

    [Fact]
    public async Task AMissingFileChannelSaysWhatWasLookedUpAndWhatIsThere()
    {
        // SERVES RemEx-6bfyt (RemEx-3ipz3). Both transfer directions fail at this one lookup and the
        // message said neither which key was sought nor which are registered — so client-id SKEW and
        // "the phone never opened /ws/files at all" are indistinguishable from the phone, and the
        // reported symptom is identical either way. An EMPTY registered set means the second; a
        // non-empty one that lacks the key means the first.
        // A REALISTIC ID, NOT THE SHORT FIXTURE ONE. RedactClientId elides a prefix, so on "phone-a"
        // it is a no-op and the redaction assertion below would be measuring the fixture's length
        // rather than the code. Production ids are UUIDs.
        const string realisticId = "3f2b9c14-77ad-4e01-9a6c-8d5512ee0b73";

        var log = new CapturingLogger();
        var manager = NewManager(log);
        var (push, socket) = StartPush(manager, realisticId, CancellationToken.None);
        await socket.OfferSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The peer accepts, and there is no file channel for it — the reported failure exactly.
        manager.HandleReady(new FileTransferReady { TransferId = Transfer, Accepted = true }, channelKey: realisticId);

        Assert.False(await push.WaitAsync(TimeSpan.FromSeconds(5)));

        var miss = Assert.Single(log.Warnings, w => w.Contains("No /ws/files channel", StringComparison.Ordinal));
        Assert.Contains("(none)", miss);
        Assert.Contains("(0)", miss);

        // REDACTED, like every other client id in this file. A diagnostic is exportable by design, so
        // a raw paired-device identifier here would leave the machine in a support bundle.
        Assert.DoesNotContain(realisticId, miss, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APushNobodyEverGetsReadyForGIVESUPAndLETSGOOFTHEFILE()
    {
        // THE TIMEOUT BRANCH, REACHABLE FOR THE FIRST TIME (RemEx-gfx3n, an item of RemEx-hn23). It
        // was unreachable in a test until now for a dull reason: the wait was an inline
        // TimeSpan.FromSeconds(30), so asserting anything about it cost half a minute per assertion
        // and nobody wrote the test. The seam is an internal init property rather than a constructor
        // parameter, so this costs nothing in the DI surface and production still waits thirty
        // seconds.
        //
        // BOTH HALVES MATTER AND THE SECOND IS THE ONE WORTH HAVING. Returning false is the visible
        // half. Releasing the file is the half that is silent when it breaks: a push that consented
        // and then went quiet would leave the source held open, and the user would find they could
        // not move, rename or delete their own file with nothing on screen to explain why.
        var manager = new TransferSessionManager(
                _log,
                Mock.Of<IFileTransferService>(),
                new SharedRootReadResolver(
                    Mock.Of<IFileTransferService>(),
                    Mock.Of<IFileTrustService>(),
                    new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance)),
                _staging.FullName)
        {
            ReadyTimeout = TimeSpan.FromMilliseconds(150),
        };

        var (push, _) = StartPush(manager);

        Assert.False(await push.WaitAsync(TimeSpan.FromSeconds(5)));

        // Opening it exclusively is the assertion. A leaked handle fails here and nowhere else -
        // File.Exists would pass either way, and so would deleting it on Linux.
        var source = Path.Combine(_staging.FullName, "push.bin");
        using var exclusive = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanRead);

        // AND THE CHANNEL WAS NEVER LOOKED UP, which is an ordering the code depends on and nothing
        // else states (RemEx-dwlzb). The phone opens its /ws/files channel in RESPONSE to the offer,
        // so a lookup done before the ready handshake finds nothing and fails the push for a reason
        // that is not the user's. No channel was ever registered here, so an implementation that
        // looked first would have logged the miss on the way past; a correct one never gets there.
        //
        // The absence is the assertion, which is only safe because the sibling test above proves
        // that warning DOES appear when the lookup genuinely happens and finds nothing. Without that
        // pair, this would pass just as happily against a renamed log message.
        Assert.DoesNotContain(_log.Warnings, w => w.Contains("No /ws/files channel", StringComparison.Ordinal));
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private TransferSessionManager NewManager() => NewManager(_log);

    private TransferSessionManager NewManager(CapturingLogger log)
    {
        var files = Mock.Of<IFileTransferService>();
        var resolver = new SharedRootReadResolver(
            files, Mock.Of<IFileTrustService>(), new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance));
        return new TransferSessionManager(log, files, resolver, _staging.FullName);
    }

    /// <summary>Starts a push to <see cref="Phone"/> and leaves it waiting on the ready.</summary>
    private (Task<bool> Push, SilentSocket Socket) StartPush(TransferSessionManager manager)
        => StartPush(manager, Phone, CancellationToken.None);

    private (Task<bool> Push, SilentSocket Socket) StartPush(
        TransferSessionManager manager, string clientId, CancellationToken ct)
    {
        var source = Path.Combine(_staging.FullName, "push.bin");
        if (!File.Exists(source)) File.WriteAllBytes(source, [1, 2, 3, 4]);

        var socket = new SilentSocket();
        return (manager.PushFileAsync(
            clientId, Transfer, source, "push.bin", offeredSize: 4, socket, ct), socket);
    }

    /// <summary>Records warning text, which is the only deterministic view of a refusal.</summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<TransferSessionManager>
    {
        /// <summary>
        /// A concurrent queue, not a List (review): PushFileAsync's continuation logs from a pool
        /// thread while the test thread is enumerating this, and List.Add racing an enumeration is
        /// undefined — the load-sensitive flake shape of RemEx-w7ei.
        /// </summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
                Warnings.Enqueue(formatter(state, exception));
        }
    }

    /// <summary>Accepts the offer frame and never answers, so the push stays parked on its ready.</summary>
    private sealed class SilentSocket : System.Net.WebSockets.WebSocket
    {
        /// <summary>
        /// Completes when the offer reaches the wire, which is AFTER the pending entry is registered.
        /// </summary>
        /// <remarks>
        /// Per instance, not static: a shared one completes once and then every later test would read
        /// it as already-signalled and race the registration it is supposed to wait for. The map
        /// itself is private, so this frame is the only observable edge — and PushFileAsync registers
        /// before it sends, deliberately, so the signal cannot fire early.
        /// </remarks>
        public TaskCompletionSource OfferSent { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override System.Net.WebSockets.WebSocketState State => System.Net.WebSockets.WebSocketState.Open;
        public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus s, string? d, CancellationToken c)
            => Task.CompletedTask;
        public override Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus s, string? d, CancellationToken c)
            => Task.CompletedTask;
        public override Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> b, CancellationToken c)
            => throw new NotSupportedException("nothing here reads");
        public override Task SendAsync(
            ArraySegment<byte> b, System.Net.WebSockets.WebSocketMessageType t, bool e, CancellationToken c)
        {
            OfferSent.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
