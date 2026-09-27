using FluentAssertions;
using Remex.Branding;
using Remex.Desktop.Controls.Splash;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// What the PC's Live Handshake splash is told about the real startup (RemEx-8g6n0.2): paired phones
/// as peers, authenticated non-loopback sessions as links, and the host's container appearing as
/// ready/listening — resolved fresh on every poll.
/// </summary>
public class LiveHandshakeFeedTests
{
    [Fact]
    public void BeforeTheHostPublishesNothingIsObserved()
    {
        var feed = new LiveHandshakeFeed(() => null, () => 5005);

        var snap = feed.Poll(0.3f);

        snap.ReadyAt.Should().BeNull("no host container means not ready yet, never 'ready with nothing paired'");
        snap.Peers.Should().BeEmpty();
    }

    [Fact]
    public void TheHostIsResolvedFreshEachPollAndReadyIsTheFirstTimeItAppears()
    {
        IServiceProvider? host = null;
        var feed = new LiveHandshakeFeed(() => host, () => 5005);

        feed.Poll(0.1f).ReadyAt.Should().BeNull();
        host = new Services(new Sessions(), new Paired(Row("a", "Pixel 9 Pro")));
        var snap = feed.Poll(0.2f);
        feed.Poll(0.3f);

        snap.ReadyAt.Should().Be(0.2f, "a null from before the host started must not have been cached");
        snap.ListeningAt.Should().Be(0.2f, "on the PC the listener is up by the time the container is published");
        snap.ListeningPort.Should().Be(5005);
        feed.Poll(0.4f).ReadyAt.Should().Be(0.2f, "ready is stamped once");
    }

    [Fact]
    public void AnUnresolvablePortIsOmitted()
    {
        var feed = new LiveHandshakeFeed(() => new Services(new Sessions(), new Paired()), () => null);
        feed.Poll(0f).ListeningPort.Should().BeNull();
    }

    [Fact]
    public void PairedDevicesArePeersNamedByTheUsersNickname()
    {
        var paired = new Paired(
            Row("a", "SM-S938B", nameOverride: "Connor's S26"),
            Row("b", "Galaxy Tab S10"),
            Row("c", null));
        var feed = new LiveHandshakeFeed(() => new Services(new Sessions(), paired), () => 5005);

        var peers = feed.Poll(0f).Peers;

        peers.Select(p => p.Name).Should().Equal("Connor's S26", "Galaxy Tab S10", "c");
        peers[1].Kind.Should().Be(HandshakeDeviceKind.Tablet);
        peers[0].Kind.Should().Be(HandshakeDeviceKind.Phone);
        peers.Should().OnlyContain(p => p.LinkedAt == null);
    }

    [Fact]
    public void ALinkedPhoneBecomesTheTargetAtTheMomentItIsFirstSeen()
    {
        var sessions = new Sessions();
        var paired = new Paired(Row("a", "Pixel 9 Pro"), Row("b", "Galaxy S26 Ultra"));
        var feed = new LiveHandshakeFeed(() => new Services(sessions, paired), () => 5005);

        feed.Poll(0.1f).TargetIndex.Should().Be(-1);
        sessions.Current = new[] { new ClientSession("192.168.1.40:51000", "Galaxy S26 Ultra") };
        var snap = feed.Poll(0.6f);
        sessions.Current = Array.Empty<ClientSession>();
        var later = feed.Poll(0.7f);

        snap.TargetIndex.Should().Be(1);
        snap.TargetAt.Should().Be(0.6f);
        snap.TargetLinkedAt.Should().Be(0.6f);
        later.TargetLinkedAt.Should().Be(0.6f, "a link already shown is not un-shown mid-splash");
    }

    [Fact]
    public void TheLoopbackLinkIsNeverAPhone()
    {
        // This PC's own UI talks to its embedded host over loopback; that is not a phone (AGENTS.md).
        var sessions = new Sessions
        {
            Current = new[]
            {
                new ClientSession("127.0.0.1:5005", "Pixel 9 Pro"),
                new ClientSession("[::1]:5005", "Pixel 9 Pro"),
            },
        };
        var feed = new LiveHandshakeFeed(() => new Services(sessions, new Paired(Row("a", "Pixel 9 Pro"))), () => 5005);

        var snap = feed.Poll(0.5f);

        snap.TargetIndex.Should().Be(-1);
        snap.Peers.Should().ContainSingle().Which.LinkedAt.Should().BeNull();
    }

    [Fact]
    public void AFailingSourceReadsAsNotObservedRatherThanThrowing()
    {
        var feed = new LiveHandshakeFeed(() => new Services(new ThrowingSessions(), new ThrowingPaired()), () => 5005);

        var act = () => feed.Poll(0.2f);

        act.Should().NotThrow();
        feed.Poll(0.3f).Peers.Should().BeEmpty();
    }

    [Fact]
    public void EachDistinctFailureIsLoggedOnceWithItsException()
    {
        // The feed polls ten times a second; a fault on every poll must be one warning, not hundreds.
        var log = new CapturingLogger();
        var feed = new LiveHandshakeFeed(
            () => new Services(new ThrowingSessions(), new ThrowingPaired()), () => 5005, () => log);

        for (int i = 0; i < 20; i++) feed.Poll(i * 0.1f);

        log.Entries.Should().HaveCount(2, "one for the paired list, one for the live sessions");
        log.Entries.Should().OnlyContain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Exception != null);
        log.Entries.Select(e => e.Exception!.Message).Should().BeEquivalentTo(new[] { "store fault", "host fault" });
    }

    [Fact]
    public void AThrowingHostResolverIsLoggedOnceAndReadsAsNotReady()
    {
        var log = new CapturingLogger();
        var feed = new LiveHandshakeFeed(() => throw new InvalidOperationException("locator fault"), () => 5005, () => log);

        feed.Poll(0.1f).ReadyAt.Should().BeNull();
        feed.Poll(0.2f).ReadyAt.Should().BeNull();

        log.Entries.Should().ContainSingle().Which.Exception!.Message.Should().Be("locator fault");
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }

    private static PairedDeviceRow Row(string id, string? deviceName, string? nameOverride = null) =>
        new(id, deviceName, nameOverride, null, null, false);

    private sealed class Services(IClientSessionSource sessions, IPairedDeviceSource paired) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IClientSessionSource) ? sessions
            : serviceType == typeof(IPairedDeviceSource) ? paired
            : null;
    }

    private sealed class Sessions : IClientSessionSource
    {
        public IReadOnlyList<ClientSession> Current { get; set; } = Array.Empty<ClientSession>();
        public IReadOnlyList<ClientSession> Snapshot() => Current;
    }

    private sealed class Paired(params PairedDeviceRow[] rows) : IPairedDeviceSource
    {
        public IReadOnlyList<PairedDeviceRow> PairedDevices() => rows;
    }

    private sealed class ThrowingSessions : IClientSessionSource
    {
        public IReadOnlyList<ClientSession> Snapshot() => throw new InvalidOperationException("host fault");
    }

    private sealed class ThrowingPaired : IPairedDeviceSource
    {
        public IReadOnlyList<PairedDeviceRow> PairedDevices() => throw new InvalidOperationException("store fault");
    }
}
