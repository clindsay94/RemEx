using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Tmds.DBus.Protocol;

namespace Remex.Agent.Services.Media;

/// <summary>
/// Reads the MPRIS media players on the session bus (RemEx-xx6xf).
/// </summary>
/// <remarks>
/// <para>
/// LINUX HAS NO "CURRENT SESSION", WHICH IS THE REAL DIFFERENCE FROM WINDOWS AND IS WORTH STATING
/// PLAINLY. Windows hands back the one session its own transport controls act on, so the reported
/// state and the key press provably concern the same player. MPRIS is a list of equals: every player
/// exports its own bus name and nobody arbitrates. Which one a media key actually reaches is decided
/// by the desktop environment, using rules this process cannot see. So the player chosen here is a
/// PREFERENCE — playing beats paused beats anything — and with two players open it can disagree with
/// whichever one the key lands on. That is a smaller wrong than reporting nothing, and it is the same
/// answer a user would give if asked "what is your PC playing".
/// </para>
/// <para>
/// EVERY READ RE-ENUMERATES THE BUS rather than caching player names. Players appear and vanish as
/// they are opened and closed, a cached name outlives its owner, and a request to a departed name is
/// an error that would read as "nothing is playing" — which is a wrong answer rather than a missing
/// one.
/// </para>
/// <para>
/// IT IS ALSO THE ARTWORK SOURCE (RemEx-vtorl). The <c>artUrl</c> arrives in the same
/// <c>GetAll</c> the reading did, so this class already holds the handle; see
/// <see cref="IMediaArtworkSource"/> for why fetching it is a separate interface and happens off the
/// poll tick.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class LinuxMediaSessionReader(ILogger<LinuxMediaSessionReader> logger)
    : IMediaSessionReader, IMediaArtworkSource, IAsyncDisposable
{
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerPath = "/org/mpris/MediaPlayer2";
    private const string MprisPrefix = "org.mpris.MediaPlayer2.";

    private DBusConnection? _conn;

    /// <summary>
    /// The anchor state for whichever player is currently winning the preference.
    /// </summary>
    /// <remarks>
    /// REBUILT WHEN THE WINNING BUS NAME CHANGES, because an anchor is only meaningful relative to
    /// one player's clock. Carrying Spotify's anchor over to VLC would have the tracker compare VLC's
    /// position against a prediction made from a different track in a different app — a divergence on
    /// the first tick, a re-anchor, and a broadcast that reports nothing anybody did.
    /// </remarks>
    private PlaybackAnchorTracker _anchors = new();
    private string? _anchorBusName;

    /// <summary>
    /// What the last winning reading said about its cover, for <see cref="ResolveArtworkAsync"/>.
    /// </summary>
    /// <remarks>
    /// KEPT RATHER THAN RE-READ, unlike the Windows path, because <c>artUrl</c> came back in the same
    /// <c>GetAll</c> that produced the reading — asking again would be a second round trip for a
    /// value already in hand. The title rides along for the same confirmation Windows performs by
    /// re-reading: if the player has moved on since, the cover belongs to a different track and must
    /// not be stored under this one's id.
    /// </remarks>
    private ArtworkHandle? _artwork;

    /// <summary>The cover handle from one reading: which player, which track, and where the art is.</summary>
    private sealed record ArtworkHandle(string BusName, string? Title, string? ArtUrl);

    /// <summary>One player's reading, plus the raw position the anchor tracker needs.</summary>
    /// <remarks>
    /// THE OBSERVED POSITION TRAVELS BESIDE THE STATE RATHER THAN ON IT, because only the winner of
    /// the preference loop may touch the tracker. Folding every player's position in would let a
    /// background player nobody is listening to re-anchor the one they are.
    /// </remarks>
    private sealed record PlayerReading(
        MediaPlaybackState State, long? ObservedPositionMs, string BusName, string? ArtUrl);

    /// <summary>
    /// Whether a session bus exists to ask at all.
    /// </summary>
    /// <remarks>
    /// READ FROM THE ENVIRONMENT RATHER THAN FROM A PROBE, because this is consulted when
    /// <c>HostCapabilities</c> is built and must not block a connecting client on a D-Bus round trip.
    /// It answers "could this host ever report" — a headless service account with no session bus
    /// never can — not "is a player running right now", which is what <see cref="ReadAsync"/> is for.
    /// </remarks>
    public bool IsSupported => !string.IsNullOrEmpty(DBusAddress.Session);

    public async Task<MediaPlaybackState> ReadAsync(CancellationToken ct)
    {
        try
        {
            var conn = await EnsureConnectionAsync(ct);
            if (conn is null)
            {
                return new MediaPlaybackState { Status = MediaPlaybackStatus.Unknown };
            }

            var players = await ListPlayersAsync(conn, ct);
            if (players.Count == 0)
            {
                return new MediaPlaybackState { Status = MediaPlaybackStatus.None };
            }

            PlayerReading? best = null;

            foreach (var player in players)
            {
                var reading = await ReadPlayerAsync(conn, player, ct);
                if (reading is null)
                {
                    continue;
                }

                // FIRST PLAYING WINS OUTRIGHT — there is nothing a later player could say that would
                // be a better answer to "what is this PC playing".
                if (reading.State.Status == MediaPlaybackStatus.Playing)
                {
                    return Anchor(reading);
                }

                best ??= reading;

                // A paused player beats a stopped or unreadable one, but keep looking for a playing
                // one. Ordering the preference here rather than sorting the list keeps this a single
                // pass over a bus that can change under it.
                if (reading.State.Status == MediaPlaybackStatus.Paused
                    && best.State.Status != MediaPlaybackStatus.Paused)
                {
                    best = reading;
                }
            }

            return best is null
                ? new MediaPlaybackState { Status = MediaPlaybackStatus.None }
                : Anchor(best);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The contract is "never throws". D-Bus surfaces a departed peer, a refused connection and
            // a malformed reply as different exception types, and this call reaches whatever media
            // player the user happens to have open, so the honest guard is the broad one.
            logger.LogDebug(ex, "Could not read MPRIS media state.");
            return new MediaPlaybackState { Status = MediaPlaybackStatus.Unknown };
        }
    }

    /// <summary>Every well-known name on the session bus that belongs to an MPRIS player.</summary>
    private static async Task<List<string>> ListPlayersAsync(DBusConnection conn, CancellationToken ct)
    {
        MessageBuffer buf;
        {
            var writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: "org.freedesktop.DBus",
                path: "/org/freedesktop/DBus",
                @interface: "org.freedesktop.DBus",
                member: "ListNames");
            buf = writer.CreateMessage();
        }

        var names = await conn.CallMethodAsync(
            buf,
            static (Message msg, object? state) => msg.GetBodyReader().ReadArrayOfString());

        ct.ThrowIfCancellationRequested();

        return [.. names.Where(n => n.StartsWith(MprisPrefix, StringComparison.Ordinal))];
    }

    /// <summary>
    /// One player's reading, or null when it will not answer.
    /// </summary>
    /// <remarks>
    /// NULL RATHER THAN AN UNKNOWN STATE, so that one uncooperative player cannot outrank a healthy
    /// one in the preference above. A player that has just quit is the common case here, and its
    /// silence should not become the PC's answer.
    /// </remarks>
    private async Task<PlayerReading?> ReadPlayerAsync(DBusConnection conn, string busName, CancellationToken ct)
    {
        try
        {
            MessageBuffer buf;
            {
                var writer = conn.GetMessageWriter();
                writer.WriteMethodCallHeader(
                    destination: busName,
                    path: PlayerPath,
                    @interface: "org.freedesktop.DBus.Properties",
                    member: "GetAll",
                    signature: "s");
                writer.WriteString(PlayerInterface);
                buf = writer.CreateMessage();
            }

            var properties = await conn.CallMethodAsync(
                buf,
                static (Message msg, object? state) => msg.GetBodyReader().ReadDictionaryOfStringToVariantValue());

            ct.ThrowIfCancellationRequested();

            if (!properties.TryGetValue("PlaybackStatus", out var rawStatus))
            {
                return null;
            }

            var status = TryGetString(rawStatus) switch
            {
                "Playing" => MediaPlaybackStatus.Playing,
                "Paused" => MediaPlaybackStatus.Paused,
                "Stopped" => MediaPlaybackStatus.Stopped,
                _ => MediaPlaybackStatus.Unknown,
            };

            var metadata = ReadMetadata(properties);

            // POSITION AND LENGTH COME OUT OF THE SAME GetAll, both in microseconds. Asking for
            // Position separately is the obvious alternative and is worse: it is a second round trip
            // per player per second, and it reads a moment later than the metadata it is paired with.
            var observedPositionMs = ReadPositionMs(properties);

            return new PlayerReading(
                new MediaPlaybackState
                {
                    Status = status,
                    Title = metadata.Title,
                    Artist = metadata.Artist,
                    SourceApp = busName[MprisPrefix.Length..],
                    DurationMs = metadata.DurationMs,
                },
                observedPositionMs,
                busName,
                metadata.ArtUrl);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "MPRIS player {BusName} did not answer.", busName);
            return null;
        }
    }

    /// <summary>Title, artist, length and cover URL out of the <c>Metadata</c> dictionary.</summary>
    /// <remarks>
    /// <para>
    /// <c>xesam:artist</c> IS AN ARRAY, unlike <c>xesam:title</c>, and joining rather than taking the
    /// first is the difference between "Simon &amp; Garfunkel" and half of it.
    /// </para>
    /// <para>
    /// <c>mpris:length</c> IS MICROSECONDS, and it is absent for a live stream — which is the point
    /// of the null rather than a zero. A radio station with no end has no progress bar to draw, and a
    /// zero duration would draw one that is permanently full.
    /// </para>
    /// </remarks>
    private (string? Title, string? Artist, long? DurationMs, string? ArtUrl) ReadMetadata(
        Dictionary<string, VariantValue> properties)
    {
        if (!properties.TryGetValue("Metadata", out var rawMetadata))
        {
            return (null, null, null, null);
        }

        try
        {
            var metadata = rawMetadata.GetDictionary<string, VariantValue>();

            var title = metadata.TryGetValue("xesam:title", out var rawTitle) ? TryGetString(rawTitle) : null;

            string? artist = null;
            if (metadata.TryGetValue("xesam:artist", out var rawArtist))
            {
                var names = rawArtist.GetArray<string>().Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
                artist = names.Length > 0 ? string.Join(", ", names) : null;
            }

            long? durationMs = null;
            if (metadata.TryGetValue("mpris:length", out var rawLength))
            {
                var microseconds = TryGetInt64(rawLength);
                durationMs = microseconds is > 0 ? microseconds.Value / 1000 : null;
            }

            var artUrl = metadata.TryGetValue("mpris:artUrl", out var rawArtUrl) ? TryGetString(rawArtUrl) : null;

            return (Blank(title), Blank(artist), durationMs, Blank(artUrl));
        }
        catch (Exception ex)
        {
            // Metadata is free-form: players ship keys with the wrong type, and a track's title is not
            // worth an exception escaping into the sampler loop.
            logger.LogTrace(ex, "MPRIS metadata was not in the expected shape.");
            return (null, null, null, null);
        }
    }

    /// <summary>
    /// The player's <c>Position</c> in milliseconds, or null when it will not say.
    /// </summary>
    /// <remarks>
    /// NULL RATHER THAN ZERO WHEN THE PROPERTY IS MISSING, because those mean opposite things to the
    /// anchor tracker: null keeps the anchor it has, zero claims the track restarted. Several players
    /// omit <c>Position</c> from <c>GetAll</c> entirely.
    /// </remarks>
    private static long? ReadPositionMs(Dictionary<string, VariantValue> properties)
    {
        if (!properties.TryGetValue("Position", out var rawPosition))
        {
            return null;
        }

        var microseconds = TryGetInt64(rawPosition);
        return microseconds is null ? null : Math.Max(0, microseconds.Value / 1000);
    }

    /// <summary>
    /// A microsecond count out of a variant, whatever numeric type the player chose to publish it as.
    /// </summary>
    /// <remarks>
    /// SWITCHED ON <see cref="VariantValue.Type"/> RATHER THAN TRIED, because this runs twice per
    /// player per poll tick (<c>mpris:length</c> and <c>Position</c>, 1 Hz): a player that publishes
    /// either as a type other than int64 would otherwise cost thrown exceptions on every tick for the
    /// life of the process. MPRIS SAYS INT64 AND PLAYERS DISAGREE — in practice <c>mpris:length</c>
    /// arrives as uint64 from some players and as a double from others. Insisting on the correct type
    /// would mean no progress bar at all for those, which is a worse answer than accepting the number
    /// they sent.
    /// </remarks>
    internal static long? TryGetInt64(VariantValue value) => value.Type switch
    {
        VariantValueType.Int64 => value.GetInt64(),
        VariantValueType.UInt64 => (long)value.GetUInt64(),
        VariantValueType.Int32 => value.GetInt32(),
        VariantValueType.UInt32 => value.GetUInt32(),
        VariantValueType.Int16 => value.GetInt16(),
        VariantValueType.UInt16 => value.GetUInt16(),
        VariantValueType.Byte => value.GetByte(),
        VariantValueType.Double => (long)value.GetDouble(),
        _ => null,
    };

    /// <summary>
    /// Folds the winning reading into the anchor tracker and stamps the result onto the state.
    /// </summary>
    /// <remarks>
    /// ONLY THE WINNER GETS HERE, and only once per read. This is also where the artwork handle is
    /// captured, so the cover that <see cref="ResolveArtworkAsync"/> fetches is always the one from
    /// the reading that was actually published.
    /// </remarks>
    private MediaPlaybackState Anchor(PlayerReading reading)
    {
        if (!string.Equals(_anchorBusName, reading.BusName, StringComparison.Ordinal))
        {
            _anchors = new PlaybackAnchorTracker();
            _anchorBusName = reading.BusName;
        }

        _artwork = new ArtworkHandle(reading.BusName, reading.State.Title, reading.ArtUrl);

        var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var trackKey = $"{reading.State.Title}|{reading.State.Artist}|{reading.State.SourceApp}";
        var (anchorPositionMs, anchorUtcMs) = _anchors.Observe(
            reading.State.Status, reading.ObservedPositionMs, nowUtcMs, trackKey);

        return reading.State with { AnchorPositionMs = anchorPositionMs, AnchorUtcMs = anchorUtcMs };
    }

    /// <inheritdoc />
    /// <remarks>
    /// THE TITLE IS CONFIRMED BEFORE ANYTHING IS FETCHED, for the reason the Windows reader
    /// re-requests its session: this runs off the poll tick, and art fetched for a track the user has
    /// already skipped past would be stored under the new track's id.
    /// </remarks>
    public Task<byte[]?> ResolveArtworkAsync(MediaPlaybackState state, CancellationToken ct)
    {
        var handle = _artwork;
        if (handle is null || !string.Equals(handle.Title, state.Title, StringComparison.Ordinal))
        {
            return Task.FromResult<byte[]?>(null);
        }

        return MediaArtworkFallback.FirstNonEmptyAsync(
            [
                c => LinuxArtworkFetcher.FetchAsync(handle.ArtUrl, c),
                c => ResolveDesktopIconAsync(handle.BusName, c),
            ],
            ct);
    }

    /// <summary>
    /// The player's own application icon, via its <c>DesktopEntry</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DesktopEntry</c> IS ON THE ROOT INTERFACE, not the Player one, so it is a separate
    /// <c>Get</c> — and it is made HERE rather than on the poll tick precisely because it is only
    /// needed when a track has no cover of its own. Once a second for every player on the bus would
    /// be paying for it always.
    /// </para>
    /// <para>
    /// THE VALUE IS A BASENAME, NOT A PATH. The search order below is the freedesktop one — the
    /// user's own applications directory first, then the XDG data dirs, then the system directory,
    /// then Flatpak's exports — so a locally installed override wins over the packaged copy, which is
    /// what the user sees in their launcher.
    /// </para>
    /// </remarks>
    private async Task<byte[]?> ResolveDesktopIconAsync(string busName, CancellationToken ct)
    {
        var conn = await EnsureConnectionAsync(ct);
        if (conn is null)
        {
            return null;
        }

        MessageBuffer buf;
        {
            var writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: busName,
                path: PlayerPath,
                @interface: "org.freedesktop.DBus.Properties",
                member: "Get",
                signature: "ss");
            writer.WriteString(RootInterface);
            writer.WriteString("DesktopEntry");
            buf = writer.CreateMessage();
        }

        var entry = await conn.CallMethodAsync(
            buf,
            static (Message msg, object? state) => msg.GetBodyReader().ReadVariantValue());

        ct.ThrowIfCancellationRequested();

        var name = Blank(TryGetString(entry));
        if (name is null)
        {
            return null;
        }

        var fileName = name.EndsWith(".desktop", StringComparison.Ordinal) ? name : name + ".desktop";
        var path = DesktopEntryDirectories()
            .Select(dir => Path.Combine(dir, fileName))
            .FirstOrDefault(File.Exists);

        return path is null ? null : MediaArtworkFallback.ExtractedIconBytes(path);
    }

    /// <summary>Where a <c>.desktop</c> file can live, in freedesktop precedence order.</summary>
    private static IEnumerable<string> DesktopEntryDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(dataHome))
        {
            yield return Path.Combine(dataHome, "applications");
        }

        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, ".local", "share", "applications");
        }

        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (!string.IsNullOrWhiteSpace(dataDirs))
        {
            foreach (var dir in dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                yield return Path.Combine(dir, "applications");
            }
        }

        yield return "/usr/share/applications";
        yield return "/var/lib/flatpak/exports/share/applications";
    }

    private static string? TryGetString(VariantValue value)
    {
        try
        {
            return value.GetString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// The session-bus connection, opened once and kept.
    /// </summary>
    /// <remarks>
    /// KEPT RATHER THAN OPENED PER READ, because this runs once a second for the life of the process
    /// and a D-Bus connect is a handshake, not a socket open. Null means the bus is unreachable, which
    /// <see cref="ReadAsync"/> reports as Unknown rather than as "nothing playing".
    /// </remarks>
    private async Task<DBusConnection?> EnsureConnectionAsync(CancellationToken ct)
    {
        if (_conn is not null)
        {
            return _conn;
        }

        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address))
        {
            return null;
        }

        try
        {
            var conn = new DBusConnection(address);
            await conn.ConnectAsync();
            ct.ThrowIfCancellationRequested();
            _conn = conn;
            return _conn;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not connect to the D-Bus session bus for MPRIS.");
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _conn?.Dispose();
        _conn = null;
        return ValueTask.CompletedTask;
    }
}
