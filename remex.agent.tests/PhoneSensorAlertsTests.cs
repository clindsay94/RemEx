using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services;
using Remex.Agent.Services.Alerts;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.Alerts;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The host's push of the PC's sensor alerts to phones (RemEx-pp4cm.12): every paired, proven,
/// connected phone is told; a loopback session that merely named a phone, an unpaired phone and a
/// phone that is gone are not; and a request from a phone reaches the desktop only when well formed.
/// </summary>
/// <remarks>
/// Builds the REAL <see cref="Remex.Agent.Services.ClientSessionRegistry"/> and
/// <see cref="Remex.Agent.Services.Security.PairedClientRegistry"/> through <see cref="PhoneRelayTestKit"/>,
/// so the push gate under test is the production one. Only the sockets are fake.
/// </remarks>
public sealed class PhoneSensorAlertsTests : IDisposable
{
    private readonly PhoneRelayTestKit _kit = new();
    private readonly PhoneSensorAlerts _alerts;
    private readonly FakeTime _time = new();

    public PhoneSensorAlertsTests()
    {
        _alerts = new PhoneSensorAlerts(
            _kit.Sessions, _kit.Paired, NullLogger<PhoneSensorAlerts>.Instance, _time);
    }

    /// <summary>
    /// A phone's control socket that decodes what the PC sends the way the real wire does
    /// (<see cref="MessageSerializer"/>, source-generated, camelCase), unlike the relay kit's fake,
    /// which reads with default reflection options and cannot see a nested payload's fields.
    /// </summary>
    private sealed class DecodingSocket : WebSocket
    {
        private readonly object _gate = new();
        private readonly List<RemexMessage> _messages = [];
        private WebSocketState _state = WebSocketState.Open;

        public IReadOnlyList<RemexMessage> Messages
        {
            get { lock (_gate) return [.. _messages]; }
        }

        public IReadOnlyList<RemexMessage> MessagesOfType(string type) =>
            [.. Messages.Where(m => string.Equals(m.Type, type, StringComparison.Ordinal))];

        public void Close() => _state = WebSocketState.Closed;

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            var message = MessageSerializer.Deserialize(buffer.AsSpan());
            if (message is not null)
            {
                lock (_gate) _messages.Add(message);
            }

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
            => throw new NotSupportedException("the reader loop is not part of these tests");
    }

    private readonly List<IDisposable> _sessions = [];

    private (DecodingSocket Socket, IDisposable Session) ConnectProvenPhone(string clientId, bool paired = true)
    {
        if (paired)
            _kit.Paired.RegisterClient(clientId, new byte[32]);

        var socket = new DecodingSocket();
        var session = _kit.Sessions.Register("192.168.1.50", socket);
        _kit.Sessions.Identify(session, clientId, null);
        _kit.Sessions.MarkAuthenticated(session, identityProven: true);
        _sessions.Add(session);
        return (socket, session);
    }

    private DecodingSocket ConnectLoopbackClaiming(string clientId)
    {
        var socket = new DecodingSocket();
        var session = _kit.Sessions.Register("127.0.0.1", socket);
        _kit.Sessions.Identify(session, clientId, null);
        _kit.Sessions.MarkAuthenticated(session, identityProven: false);
        _sessions.Add(session);
        return socket;
    }

    public void Dispose()
    {
        foreach (var session in _sessions) session.Dispose();
        _kit.Dispose();
    }

    private sealed class FakeTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    }

    private static SensorAlertFiredEvent Fired(string name = "CPU Package", double value = 92.4) => new()
    {
        SensorName = name,
        DisplayName = "CPU temperature",
        Value = value,
        Unit = "°C",
        Threshold = 90,
        Direction = AlertDirection.Above,
        Severity = AlertSeverity.Critical,
        FiredAtUtc = new DateTimeOffset(2026, 10, 4, 11, 59, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task AFiredAlertReachesEveryPairedProvenPhone()
    {
        var (first, _) = ConnectProvenPhone("phone-1");
        var (second, _) = ConnectProvenPhone("phone-2");

        await _alerts.PublishFiredAsync(Fired());

        foreach (var socket in new[] { first, second })
        {
            var message = Assert.Single(socket.MessagesOfType(MessageTypes.SensorAlertFired));
            Assert.Equal("CPU Package", message.SensorAlertFired!.SensorName);
            Assert.Equal(92.4, message.SensorAlertFired.Value);
            Assert.Equal(AlertSeverity.Critical, message.SensorAlertFired.Severity);
        }
    }

    [Fact]
    public async Task ALoopbackSessionThatNamedAPairedPhoneIsNeverTold()
    {
        ConnectProvenPhone("phone-1");
        var impostor = ConnectLoopbackClaiming("phone-1");

        await _alerts.PublishFiredAsync(Fired());
        await _alerts.PublishRulesAsync([new SensorAlertRule { SensorName = "CPU Package", Threshold = 90 }]);

        Assert.Empty(impostor.Messages);
    }

    [Fact]
    public async Task APhoneThatIsNotPairedIsNeverTold()
    {
        var (stranger, _) = ConnectProvenPhone("stranger", paired: false);
        var (owner, _) = ConnectProvenPhone("phone-1");

        await _alerts.PublishFiredAsync(Fired());
        await _alerts.PublishRulesAsync([new SensorAlertRule { SensorName = "CPU Package", Threshold = 90 }]);

        Assert.Empty(stranger.Messages);
        Assert.Single(owner.MessagesOfType(MessageTypes.SensorAlertFired));
        Assert.Single(owner.MessagesOfType(MessageTypes.SensorAlertRules));
    }

    [Fact]
    public async Task APhoneUnpairedSinceItConnectedIsNoLongerTold()
    {
        var (socket, _) = ConnectProvenPhone("phone-1");
        _kit.Paired.UnregisterClient("phone-1");

        await _alerts.PublishFiredAsync(Fired());

        Assert.Empty(socket.Messages);
    }

    [Fact]
    public async Task APhoneThatHasLeftIsNotTold()
    {
        var (socket, session) = ConnectProvenPhone("phone-1");
        session.Dispose();

        await _alerts.PublishFiredAsync(Fired());

        Assert.Empty(socket.Messages);
    }

    [Fact]
    public async Task APhoneWhoseSocketHasClosedDoesNotStopTheNextOneBeingTold()
    {
        var (gone, _) = ConnectProvenPhone("phone-1");
        var (here, _) = ConnectProvenPhone("phone-2");
        gone.Close();

        await _alerts.PublishFiredAsync(Fired());

        Assert.Empty(gone.Messages);
        Assert.Single(here.MessagesOfType(MessageTypes.SensorAlertFired));
    }

    /// <summary>A socket that is open but never takes a send: it waits until the send is cancelled.</summary>
    private sealed class StalledSocket : WebSocket
    {
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
            => await Task.Delay(Timeout.Infinite, ct);

        public override WebSocketState State => WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
            => throw new NotSupportedException("the reader loop is not part of these tests");
    }

    [Fact]
    public async Task AStalledPhoneDoesNotDelayTheOthersAndIsAbandonedAfterTheTimeout()
    {
        var alerts = new PhoneSensorAlerts(
            _kit.Sessions, _kit.Paired, NullLogger<PhoneSensorAlerts>.Instance, _time, sendTimeout: TimeSpan.FromSeconds(2));

        // The stalled phone connects first, so a one-after-another broadcast would reach it first and wait.
        _kit.Paired.RegisterClient("stalled", new byte[32]);
        var stalledSession = _kit.Sessions.Register("192.168.1.51", new StalledSocket());
        _kit.Sessions.Identify(stalledSession, "stalled", null);
        _kit.Sessions.MarkAuthenticated(stalledSession, identityProven: true);
        _sessions.Add(stalledSession);
        var (healthy, _) = ConnectProvenPhone("phone-1");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var publish = alerts.PublishFiredAsync(Fired());

        while (healthy.MessagesOfType(MessageTypes.SensorAlertFired).Count == 0 && started.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            await Task.Delay(10);
        }

        Assert.Single(healthy.MessagesOfType(MessageTypes.SensorAlertFired));
        Assert.False(publish.IsCompleted, "the stalled phone is still being waited on, but it is not holding the healthy one up");

        await publish.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(started.Elapsed >= TimeSpan.FromSeconds(1.5), "the stalled send was given its timeout, not abandoned at once");
    }

    [Fact]
    public async Task PublishingWithNoPhoneConnectedIsQuiet()
    {
        await _alerts.PublishFiredAsync(Fired());
        await _alerts.PublishRulesAsync([]);
    }

    [Fact]
    public async Task AnAlertThatCannotBeWrittenAsJsonIsNotSent()
    {
        var (socket, _) = ConnectProvenPhone("phone-1");

        await _alerts.PublishFiredAsync(Fired(value: double.NaN));
        await _alerts.PublishFiredAsync(Fired(name: "  "));
        await _alerts.PublishFiredAsync(Fired() with { Direction = (AlertDirection)5 });

        Assert.Empty(socket.Messages);
    }

    [Fact]
    public async Task RulesAreSentNormalizedWithARevisionThatOnlyGoesUp()
    {
        var (socket, _) = ConnectProvenPhone("phone-1");

        await _alerts.PublishRulesAsync(
        [
            new SensorAlertRule { SensorName = "CPU Package", Threshold = 90, Direction = AlertDirection.Above },
            new SensorAlertRule { SensorName = "cpu package", Threshold = 10 },
            new SensorAlertRule { SensorName = "Broken", Threshold = double.PositiveInfinity },
        ]);
        await _alerts.PublishRulesAsync([]);

        var sent = socket.MessagesOfType(MessageTypes.SensorAlertRules);
        Assert.Equal(2, sent.Count);
        var rule = Assert.Single(sent[0].SensorAlertRules!.Rules);
        Assert.Equal("CPU Package", rule.SensorName);
        Assert.Equal(90, rule.Threshold);
        Assert.Empty(sent[1].SensorAlertRules!.Rules);
        Assert.True(sent[1].SensorAlertRules!.Revision > sent[0].SensorAlertRules!.Revision);
        Assert.Equal(_time.GetUtcNow(), sent[0].SensorAlertRules!.UpdatedUtc);
    }

    // ── Requests from a phone ─────────────────────────────────────────────────

    [Fact]
    public void AWellFormedRequestReachesTheDesktop()
    {
        var seen = new List<PhoneSensorAlertRequest>();
        _alerts.PhoneRequested += seen.Add;

        _alerts.RequestFromPhone(new PhoneSensorAlertRequest(PhoneSensorAlertRequestKind.Get, "phone-1"));
        _alerts.RequestFromPhone(new PhoneSensorAlertRequest(
            PhoneSensorAlertRequestKind.Set, "phone-1",
            Change: new SensorAlertChange { SensorName = "CPU", Threshold = 80 }));
        _alerts.RequestFromPhone(new PhoneSensorAlertRequest(
            PhoneSensorAlertRequestKind.Remove, "phone-1", SensorName: "CPU"));

        Assert.Equal(
            [PhoneSensorAlertRequestKind.Get, PhoneSensorAlertRequestKind.Set, PhoneSensorAlertRequestKind.Remove],
            seen.Select(r => r.Kind));
    }

    [Fact]
    public void AMalformedRequestNeverReachesTheDesktop()
    {
        var seen = new List<PhoneSensorAlertRequest>();
        _alerts.PhoneRequested += seen.Add;

        _alerts.RequestFromPhone(new PhoneSensorAlertRequest(PhoneSensorAlertRequestKind.Set, "phone-1"));
        _alerts.RequestFromPhone(new PhoneSensorAlertRequest(
            PhoneSensorAlertRequestKind.Set, "phone-1",
            Change: new SensorAlertChange { SensorName = "CPU", Threshold = double.NaN }));
        _alerts.RequestFromPhone(new PhoneSensorAlertRequest(PhoneSensorAlertRequestKind.Remove, "phone-1", SensorName: " "));
        _alerts.RequestFromPhone(new PhoneSensorAlertRequest((PhoneSensorAlertRequestKind)9, "phone-1"));

        Assert.Empty(seen);
    }
}
