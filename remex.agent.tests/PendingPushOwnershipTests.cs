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

    // ── Harness ────────────────────────────────────────────────────────────────

    private TransferSessionManager NewManager()
    {
        var files = Mock.Of<IFileTransferService>();
        var resolver = new SharedRootReadResolver(
            files, Mock.Of<IFileTrustService>(), new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance));
        return new TransferSessionManager(_log, files, resolver, _staging.FullName);
    }

    /// <summary>Starts a push to <see cref="Phone"/> and leaves it waiting on the ready.</summary>
    private (Task<bool> Push, SilentSocket Socket) StartPush(TransferSessionManager manager)
    {
        var source = Path.Combine(_staging.FullName, "push.bin");
        File.WriteAllBytes(source, [1, 2, 3, 4]);

        var socket = new SilentSocket();
        return (manager.PushFileAsync(
            Phone, Transfer, source, "push.bin", offeredSize: 4,
            socket, CancellationToken.None), socket);
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
