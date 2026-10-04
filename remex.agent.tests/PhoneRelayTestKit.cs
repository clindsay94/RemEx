using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Services;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Security;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;
using Remex.Desktop.Services;

namespace Remex.Agent.Tests;

/// <summary>
/// A fake phone for the PC-browses-the-phone tests (RemEx-xt0af): its control socket, its /ws/files
/// channel, and the host-side registries a real phone's session would populate.
/// </summary>
/// <remarks>
/// Builds the REAL <see cref="ClientSessionRegistry"/>, <see cref="PairedClientRegistry"/>,
/// <see cref="TransferSessionManager"/> and <see cref="PhoneFileRelay"/>, so the security rules under
/// test are the production ones. Only the sockets are fake.
/// </remarks>
internal sealed class PhoneRelayTestKit : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("remex-phone-relay-");
    private readonly List<IDisposable> _sessions = [];

    public PhoneRelayTestKit(
        TimeSpan? requestTimeout = null,
        Action<TransferSessionManagerOptions>? configure = null,
        IStagedFilePromoter? promoter = null,
        TimeSpan? copyMoveTimeout = null)
    {
        Paired = new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, Path.Combine(_root.FullName, "paired.json"));
        Sessions = new ClientSessionRegistry();
        Devices = new FakePairedDeviceSource(Paired, Sessions);

        var files = new FakeFileTransferService(Directory.CreateDirectory(Path.Combine(_root.FullName, "shared")).FullName);
        var resolver = new SharedRootReadResolver(
            files, new Mock<IFileTrustService>().Object, new VolumeEnumerator(NullLogger<VolumeEnumerator>.Instance));

        var options = new TransferSessionManagerOptions();
        configure?.Invoke(options);
        Transfers = new TransferSessionManager(
            NullLogger<TransferSessionManager>.Instance,
            files,
            resolver,
            Directory.CreateDirectory(Path.Combine(_root.FullName, "staging")).FullName,
            queue: null,
            localPromoter: promoter ?? new MovingPromoter())
        {
            ReadyTimeout = options.ReadyTimeout,
            PeerIdleTimeout = options.PeerIdleTimeout,
            PeerVerdictTimeout = options.PeerVerdictTimeout,
            PeerProgressInterval = TimeSpan.FromMilliseconds(20),
        };

        Relay = new PhoneFileRelay(
            NullLogger<PhoneFileRelay>.Instance, Paired, Sessions, Devices, Transfers)
        {
            RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30),
            CopyMoveTimeout = copyMoveTimeout ?? TimeSpan.FromMinutes(30),
        };
    }

    public PairedClientRegistry Paired { get; }
    public ClientSessionRegistry Sessions { get; }
    public FakePairedDeviceSource Devices { get; }
    public TransferSessionManager Transfers { get; }
    public PhoneFileRelay Relay { get; }

    /// <summary>A scratch folder on "this PC" for downloads to land in.</summary>
    public string LocalFolder => Directory.CreateDirectory(Path.Combine(_root.FullName, "local")).FullName;

    /// <summary>
    /// A phone that paired and proved its id on a live control session. Returns its socket and the
    /// registration, which a test disposes to make the phone leave.
    /// </summary>
    public (FakePhoneSocket Socket, IDisposable Session) ConnectProvenPhone(string clientId, string? name = null, bool paired = true)
    {
        if (paired)
            Paired.RegisterClient(clientId, new byte[32]);

        var socket = new FakePhoneSocket();
        var session = Sessions.Register("192.168.1.50", socket);
        Sessions.Identify(session, clientId, name);
        Sessions.MarkAuthenticated(session, identityProven: true);
        Devices.SetName(clientId, name);
        _sessions.Add(session);
        return (socket, session);
    }

    /// <summary>
    /// A local process on 127.0.0.1 that named a paired phone's id: authenticated by construction,
    /// never proven (RemEx-4215).
    /// </summary>
    public FakePhoneSocket ConnectLoopbackClaiming(string clientId)
    {
        var socket = new FakePhoneSocket();
        var session = Sessions.Register("127.0.0.1", socket);
        Sessions.Identify(session, clientId, null);
        Sessions.MarkAuthenticated(session, identityProven: false);
        _sessions.Add(session);
        return socket;
    }

    public void Dispose()
    {
        foreach (var session in _sessions)
            session.Dispose();
        Relay.Dispose();
        Transfers.Dispose();
        try { _root.Delete(recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    /// <summary>The test seams on <see cref="TransferSessionManager"/> this kit sets.</summary>
    internal sealed class TransferSessionManagerOptions
    {
        public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan PeerIdleTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan PeerVerdictTimeout { get; set; } = TimeSpan.FromMinutes(5);
    }

    /// <summary>Lands the staging file with a plain move, like a same-volume promotion.</summary>
    private sealed class MovingPromoter : IStagedFilePromoter
    {
        public Task PromoteStagedFileToPathAsync(string stagingPath, string destination, CancellationToken ct)
        {
            File.Move(stagingPath, destination, overwrite: true);
            return Task.CompletedTask;
        }
    }
}

/// <summary>Paired devices, with IsOnline read from the real session registry.</summary>
internal sealed class FakePairedDeviceSource(PairedClientRegistry paired, ClientSessionRegistry sessions) : IPairedDeviceSource
{
    private readonly Dictionary<string, string?> _names = new(StringComparer.Ordinal);

    public void SetName(string clientId, string? name) => _names[clientId] = name;

    public IReadOnlyList<PairedDeviceRow> PairedDevices() =>
        [.. paired.PairedClientIds().Select(id => new PairedDeviceRow(
            id, _names.GetValueOrDefault(id), null, null, null, sessions.IsConnected(id)))];
}

/// <summary>
/// A phone's control socket: records every message the PC sends it, and lets a test answer.
/// </summary>
internal sealed class FakePhoneSocket : WebSocket
{
    private readonly Lock _gate = new();
    private readonly List<byte> _pending = [];
    private readonly List<RemexMessage> _messages = [];
    private WebSocketState _state = WebSocketState.Open;

    /// <summary>Called with each message the PC sends, on the sending thread.</summary>
    public Action<RemexMessage>? OnMessage { get; set; }

    public IReadOnlyList<RemexMessage> Messages
    {
        get { lock (_gate) return [.. _messages]; }
    }

    public IReadOnlyList<RemexMessage> MessagesOfType(string type) =>
        [.. Messages.Where(m => string.Equals(m.Type, type, StringComparison.Ordinal))];

    public void Close() => _state = WebSocketState.Closed;

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
    {
        RemexMessage? decoded = null;
        lock (_gate)
        {
            _pending.AddRange(buffer.AsSpan().ToArray());
            if (end)
            {
                var json = Encoding.UTF8.GetString([.. _pending]);
                _pending.Clear();
                decoded = JsonSerializer.Deserialize<RemexMessage>(json);
                if (decoded is not null) _messages.Add(decoded);
            }
        }

        if (decoded is not null)
            OnMessage?.Invoke(decoded);
        return Task.CompletedTask;
    }

    public override WebSocketState State => _state;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public override void Abort() => _state = WebSocketState.Aborted;
    public override void Dispose() { }
    public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
        => throw new NotSupportedException("the PC's reader loop is not part of these tests");
}

/// <summary>
/// A phone's /ws/files channel: frames the test queues are delivered to the PC's channel loop, and
/// frames the PC sends (acks, data) are decoded and recorded.
/// </summary>
internal sealed class FakeChannelSocket : WebSocket
{
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
    private readonly Lock _gate = new();
    private readonly List<(FileFrameEnvelope Envelope, byte[] Payload)> _sent = [];
    private WebSocketState _state = WebSocketState.Open;

    /// <summary>Called with each frame the PC sends, on the sending thread.</summary>
    public Action<FileFrameEnvelope, byte[]>? OnFrame { get; set; }

    public IReadOnlyList<(FileFrameEnvelope Envelope, byte[] Payload)> Sent
    {
        get { lock (_gate) return [.. _sent]; }
    }

    /// <summary>Queues one frame as if the phone had sent it.</summary>
    public void Deliver(FileFrameEnvelope envelope, ReadOnlySpan<byte> payload)
        => _inbound.Writer.TryWrite(FileFrameCodec.Wrap(envelope, payload));

    /// <summary>Ends the channel, the way a dropped phone socket does.</summary>
    public void Drop()
    {
        _state = WebSocketState.Closed;
        _inbound.Writer.TryComplete();
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        var frame = await ReadNextAsync(ct);
        if (frame is null)
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        frame.CopyTo(buffer.Array!, buffer.Offset);
        return new WebSocketReceiveResult(frame.Length, WebSocketMessageType.Binary, true);
    }

    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var frame = await ReadNextAsync(ct);
        if (frame is null)
            return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        frame.CopyTo(buffer);
        return new ValueWebSocketReceiveResult(frame.Length, WebSocketMessageType.Binary, true);
    }

    private async Task<byte[]?> ReadNextAsync(CancellationToken ct)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
    {
        var bytes = buffer.AsSpan().ToArray();
        if (FileFrameCodec.TryRead(new ReadOnlyMemory<byte>(bytes), out var envelope, out var payload) && envelope is not null)
        {
            var copy = payload.ToArray();
            lock (_gate) _sent.Add((envelope, copy));
            OnFrame?.Invoke(envelope, copy);
        }

        return Task.CompletedTask;
    }

    public override WebSocketState State => _state;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public override void Abort() => Drop();
    public override void Dispose() { }
    public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) { Drop(); return Task.CompletedTask; }
    public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
}
