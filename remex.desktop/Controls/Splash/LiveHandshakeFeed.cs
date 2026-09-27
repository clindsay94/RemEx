using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Remex.Branding;
using Remex.Desktop.Services;

namespace Remex.Desktop.Controls.Splash;

/// <summary>
/// What the Live Handshake splash is shown about the real startup on the PC (RemEx-8g6n0): the paired
/// phones (ghosts), which of them are actually linked right now, and when the embedded host came up.
/// Polled by <see cref="SkiaSplashControl"/> about every 100 ms on its own tick, and stamped on the
/// splash's own clock, so the snapshot's times line up with the frames that draw them.
/// </summary>
/// <remarks>
/// <para>
/// RESOLVED FRESH EVERY POLL (the <see cref="ViewModels.PhonePresenceMonitor"/> pattern, RemEx-n8xk).
/// The host publishes <c>App.EmbeddedHostServices</c> after it starts; caching a null from before then
/// would pin "host down" for the whole splash.
/// </para>
/// <para>
/// LINKED MEANS AUTHENTICATED AND NOT LOOPBACK. <see cref="IClientSessionSource.Snapshot"/> is already
/// authenticated-only; <see cref="PhonePresence.IsPhone"/> drops the loopback link, which is this PC's
/// own UI talking to its embedded host and never a phone (AGENTS.md).
/// </para>
/// <para>
/// NOTHING HERE MAY THROW INTO THE SPLASH. A source that fails reads as "not observed yet"; the director
/// then hands off by its cap regardless, so a broken host costs at most 2.6 s, never a stuck splash.
/// Each distinct failure (call site + exception type) is logged once as a warning with its exception:
/// the splash polls ten times a second, and a fault repeated on every poll would bury the log.
/// </para>
/// </remarks>
public sealed class LiveHandshakeFeed
{
    private static readonly Regex TabletName = new(@"\b(tab|tablet|ipad|pad)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly Func<IServiceProvider?> _hostServices;
    private readonly Func<int?> _hostPort;
    private readonly Func<ILogger?> _logger;
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);

    private float? _readyAt;
    private int? _port;
    private List<Peer>? _peers;
    private readonly Dictionary<string, float> _linkedAt = new(StringComparer.OrdinalIgnoreCase);
    private HandshakeSnapshot _last = HandshakeSnapshot.Empty;

    /// <summary>The production feed: the embedded host this process started.</summary>
    public LiveHandshakeFeed()
        : this(() => App.EmbeddedHostServices, () => App.OverrideHostPort,
               () => App.Services?.GetService(typeof(ILogger<LiveHandshakeFeed>)) as ILogger)
    {
    }

    /// <summary>Test seam: supply the host container, the port and (optionally) the logger.</summary>
    /// <remarks>The logger is resolved lazily, at the first failure: the app container may not be
    /// built yet when the splash starts, and a null resolved then must not stick.</remarks>
    public LiveHandshakeFeed(Func<IServiceProvider?> hostServices, Func<int?> hostPort, Func<ILogger?>? logger = null)
    {
        _hostServices = hostServices;
        _hostPort = hostPort;
        _logger = logger ?? (() => null);
    }

    /// <summary>Logs <paramref name="ex"/> once per call site and exception type.</summary>
    private void LogOnce(string site, Exception ex)
    {
        if (!_logged.Add(site + "|" + ex.GetType().FullName)) return;
        try
        {
            _logger()?.LogWarning(ex,
                "Live Handshake splash: {Site} failed; the splash treats it as not observed yet and still hands off by its cap",
                site);
        }
        catch (Exception)
        {
            // Logging must never be the thing that breaks the splash; there is nowhere left to report to.
        }
    }

    /// <summary>Reads the host now and returns the snapshot as of <paramref name="now"/> (splash seconds).</summary>
    public HandshakeSnapshot Poll(float now)
    {
        IServiceProvider? services;
        try { services = _hostServices(); }
        catch (Exception ex) { LogOnce("resolving the embedded host services", ex); services = null; }
        if (services is null) return _last;

        bool changed = false;
        if (_readyAt is null)
        {
            // Ready and Listening are the same moment on the PC: the embedded host's container is
            // published only once its listener has started (remex.agent Program.Main).
            _readyAt = now;
            try { _port = _hostPort(); }
            catch (Exception ex) { LogOnce("reading the listening port", ex); _port = null; }
            changed = true;
        }

        if (_peers is null)
        {
            _peers = ReadPairedPeers(services);
            changed = true;
        }

        changed |= ObserveSessions(services, now);
        if (!changed) return _last;
        _last = BuildSnapshot();
        return _last;
    }

    private List<Peer> ReadPairedPeers(IServiceProvider services)
    {
        var peers = new List<Peer>();
        try
        {
            if (services.GetService(typeof(IPairedDeviceSource)) is IPairedDeviceSource source)
            {
                foreach (var row in source.PairedDevices())
                {
                    var name = FirstNonBlank(row.NameOverride, row.DeviceName) ?? row.ClientId;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    peers.Add(new Peer(name.Trim(), row.DeviceName, KindOf(row.DeviceName ?? name)));
                }
            }
        }
        catch (Exception ex)
        {
            // A paired list that cannot be read shows as nothing paired; the splash still hands off.
            LogOnce("reading the paired devices", ex);
        }
        return peers;
    }

    /// <summary>
    /// Marks peers linked from the host's live sessions. A session is matched to a paired row by the
    /// name the device reports; a phone session with no matching row (an unusual state: a live link
    /// the paired list does not know by that name) is still shown, as its own peer.
    /// </summary>
    private bool ObserveSessions(IServiceProvider services, float now)
    {
        IReadOnlyList<ClientSession> sessions;
        try
        {
            if (services.GetService(typeof(IClientSessionSource)) is not IClientSessionSource source) return false;
            sessions = source.Snapshot();
        }
        catch (Exception ex)
        {
            LogOnce("reading the live sessions", ex);
            return false;
        }

        bool changed = false;
        foreach (var session in sessions)
        {
            if (!PhonePresence.IsPhone(session)) continue;
            var reported = session.DeviceName?.Trim();
            var peer = _peers!.Find(p => Matches(p, reported));
            if (peer is null)
            {
                var name = string.IsNullOrWhiteSpace(reported) ? session.RemoteAddress ?? "?" : reported;
                peer = new Peer(name, reported, KindOf(name));
                _peers.Add(peer);
                changed = true;
            }
            if (_linkedAt.TryAdd(peer.Name, now)) changed = true;
        }
        return changed;
    }

    private static bool Matches(Peer peer, string? reported) =>
        !string.IsNullOrWhiteSpace(reported)
        && (string.Equals(peer.DeviceName, reported, StringComparison.OrdinalIgnoreCase)
            || string.Equals(peer.Name, reported, StringComparison.OrdinalIgnoreCase));

    private HandshakeSnapshot BuildSnapshot()
    {
        var peers = _peers ?? new List<Peer>();
        var rows = new HandshakePeer[peers.Count];
        int target = -1;
        float? targetAt = null;
        for (int i = 0; i < peers.Count; i++)
        {
            float? linked = _linkedAt.TryGetValue(peers[i].Name, out var at) ? at : null;
            rows[i] = new HandshakePeer(peers[i].Name, peers[i].Kind, linked);
            // The target is the first phone that linked (spec: "a phone already connected").
            if (linked is { } l && (targetAt is null || l < targetAt))
            {
                target = i;
                targetAt = l;
            }
        }

        return new HandshakeSnapshot(
            Peers: rows,
            TargetIndex: target,
            TargetAt: targetAt,
            ReadyAt: _readyAt,
            ListeningAt: _readyAt,
            ListeningPort: _port is > 0 and <= 65535 ? _port : null,
            FailedAt: null);
    }

    /// <summary>
    /// The glyph for a device. Paired devices do not record a form factor, so this reads the name the
    /// device reports; anything that does not look like a tablet is drawn as a phone.
    /// </summary>
    internal static HandshakeDeviceKind KindOf(string? deviceName) =>
        deviceName is not null && TabletName.IsMatch(deviceName) ? HandshakeDeviceKind.Tablet : HandshakeDeviceKind.Phone;

    private static string? FirstNonBlank(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a : !string.IsNullOrWhiteSpace(b) ? b : null;

    private sealed record Peer(string Name, string? DeviceName, HandshakeDeviceKind Kind);
}
