using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Desktop.Services;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins that a download never sends the host a cancel for a transfer it has not been told about
/// (RemEx-mubp).
/// </summary>
/// <remarks>
/// <para>
/// <c>DownloadAsync</c> registers its cancellation callback before sending <c>FileTransferStart</c>,
/// and <c>CancellationToken.Register</c> runs the callback SYNCHRONOUSLY INLINE when the token is
/// already cancelled. So a download begun with a cancelled token put a <c>FileTransferCancel</c> on
/// the wire as its very first act, naming a transfer id the host had never heard of. The host
/// discards an unknown id, the <c>Start</c> follows, and nothing ever cancels it again — so the host
/// streams an entire file to a client that gave up before it asked. The local unwind was always
/// clean; the whole residual was on the wire, which is why the fix is about ORDER, not state.
/// </para>
/// <para>
/// THESE TESTS ONLY EXIST BECAUSE THE SEND SEAM WAS WIDENED TO COVER THEM. <c>SendGuardedAsync</c>
/// returns early when the socket is not open, so against a disconnected view model every send
/// silently no-ops: the pre-existing tests in this folder could observe leaked registrations but not
/// a single byte of what was, or was not, put on the wire. That is precisely how a bug whose entire
/// symptom is message ORDERING survived a green suite.
/// </para>
/// </remarks>
public class FileTransferCancelOrderingTests
{
    /// <summary>Records what reached the wire, in order, and can act when a message arrives.</summary>
    private sealed class RecordingSender(Action<RemexMessage>? onSend = null) : IWebSocketSender
    {
        private readonly List<RemexMessage> _sent = [];

        public IReadOnlyList<RemexMessage> Sent
        {
            get { lock (_sent) { return _sent.ToList(); } }
        }

        public Task SendAsync(RemexMessage message, CancellationToken ct)
        {
            lock (_sent) { _sent.Add(message); }
            onSend?.Invoke(message);
            return Task.CompletedTask;
        }
    }

    private static string TempDownloadPath() =>
        Path.Combine(Path.GetTempPath(), "remex-mubp-" + Guid.NewGuid().ToString("N") + ".bin");

    [Fact]
    public async Task ADownloadStartedWithACancelledTokenPutsNothingOnTheWire()
    {
        // THE BEAD. Not merely "the cancel comes after the start" — an already-cancelled request
        // should say nothing at all, because the host has no idea the transfer was ever wanted.
        var sender = new RecordingSender();
        var connection = new ConnectionViewModel { OutboundSender = sender };
        var client = new FileTransferClient(connection);
        var localPath = TempDownloadPath();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        try
        {
            var attempt = () => client.DownloadAsync("root-1", "remote/file.bin", localPath, progress: null, cts.Token);
            await attempt.Should().ThrowAsync<OperationCanceledException>();

            sender.Sent.Should().BeEmpty(
                "a transfer the host was never told about must not be announced OR retracted; before "
                + "the fix a FileTransferCancel went out naming an id the host had never seen");

            // The throw sits INSIDE the try precisely so the six registrations made just above it
            // are still reaped. Asserting it here because the existing leak tests cannot: all of
            // them fail at the FileStream open, which is ABOVE the registrations, so they prove
            // "registered nothing" and never "registered, then reaped".
            client.ActiveTransferCount.Should().Be(0, "the watchdog lease must not survive the throw");
            client.PendingDownloadRegistrationCount.Should().Be(0,
                "all five download dictionaries must be reaped even though nothing was ever sent");
            File.Exists(localPath).Should().BeFalse(
                "the destination file was created before the throw and must not be left behind");
        }
        finally
        {
            try { File.Delete(localPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task AnUploadStartedWithACancelledTokenPutsNothingOnTheWire()
    {
        var sender = new RecordingSender();
        var connection = new ConnectionViewModel { OutboundSender = sender };
        var client = new FileTransferClient(connection);
        var sourcePath = TempDownloadPath();
        await File.WriteAllBytesAsync(sourcePath, new byte[4096]);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        try
        {
            var attempt = () => client.UploadAsync(sourcePath, "root-1", "remote/file.bin", progress: null, cts.Token);
            await attempt.Should().ThrowAsync<OperationCanceledException>();

            sender.Sent.Should().BeEmpty("the host was never told this upload existed");
            client.ActiveTransferCount.Should().Be(0, "the watchdog lease must not survive the throw");
        }
        finally
        {
            try { File.Delete(sourcePath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task AnUploadCancelledMidChunkLoopTellsTheHostToStop()
    {
        // THE BEAD (RemEx-o5cz), and the realistic shape: the user stops a large upload while it is
        // still sending. Upload's registration only cancelled the local wait, so the host was told
        // nothing — and the host opens the destination with FileMode.Create and FileShare.None, so
        // the user's intended filename had already been TRUNCATED and locked. It reaps only when the
        // WebSocket session ends (there is no idle timeout), so that stub replaced the real file for
        // as long as the app stayed connected.
        //
        // CANCELLED FROM INSIDE THE SENDER, ON A SPECIFIC CHUNK, and that is the whole design of this
        // test. The first version used an 8 MB file and a 100 ms delay, assuming the loop would still
        // be running — measured, the entire file AND the FileTransferEnd were on the wire in ~22 ms,
        // so it tested the final await while its comment claimed the opposite. Cancelling on chunk 3
        // runs the registration callback inline on the sending thread with the loop provably still
        // in progress, needs no sleep, and cannot drift with machine speed.
        const int chunkSize = 65536;
        var chunksSeen = 0;
        using var cts = new CancellationTokenSource();

        var sender = new RecordingSender(message =>
        {
            if (message.Type == MessageTypes.FileTransferChunk && ++chunksSeen == 3) cts.Cancel();
        });

        var connection = new ConnectionViewModel { OutboundSender = sender };
        var client = new FileTransferClient(connection);
        var sourcePath = TempDownloadPath();
        await File.WriteAllBytesAsync(sourcePath, new byte[chunkSize * 8]);

        try
        {
            var attempt = () => client.UploadAsync(sourcePath, "root-1", "remote/file.bin", progress: null, cts.Token);
            await attempt.Should().ThrowAsync<OperationCanceledException>();

            var types = sender.Sent.Select(m => m.Type).ToList();
            types.Should().NotContain(MessageTypes.FileTransferEnd,
                "the loop must have been abandoned mid-flight — if the End went out this is testing "
                + "the final await instead, which is what the first version of this test did");
            types.Count(t => t == MessageTypes.FileTransferChunk).Should().BeLessThan(8,
                "cancelling on chunk 3 of 8 must stop the loop rather than let it drain");

            var cancels = sender.Sent.Where(m => m.Type == MessageTypes.FileTransferCancel).ToList();
            cancels.Should().ContainSingle(
                "a stopped upload must tell the host, or it holds the handle and the truncated file "
                + "until the connection itself drops");
            cancels[0].FileTransferCancel!.TransferId.Should().Be(
                sender.Sent.First(m => m.Type == MessageTypes.FileTransferStart).FileTransferStart!.TransferId);

            client.ActiveTransferCount.Should().Be(0, "the watchdog lease must be reaped");
            client.PendingDownloadRegistrationCount.Should().Be(0);
        }
        finally
        {
            try { File.Delete(sourcePath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task ACancelRaisedOnceTheTransferIsRunningStillReachesTheHost()
    {
        // THE PATH THAT MATTERS AND MUST NOT REGRESS: the user taps Cancel while chunks are
        // streaming. Suppressing the early cancel is only correct if this one still gets through —
        // otherwise the fix trades a harmless ignored message for a host that never learns to stop.
        //
        // THIS IS THE ONE THAT EXERCISES THE REGISTRATION CALLBACK. The sibling test above cancels
        // from inside the send, which runs the callback before the gate opens, so it proves the
        // window-closer instead. Deleting the emit from the callback leaves that test green and
        // fails this one — which is how the gap it was hiding was found.
        var startSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new RecordingSender(message =>
        {
            if (message.Type == MessageTypes.FileTransferStart) startSeen.TrySetResult();
        });

        var connection = new ConnectionViewModel { OutboundSender = sender };
        var client = new FileTransferClient(connection);
        var localPath = TempDownloadPath();
        using var cts = new CancellationTokenSource();

        try
        {
            var download = client.DownloadAsync("root-1", "remote/file.bin", localPath, progress: null, cts.Token);

            // Wait for the Start to have been SENT, then let the caller return from the send and
            // reach its wait. The signal does the ordering; the delay only covers the handful of
            // instructions between the send returning and the gate opening, which is why it can be
            // short without being fragile. Cancelling before that point is the sibling test's case,
            // and this test's injection proof is what confirms which path was taken.
            await startSeen.Task;
            await Task.Delay(100);
            await cts.CancelAsync();

            var attempt = () => download;
            await attempt.Should().ThrowAsync<OperationCanceledException>();

            var cancels = sender.Sent.Where(m => m.Type == MessageTypes.FileTransferCancel).ToList();
            cancels.Should().ContainSingle(
                "a cancel raised once the host knows the transfer must reach it, exactly once");
            cancels[0].FileTransferCancel!.TransferId.Should().Be(
                sender.Sent.First(m => m.Type == MessageTypes.FileTransferStart).FileTransferStart!.TransferId,
                "the cancel must name the transfer the start announced");
        }
        finally
        {
            try { File.Delete(localPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task ACancelRaisedWhileTheStartIsStillInFlightIsNotLost()
    {
        // NAMED FOR WHAT IT ACTUALLY REACHES, which is not what it first claimed. Cancelling from
        // inside the sender runs the registration callback synchronously on that same thread, while
        // the Start send has not yet returned — so the gate is still 0 and the CALLBACK EMITS
        // NOTHING. What this proves is the window-closer after the send, which exists for exactly
        // this interleaving: without it the cancel would be swallowed entirely and the host would
        // stream on with no retraction, the same end state as the bug reached from the other side.
        // The registration callback's own path is covered by the test below.
        RecordingSender? sender = null;
        using var cts = new CancellationTokenSource();

        sender = new RecordingSender(message =>
        {
            if (message.Type == MessageTypes.FileTransferStart) cts.Cancel();
        });

        var connection = new ConnectionViewModel { OutboundSender = sender };
        var client = new FileTransferClient(connection);
        var localPath = TempDownloadPath();

        try
        {
            var attempt = () => client.DownloadAsync("root-1", "remote/file.bin", localPath, progress: null, cts.Token);
            await attempt.Should().ThrowAsync<OperationCanceledException>();

            var types = sender.Sent.Select(m => m.Type).ToList();
            types.Should().Contain(MessageTypes.FileTransferStart);
            types.Should().Contain(MessageTypes.FileTransferCancel,
                "a cancel raised once the host knows the transfer must still reach it");
            types.IndexOf(MessageTypes.FileTransferCancel).Should()
                .BeGreaterThan(types.IndexOf(MessageTypes.FileTransferStart),
                    "the cancel is only meaningful to the host after the start that names the transfer");

            var cancels = sender.Sent.Where(m => m.Type == MessageTypes.FileTransferCancel).ToList();
            cancels.Should().ContainSingle("the gate must emit exactly one cancel, not one per path");
            cancels[0].FileTransferCancel!.TransferId.Should().Be(
                sender.Sent.First(m => m.Type == MessageTypes.FileTransferStart).FileTransferStart!.TransferId,
                "the cancel must name the transfer the start announced");
        }
        finally
        {
            try { File.Delete(localPath); } catch { /* best effort */ }
        }
    }
}
