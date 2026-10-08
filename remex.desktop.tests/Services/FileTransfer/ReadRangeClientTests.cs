using System;
using System.Collections.Generic;
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
/// <see cref="FileTransferClient.ReadRangeRemoteAsync"/>, the PC's side of live preview (2026-10-08 redesign):
/// what goes on the wire, which reply it takes, and that a host's refusal reaches the screen as the host's words.
/// </summary>
public class ReadRangeClientTests
{
    private sealed class ScriptedConnection : IFileTransferConnection
    {
        private readonly Func<RemexMessage, RemexMessage?> _reply;

        public ScriptedConnection(Func<RemexMessage, RemexMessage?> reply) => _reply = reply;

        public List<RemexMessage> Sent { get; } = [];

        public event Action<RemexMessage>? FileTransferMessageReceived;

        public Task SendAsync(RemexMessage message)
        {
            Sent.Add(message);
            if (_reply(message) is { } answer)
                FileTransferMessageReceived?.Invoke(answer);
            return Task.CompletedTask;
        }
    }

    private static RemexMessage Answer(string requestId, string? data = null, string? error = null) => new()
    {
        Type = MessageTypes.FileReadRangeResponse,
        FileReadRangeResponse = new FileReadRangeResponse
        {
            RequestId = requestId, Offset = 6, DataBase64 = data, FileSize = 10, Eof = true, ErrorMessage = error,
        },
    };

    [Fact]
    public async Task ARead_SendsTheRangeAsked_AndReturnsTheMatchingReply()
    {
        var connection = new ScriptedConnection(m =>
            Answer(m.FileReadRangeRequest!.RequestId, data: Convert.ToBase64String("6789"u8.ToArray())));
        using var client = new FileTransferClient(connection);

        var read = await client.ReadRangeRemoteAsync("root", "Logs/app.log", 0, 4, fromEnd: true, CancellationToken.None);

        var sent = connection.Sent.Should().ContainSingle().Subject;
        sent.Type.Should().Be(MessageTypes.FileReadRangeRequest);
        sent.FileReadRangeRequest!.RootId.Should().Be("root");
        sent.FileReadRangeRequest.RelativePath.Should().Be("Logs/app.log");
        sent.FileReadRangeRequest.Length.Should().Be(4);
        sent.FileReadRangeRequest.FromEnd.Should().BeTrue();
        read.Offset.Should().Be(6);
        Convert.FromBase64String(read.DataBase64!).Should().Equal("6789"u8.ToArray());
        read.Eof.Should().BeTrue();
    }

    [Fact]
    public async Task AReplyToADifferentRequest_IsNotTakenForThisOne()
    {
        // The host answers, but with someone else's request id. A client that matched on type alone would
        // hand this preview another file's bytes; it must keep waiting instead.
        var connection = new ScriptedConnection(_ => Answer("someone-elses-request", data: "AAAA"));
        using var client = new FileTransferClient(connection);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var act = async () => await client.ReadRangeRemoteAsync("root", "a.txt", 0, 4, fromEnd: false, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a reply carrying another request's id must never complete this read");
    }

    [Fact]
    public async Task AHostRefusal_ArrivesAsTheHostsOwnWords()
    {
        const string PhoneSaid = "That file isn't in a folder this phone shares, or it can't be read.";
        var connection = new ScriptedConnection(m => Answer(m.FileReadRangeRequest!.RequestId, error: PhoneSaid));
        using var client = new FileTransferClient(connection);

        var act = async () => await client.ReadRangeRemoteAsync("root", "a.txt", 0, 4, fromEnd: false, CancellationToken.None);

        (await act.Should().ThrowAsync<FileTransferHostException>()).Which.HostMessage.Should().Be(PhoneSaid);
    }
}
