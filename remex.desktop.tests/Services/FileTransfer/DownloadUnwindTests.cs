using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services.FileTransfer;
using Xunit;

namespace Remex.Desktop.Tests.Services.FileTransfer;

/// <summary>
/// The two download-unwind cases not covered anywhere else: a host-reported failure, which needs the
/// inbound seam, and a faulted Start send, which does not (RemEx-qmnl).
///
/// BE PRECISE ABOUT WHAT THE SEAM UNBLOCKED, because an earlier draft of this file was not. It is
/// NOT true that the unwind path was untestable before: <c>FileTransferCancelOrderingTests</c> and
/// <c>FileTransferClientLeakTests</c> already exercise <c>DownloadAsync</c>'s failure exits, through
/// the pre-existing <c>OutboundSender</c>/<c>IWebSocketSender</c> seam and through failures that
/// happen before the connection is reached at all. What was genuinely impossible was the INBOUND
/// half: a C# event
/// can only be raised by the class that declares it, so no test could ever deliver a
/// <c>file_transfer_end</c> through a real <c>ConnectionViewModel</c>. That is the whole
/// justification for <see cref="IFileTransferConnection"/>, and it is enough on its own —
/// host-reported failure is the most common way a real download ends badly.
///
/// So this file holds only what is not already covered elsewhere:
///   • a host-reported failure (needs the seam — nothing else can deliver the end message);
///   • a faulted Start send (reachable before, but uncovered).
/// Cancellation ordering lives in <c>FileTransferCancelOrderingTests</c> and local-file-open leaks
/// in <c>FileTransferClientLeakTests</c>; duplicating either here would create two tests that have
/// to agree.
///
/// NOT COVERED, and the seam does not help: a flush failure at the END of a download yielding Failed
/// with the partial deleted rather than Done (the RemEx-owc3 <c>FlushAsync</c>). That needs a broken
/// <c>FileStream</c> handle, not a fake socket, so this seam cannot reach it. Filed as RemEx-04p8.
///
/// The assertions use BOTH counters deliberately. <c>PendingDownloadRegistrationCount</c> covers the
/// five dictionaries and <c>ActiveTransferCount</c> covers the idle-watchdog lease; they leak
/// independently, and asserting only the latter is how five sixths of a leak could return unnoticed
/// (RemEx-kdly).
/// </summary>
public sealed class DownloadUnwindTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "remex-download-unwind-" + Guid.NewGuid().ToString("N"));

    public DownloadUnwindTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort test cleanup */ }
    }

    private string PathFor(string name) => Path.Combine(_tempDir, name);

    /// <summary>
    /// Stands in for the connection. Each test supplies what happens on send, and can answer a Start
    /// with an end message — the transfer id is generated inside the client, so the fake has to read
    /// it back off the wire exactly as the host would.
    /// </summary>
    private sealed class FakeConnection : IFileTransferConnection
    {
        private readonly Func<RemexMessage, FakeConnection, Task> _onSend;

        public FakeConnection(Func<RemexMessage, FakeConnection, Task> onSend) => _onSend = onSend;

        public event Action<RemexMessage>? FileTransferMessageReceived;

        // No record of what was sent, deliberately: neither test asserts on the outbound wire, and
        // a Sent queue nobody reads invites the next reader to assume it is being checked. Wire
        // ordering is FileTransferCancelOrderingTests' job.
        public Task SendAsync(RemexMessage message) => _onSend(message, this);

        /// <summary>Delivers an inbound message, as the real connection's receive loop would.</summary>
        public void Deliver(RemexMessage message) => FileTransferMessageReceived?.Invoke(message);
    }

    private static void AssertFullyUnwound(FileTransferClient client, string localPath)
    {
        client.PendingDownloadRegistrationCount.Should().Be(
            0, "every one of the five download dictionaries must be reaped on every exit path");
        client.ActiveTransferCount.Should().Be(
            0, "the idle-watchdog lease is a separate registration and leaks independently of the dictionaries");
        File.Exists(localPath).Should().BeFalse(
            "a failed download must not leave a partial file under the caller's chosen name, where it " +
            "is indistinguishable from a complete one");
    }

    [Fact]
    public async Task AHostReportedFailureDeletesThePartialAndReapsEverything()
    {
        // THE CASE THE SEAM EXISTS FOR. The host answers the Start with a failure end — the ordinary
        // way a download goes wrong, and the one no test could reach before, because delivering an
        // inbound message meant raising an event owned by ConnectionViewModel.
        //
        // It is also the path where a partial once survived under the FINAL name because the delete
        // hit a sharing violation that was then swallowed, leaving a truncated file that looks
        // complete (RemEx-gyf4).
        var connection = new FakeConnection((message, self) =>
        {
            if (message.Type == MessageTypes.FileTransferStart)
            {
                self.Deliver(new RemexMessage
                {
                    Type = MessageTypes.FileTransferEnd,
                    FileTransferEnd = new FileTransferEnd
                    {
                        TransferId = message.FileTransferStart!.TransferId,
                        Success = false,
                        ErrorMessage = "source file vanished",
                    },
                });
            }
            return Task.CompletedTask;
        });
        using var client = new FileTransferClient(connection);
        var localPath = PathFor("host-failed.bin");

        var failure = await Assert.ThrowsAsync<FileTransferHostException>(() =>
            client.DownloadAsync("root", "remote/file.bin", localPath, null, CancellationToken.None));

        failure.Message.Should().Contain(
            "source file vanished", "the host's reason is what makes the error actionable for the user");
        AssertFullyUnwound(client, localPath);
    }

    [Fact]
    public async Task AFailedStartSendStillReapsEverything()
    {
        // The regression this guards: a throw from the Start send used to strand the transfer-end
        // waiter, the progress reporter, the channel and the hasher forever, with the writer task
        // blocked on a channel nobody would ever complete (RemEx-w9lj).
        //
        // Reachable before the seam — the existing IWebSocketSender fake could have faulted too —
        // but never actually covered.
        var connection = new FakeConnection((message, _) =>
            message.Type == MessageTypes.FileTransferStart
                ? Task.FromException(new IOException("socket dropped mid-request"))
                : Task.CompletedTask);
        using var client = new FileTransferClient(connection);
        var localPath = PathFor("send-failed.bin");

        await Assert.ThrowsAsync<IOException>(() =>
            client.DownloadAsync("root", "remote/file.bin", localPath, null, CancellationToken.None));

        AssertFullyUnwound(client, localPath);
    }
}
