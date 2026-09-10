using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Security;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Drives the real <c>/ws/files</c> receive loop over a scripted socket, to prove that reusing ONE
/// buffer for every inbound frame does not corrupt the file being written (RemEx-8su9).
///
/// WHAT CHANGED AND WHY IT NEEDS THIS. Each inbound frame used to cost three copies: a growable
/// <c>MemoryStream</c>, its <c>ToArray()</c>, and then <c>payload.ToArray()</c> before the handler —
/// at the 256 KB frame cap, Large Object Heap allocations several times per frame. The loop now fills
/// one rented buffer per channel and passes a <see cref="ReadOnlyMemory{T}"/> VIEW of it straight
/// through.
///
/// That is only safe because of a property no compiler enforces: every consumer of a frame is awaited
/// before the loop can receive again, and nothing downstream retains the memory (<c>WriteChunkAsync</c>
/// awaits the stream write and appends to the hash synchronously). If a future change starts
/// background work over the payload, or stashes it, the bytes on disk go wrong with no exception and
/// no log line — the same silent shape that has reverted the capture-path pooling three times
/// (RemEx-lcp8). These tests are what turns that into a failure.
///
/// The <c>payload.ToArray()</c> that used to sit at the call site was never defending against this,
/// incidentally: it existed because <c>TryRead</c> yielded a <c>ReadOnlySpan</c>, and a
/// <c>ref struct</c> cannot cross an <c>await</c>.
/// </summary>
public sealed class FileChannelReceiveBufferTests
{
    private const string ClientId = "paired-android-device";
    private const string DestRoot = "root-a";

    /// <summary>
    /// Replays a script of complete binary messages, each optionally split into fragments, then
    /// reports Close. Overrides the <see cref="Memory{T}"/> receive overload because that is the one
    /// the loop calls.
    /// </summary>
    private sealed class ScriptedWebSocket : WebSocket
    {
        private readonly Queue<byte[]> _fragments = new();
        private readonly Queue<bool> _endOfMessage = new();
        private WebSocketState _state = WebSocketState.Open;

        /// <summary>Queues one complete message, split into <paramref name="fragmentCount"/> pieces.</summary>
        public void EnqueueMessage(byte[] message, int fragmentCount = 1)
        {
            var size = (int)Math.Ceiling(message.Length / (double)fragmentCount);
            for (var offset = 0; offset < message.Length; offset += size)
            {
                var take = Math.Min(size, message.Length - offset);
                _fragments.Enqueue(message.AsSpan(offset, take).ToArray());
                _endOfMessage.Enqueue(offset + take >= message.Length);
            }
        }

        /// <summary>
        /// Genuinely asynchronous ON PURPOSE. Returning a completed ValueTask would let the receive
        /// loop run start to finish without ever suspending, and a handler that was NOT awaited could
        /// then still finish before the next frame overwrote the buffer — so the reuse hazard these
        /// tests exist to detect would be invisible. Yielding makes the loop suspend at the receive,
        /// which is what a real socket does.
        /// </summary>
        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Yield();

            if (_fragments.Count == 0)
            {
                _state = WebSocketState.Closed;
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            var fragment = _fragments.Dequeue();
            var end = _endOfMessage.Dequeue();
            fragment.CopyTo(buffer.Span);
            return new ValueWebSocketReceiveResult(fragment.Length, WebSocketMessageType.Binary, end);
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType t, bool e, CancellationToken ct) =>
            Task.CompletedTask; // acks go nowhere; the file content is what these tests assert on
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken ct) =>
            throw new NotSupportedException("the loop uses the Memory overload");
    }

    private static TransferSessionManager NewManager(string stagingDir, FakeFileTransferService files)
    {
        var resolver = new SharedRootReadResolver(
            files, new Mock<IFileTrustService>().Object, new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance));
        return new TransferSessionManager(NullLogger<TransferSessionManager>.Instance, files, resolver, stagingDir);
    }

    private static FileTransferOffer Offer(string transferId, long size) => new()
    {
        TransferId = transferId,
        Mode = "upload",
        SourcePath = "/phone/DCIM/photo.bin",
        DestRoot = DestRoot,
        DestRelativePath = null,
        FileName = "photo.bin",
        Size = size,
        ResumeRequested = false,
    };

    private static byte[] DataFrame(string transferId, long offset, byte[] chunk, bool final) =>
        FileFrameCodec.Wrap(
            new FileFrameEnvelope
            {
                Kind = FileFrameKinds.Data,
                TransferId = transferId,
                Offset = offset,
                Length = chunk.Length,
                Final = final,
            },
            chunk);

    /// <summary>An Error frame: no payload, no offset, just the transferId (RemEx-0719).</summary>
    private static byte[] ErrorFrame(string transferId, string error) =>
        FileFrameCodec.Wrap(
            new FileFrameEnvelope
            {
                Kind = FileFrameKinds.Error,
                TransferId = transferId,
                Offset = 0,
                Length = 0,
                Final = true,
                Error = error,
            },
            ReadOnlySpan<byte>.Empty);

    private static byte[] DistinctBytes(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)(seed + (i % 97));
        return bytes;
    }

    [Theory]
    [InlineData(1)]   // one fragment per frame
    [InlineData(4)]   // fragmented — exercises reassembly into the reused buffer
    public async Task ConsecutiveFramesReusingOneBufferProduceTheExactFileBytes(int fragmentsPerFrame)
    {
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);

            // Deliberately DIFFERENT content per chunk, and descending sizes. If frame N+1's receive
            // overwrote the buffer before frame N had been written, the tail of the earlier chunk
            // would survive and the comparison below would fail — which a same-content payload, or
            // equal-sized chunks, could easily hide.
            //
            // THE CHUNK SIZES ARE LOAD-BEARING, so do not "simplify" them to small numbers. Detection
            // needs the handler to actually suspend before the loop receives again, and its only
            // yield point is PartialStream.WriteAsync — which completes SYNCHRONOUSLY unless the
            // write exceeds the partial's 64 KB FileStream buffer. The 200/120/50 KB chunks clear it;
            // the 7-byte final one does not, and is here to cover a short trailing frame rather than
            // to detect anything. Shrink these and the teeth vanish with no test failure to say so.
            //
            // Measured honestly: with one fragment per frame this reliably catches removal of the
            // await. With four fragments it does not — by the time the last fragment of the next
            // frame arrives, the previous write has usually finished — so that case is coverage of
            // reassembly, not of the reuse hazard.
            var chunks = new[]
            {
                DistinctBytes(200_000, 0x10),
                DistinctBytes(120_000, 0x40),
                DistinctBytes(50_000, 0x90),
                DistinctBytes(7, 0xF0),
            };
            var whole = chunks.SelectMany(c => c).ToArray();
            var tid = Guid.NewGuid().ToString("N");

            var acceptance = await mgr.BeginReceiveAsync(ClientId, Offer(tid, whole.Length), default);
            Assert.True(acceptance.Accepted);

            var ws = new ScriptedWebSocket();
            long offset = 0;
            for (var i = 0; i < chunks.Length; i++)
            {
                ws.EnqueueMessage(DataFrame(tid, offset, chunks[i], final: i == chunks.Length - 1), fragmentsPerFrame);
                offset += chunks[i].Length;
            }

            await mgr.RunChannelAsync(ClientId, ws, default);

            // Assert on the STAGING PARTIAL rather than finishing the transfer. RunChannelAsync's
            // finally suspends every receive session belonging to the client — correct behaviour, and
            // what keeps a partial resumable across a dropped socket — so calling CompleteReceiveAsync
            // after it would be testing teardown, not the buffer. In production the completion arrives
            // on the /ws control plane while this channel is still open.
            //
            // The partial is also the sharper measure: it is the bytes the receive loop actually wrote,
            // with nothing in between to launder a corrupted frame.
            var partial = Path.Combine(staging.FullName, tid + ".remexpart");
            Assert.True(File.Exists(partial), "the receive loop should have staged the frames it was fed");

            var staged = await File.ReadAllBytesAsync(partial);
            Assert.Equal(whole.Length, staged.Length);
            Assert.True(
                whole.AsSpan().SequenceEqual(staged),
                "a reused receive buffer must not change the bytes that were written — the first " +
                "difference is where a later frame overwrote one still in flight");
        }
        finally
        {
            staging.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AnOversizeFrameEndsTheChannelInsteadOfBufferingUnbounded()
    {
        // The bound moved from a MemoryStream length check into the fill loop, so it is worth pinning
        // that it still trips. A frame that slipped past it would parse and be written to the staging
        // partial as though it were legitimate.
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);

            var oversize = DistinctBytes(FileTransferLimits.DataPayloadBytes + 200_000, 0x20);
            var tid = Guid.NewGuid().ToString("N");

            // The declared size MUST accommodate the frame. An earlier version of this test declared
            // 16 bytes, and WriteChunkAsync's "overshot its declared size" guard then rejected the
            // frame for an unrelated reason — so the test passed even with the bound deleted. It was
            // an it-does-not-crash test wearing a bound-regression comment.
            Assert.True((await mgr.BeginReceiveAsync(ClientId, Offer(tid, oversize.Length), default)).Accepted);

            var ws = new ScriptedWebSocket();
            // MaxFrameBytes is DataPayloadBytes + 64 KB of header slack; comfortably past it. Note the
            // rented buffer is a 512 KB pool bucket, so the frame WOULD fit in it — only the explicit
            // bound rejects this, which is precisely what is under test.
            ws.EnqueueMessage(DataFrame(tid, 0, oversize, final: true), fragmentCount: 8);

            await mgr.RunChannelAsync(ClientId, ws, default);

            // The partial is created empty by BeginReceiveAsync, so any byte in it means the oversize
            // frame was accepted and written. This is the assertion the bound actually controls.
            var partial = Path.Combine(staging.FullName, tid + ".remexpart");
            Assert.True(File.Exists(partial));
            Assert.Equal(0, new FileInfo(partial).Length);
        }
        finally
        {
            staging.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
    }

    // --- RemEx-0719: the frame loop must CONSULT the ownership rule, not merely have one -----------
    // TransferSessionManagerTests covers the IsForeignTransfer predicate. That is the wrong half on
    // its own: the security property is that RunChannelAsync checks it BEFORE dispatching, and
    // deleting the call site leaves every predicate test green. Found in review of the fix itself.
    // These two drive the real loop over a scripted socket, so they fail if the check is removed,
    // bypassed, or hoisted out of the dispatch path by a later refactor.

    [Fact]
    public async Task AnIdentitylessChannelCannotWriteIntoAnotherClientsTransfer()
    {
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);

            // OVER 64 KB, and that is load-bearing: the partial's FileStream buffer is 64 KB, so a
            // smaller write never reaches disk and FileInfo.Length reads 0 whether the frame was
            // accepted or refused - a test that passes for the wrong reason. The first draft of this
            // test used 50 KB and stayed green with the guard's call site deleted, which is how the
            // hole was found. The same trap is documented on the buffer-reuse test above.
            var payload = DistinctBytes(200_000, 0x22);
            var tid = Guid.NewGuid().ToString("N");

            // A paired phone's transfer, registered to ITS client id.
            var acceptance = await mgr.BeginReceiveAsync(ClientId, Offer(tid, payload.Length), default);
            Assert.True(acceptance.Accepted);

            // The attacker: a loopback connection with a BLANK id, which RemEx-4u0d deliberately still
            // admits, presenting a Data frame for the victim's transferId. Before this guard the bytes
            // were written into the victim's partial with no identity of any kind required.
            var ws = new ScriptedWebSocket();
            ws.EnqueueMessage(DataFrame(tid, 0, payload, final: false));

            await mgr.RunChannelAsync(string.Empty, ws, default);

            var partial = Path.Combine(staging.FullName, tid + ".remexpart");
            var stagedLength = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            Assert.True(
                stagedLength == 0,
                $"a frame from a different client must not reach the staging partial, but {stagedLength} byte(s) were written");
        }
        finally
        {
            staging.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AnIdentitylessChannelCannotCancelAnotherClientsTransfer()
    {
        // The other reachable frame kind, and the cheaper attack: an Error frame needs no payload and
        // no offset - just the number. It reaches CancelReceive, which deletes the victim's partial.
        var staging = Directory.CreateTempSubdirectory();
        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var files = new FakeFileTransferService(dest.FullName);
            using var mgr = NewManager(staging.FullName, files);

            var payload = DistinctBytes(20_000, 0x55);
            var tid = Guid.NewGuid().ToString("N");

            await mgr.BeginReceiveAsync(ClientId, Offer(tid, payload.Length), default);

            // The victim writes some real bytes over its own channel first, so there is a partial to
            // destroy - otherwise the assertion below would pass whether or not the cancel landed.
            var victimWs = new ScriptedWebSocket();
            victimWs.EnqueueMessage(DataFrame(tid, 0, payload, final: false));
            await mgr.RunChannelAsync(ClientId, victimWs, default);

            var partial = Path.Combine(staging.FullName, tid + ".remexpart");
            Assert.True(File.Exists(partial), "the victim's own frame should have staged normally");

            var attackerWs = new ScriptedWebSocket();
            attackerWs.EnqueueMessage(ErrorFrame(tid, "cancelled by a process that does not own this transfer"));
            await mgr.RunChannelAsync(string.Empty, attackerWs, default);

            Assert.True(
                File.Exists(partial),
                "an Error frame from a different client must not cancel the transfer or delete its partial");
        }
        finally
        {
            staging.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
    }
}
