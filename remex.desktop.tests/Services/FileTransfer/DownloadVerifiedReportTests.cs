using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Services.FileTransfer;

/// <summary>
/// The download path tells its row "Verified" only when the host sent a hash and it matched (2026-10-08 redesign).
/// A host that sends no hash proves nothing, and the row must stay "Done".
/// </summary>
public sealed class DownloadVerifiedReportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("remex-verified-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class ScriptedHost(Func<RemexMessage, Action<RemexMessage>, Task> onSend) : IFileTransferConnection
    {
        public event Action<RemexMessage>? FileTransferMessageReceived;

        public Task SendAsync(RemexMessage message) => onSend(message, m => FileTransferMessageReceived?.Invoke(m));
    }

    private sealed class RecordingSink : IProgress<TransferProgress>, ITransferVerificationSink
    {
        public string? Verified;
        public void Report(TransferProgress value) { }
        public void ReportVerifying() { }
        public void ReportVerified(string sha256Base64) => Verified = sha256Base64;
    }

    private static ScriptedHost HostSending(byte[] content, string? sha256Base64) => new((message, deliver) =>
    {
        if (message.Type == MessageTypes.FileTransferStart)
        {
            var id = message.FileTransferStart!.TransferId;
            _ = Task.Run(() =>
            {
                deliver(new RemexMessage
                {
                    Type = MessageTypes.FileTransferChunk,
                    FileTransferChunk = new FileTransferChunk { TransferId = id, Offset = 0, DataBase64 = Convert.ToBase64String(content) },
                });
                deliver(new RemexMessage
                {
                    Type = MessageTypes.FileTransferEnd,
                    FileTransferEnd = new FileTransferEnd { TransferId = id, Success = true, Sha256Base64 = sha256Base64 },
                });
            });
        }
        return Task.CompletedTask;
    });

    [Fact]
    public async Task AHostThatSentAMatchingHash_MakesTheRowVerified_WithThatHash()
    {
        var content = "hello"u8.ToArray();
        var hash = Convert.ToBase64String(SHA256.HashData(content));
        using var client = new FileTransferClient(HostSending(content, hash));
        var sink = new RecordingSink();

        await client.DownloadAsync("root", "a.txt", Path.Combine(_dir, "a.txt"), sink, CancellationToken.None);

        sink.Verified.Should().Be(hash);
    }

    [Fact]
    public async Task AHostThatSentNoHash_LeavesTheRowUnverified()
    {
        using var client = new FileTransferClient(HostSending("hello"u8.ToArray(), sha256Base64: null));
        var sink = new RecordingSink();

        await client.DownloadAsync("root", "a.txt", Path.Combine(_dir, "b.txt"), sink, CancellationToken.None);

        sink.Verified.Should().BeNull("nothing was compared, so nothing was verified");
    }
}
